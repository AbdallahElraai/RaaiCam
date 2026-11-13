using System.Diagnostics;
using System.Threading.Channels;
using RaaiCam.Core.Phone;
using RaaiCam.Protocol;

namespace RaaiCam.Core.Hosting;

/// <summary>
/// Wires the virtual camera's consumer state, the USB phone link, the H.264 decoder and the frame sink together.
/// <para>
/// Threading model. One loop (<see cref="RunAsync"/>) owns all state transitions and reads a single unbounded
/// <c>Channel&lt;HostEvent&gt;</c>. Everything that happens elsewhere is only ever turned into an event and posted
/// (never awaited, never blocked on): the 100 ms ticker, session <c>Video</c>/<c>TelemetryReceived</c>/<c>Disconnected</c>
/// handlers, and keyframe requests raised by the decoder. Decoding happens on one dedicated thread fed by its own
/// <c>Channel</c>; that thread creates, uses and disposes every decoder, and writes decoded frames to the sink.
/// </para>
/// <para>
/// Phone discovery uses connect-retry polling (every 1 s while the consumer is active), not usbmux Listen, so
/// a dropped or reset daemon connection needs no special handling: the next attempt simply starts over.
/// </para>
/// </summary>
public sealed class Host
{
    static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);
    static readonly TimeSpan ConnectRetry = TimeSpan.FromSeconds(1);
    static readonly TimeSpan KeyframeMinInterval = TimeSpan.FromSeconds(1);
    static readonly TimeSpan IdleClose = TimeSpan.FromSeconds(10);
    static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(2);
    static readonly TimeSpan LogThrottle = TimeSpan.FromSeconds(5);
    const int MaxQueuedFrames = 3;

    readonly IConsumerSource _consumer;
    readonly IPhoneConnector _connector;
    readonly IDecoderFactory _decoders;
    readonly IFrameSink _sink;
    readonly Func<long> _qpcNow;
    readonly long _qpcFreq;
    readonly ILogSink _log;
    readonly TimeProvider _time;

    // Host loop mailbox, and the decode thread's own queue.
    readonly Channel<HostEvent> _events = Channel.CreateUnbounded<HostEvent>(new() { SingleReader = true });
    readonly Channel<DecodeItem> _decodeQ = Channel.CreateUnbounded<DecodeItem>(new() { SingleReader = true });

    // Loop-owned state (only touched from RunAsync's loop).
    bool _ready;                 // phone HELLO seen on the current link
    int _streamW, _streamH;      // size sent in the last start command; 0 = not streaming
    long? _idleSince;
    long? _lastConnectAttempt;
    long? _lastKeyframeSent;
    bool _keyframePending;
    uint? _lastSeq;
    int _linkNo;

    // Shared with other threads.
    volatile IPhoneLink? _link;
    Task _linkRun = Task.CompletedTask;
    Telemetry? _telemetry;
    int _reqW, _reqH;
    int _decodeBacklog;          // items in _decodeQ not yet taken by the decode thread (Channel.Count is unsupported here)
    int _epoch;                // bumped whenever the decode pipeline is reset
    long _wantSize;              // (w << 32) | h the sink may currently receive; 0 = none

    readonly SampleWindow _fpsIn;
    readonly SampleWindow _fpsOut;
    readonly SampleWindow _latency;
    readonly object _logGate = new();
    readonly Dictionary<string, long> _lastLog = new();

    /// <summary>Test hook: invoked after each poll tick has been fully processed.</summary>
    internal Action? OnTickProcessed { get; set; }

    public Host(IConsumerSource consumer, IPhoneConnector connector, IDecoderFactory decoders, IFrameSink sink,
        Func<long> qpcNow, ILogSink log, TimeProvider? time = null, long qpcFrequency = 0)
    {
        _consumer = consumer;
        _connector = connector;
        _decoders = decoders;
        _sink = sink;
        _qpcNow = qpcNow;
        _qpcFreq = qpcFrequency > 0 ? qpcFrequency : Stopwatch.Frequency;
        _log = log;
        _time = time ?? TimeProvider.System;
        _fpsIn = new SampleWindow(_qpcFreq);
        _fpsOut = new SampleWindow(_qpcFreq);
        _latency = new SampleWindow(_qpcFreq);
    }

    // ---- status ----

    public HostStatus Status
    {
        get
        {
            var link = _link;
            var tel = Volatile.Read(ref _telemetry);
            long now = _qpcNow();
            var lat = _latency.Snapshot(now);
            lat.Sort();
            return new HostStatus(
                Link: link is null ? "none" : "usb",
                Phone: link?.Phone?.Device,
                ConsumerWidth: Volatile.Read(ref _reqW),
                ConsumerHeight: Volatile.Read(ref _reqH),
                FpsIn: _fpsIn.Snapshot(now).Count,
                FpsOut: _fpsOut.Snapshot(now).Count,
                LatencyP50Ms: Percentile(lat, 0.50),
                LatencyP95Ms: Percentile(lat, 0.95),
                RttMs: link?.RttMs,
                BitrateMbps: tel is null ? null : tel.BitrateKbps / 1000.0,
                Thermal: tel?.Thermal,
                BatteryPercent: tel is null ? null : (int)Math.Round(tel.Battery * 100),
                Charging: tel?.Charging);
        }
    }

    static double? Percentile(List<double> sorted, double p) =>
        sorted.Count == 0 ? null : sorted[Math.Max(0, (int)Math.Ceiling(p * sorted.Count) - 1)];

    // ---- main loop ----

    public async Task RunAsync(CancellationToken ct)
    {
        var decodeThread = new Thread(DecodeLoop) { IsBackground = true, Name = "raaicam-decode" };
        decodeThread.Start();
        using var tickerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var ticker = TickerAsync(tickerCts.Token);
        HostLog("host started");
        try
        {
            var reader = _events.Reader;
            while (true)
            {
                HostEvent ev;
                try { ev = await reader.ReadAsync(ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }

                try { await HandleAsync(ev, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception e) { LogThrottled("loop", $"host loop error: {e.GetType().Name}: {e.Message}"); }

                if (ev is TickEvent) CallTickHook();
            }
        }
        finally
        {
            tickerCts.Cancel();
            try { await ticker.ConfigureAwait(false); } catch (OperationCanceledException) { }
            await ShutdownAsync().ConfigureAwait(false);
            _decodeQ.Writer.TryComplete();
            decodeThread.Join(TimeSpan.FromSeconds(5));
            HostLog("host stopped");
        }
    }

    void CallTickHook()
    {
        try { OnTickProcessed?.Invoke(); } catch (Exception) { }
    }

    async Task TickerAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                Post(new TickEvent());
                await Task.Delay(PollInterval, _time, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
    }

    void Post(HostEvent ev) => _events.Writer.TryWrite(ev);

    /// <summary>Test hook: completes once every event posted before this call has been handled by the loop.</summary>
    internal Task FlushAsync()
    {
        var b = new BarrierEvent();
        Post(b);
        return b.Done.Task;
    }

    async Task HandleAsync(HostEvent ev, CancellationToken ct)
    {
        switch (ev)
        {
            case TickEvent:
                PollConsumer();
                await ReconcileAsync(ct).ConfigureAwait(false);
                break;
            case VideoEvent v when ReferenceEquals(v.Link, _link):
                await OnVideoAsync(v.Frame).ConfigureAwait(false);
                break;
            case TelemetryEvent t when ReferenceEquals(t.Link, _link):
                Volatile.Write(ref _telemetry, t.Telemetry);
                break;
            case DisconnectedEvent d when ReferenceEquals(d.Link, _link):
                HostLog("phone link closed: " + d.Reason);
                await TeardownLinkAsync().ConfigureAwait(false);
                break;
            case KeyframeWantedEvent:
                await SendKeyframeAsync().ConfigureAwait(false);
                break;
            case BarrierEvent b:
                b.Done.TrySetResult();
                break;
        }
    }

    // ---- consumer polling and state reconciliation ----

    bool _active;
    int _wantW, _wantH;

    void PollConsumer()
    {
        ConsumerRequest? c = null;
        try { c = _consumer.Read(); }
        catch (Exception e) { LogThrottled("consumer", $"consumer read failed: {e.GetType().Name}: {e.Message}"); }
        _active = c is { Active: true, Width: > 0, Height: > 0 };
        _wantW = _active ? c!.Value.Width : 0;
        _wantH = _active ? c!.Value.Height : 0;
        Volatile.Write(ref _reqW, _wantW);
        Volatile.Write(ref _reqH, _wantH);
    }

    async Task ReconcileAsync(CancellationToken ct)
    {
        long now = _time.GetTimestamp();
        if (_active)
        {
            _idleSince = null;
            if (_link is null && (_lastConnectAttempt is not { } last || _time.GetElapsedTime(last, now) >= ConnectRetry))
                await TryConnectAsync(now, ct).ConfigureAwait(false);

            // Only talk to the phone after its HELLO (Phone != null); the session's own 5 s timeout ends a silent link.
            if (_link is { } link && !_ready && link.Phone is not null)
            {
                _ready = true;
                HostLog($"phone ready: {link.Phone.Device}");
            }
            if (_link is not null && _ready && (_streamW != _wantW || _streamH != _wantH))
                await StartStreamingAsync(_wantW, _wantH).ConfigureAwait(false);
            if (_keyframePending) await SendKeyframeAsync().ConfigureAwait(false);
        }
        else
        {
            if (_streamW != 0) await StopStreamingAsync().ConfigureAwait(false);
            if (_link is null) _idleSince = null;
            else
            {
                _idleSince ??= now;
                if (_time.GetElapsedTime(_idleSince.Value, now) >= IdleClose)
                {
                    HostLog("consumer idle for 10 s, closing phone session");
                    await TeardownLinkAsync().ConfigureAwait(false);
                }
            }
        }
    }

    async Task TryConnectAsync(long now, CancellationToken ct)
    {
        _lastConnectAttempt = now;
        IPhoneLink? link;
        try { link = await _connector.ConnectAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e)
        {
            LogThrottled("connect", $"connect failed: {e.GetType().Name}: {e.Message}");
            return;
        }
        if (link is null) return; // no phone attached; retry in 1 s

        int no = ++_linkNo;
        link.Video += f => Post(new VideoEvent(link, f));
        link.TelemetryReceived += t => Post(new TelemetryEvent(link, t));
        link.Disconnected += r => Post(new DisconnectedEvent(link, r));
        _ready = false;
        _link = link;
        _linkRun = link.RunAsync(CancellationToken.None);
        HostLog($"phone link #{no} up");
    }

    async Task StartStreamingAsync(int w, int h)
    {
        var link = _link!;
        bool restart = _streamW != 0;
        // Fresh pipeline first so frames of the old size can no longer reach the sink.
        ResetDecoder(w, h, link);
        _lastSeq = null;
        _keyframePending = false;
        _lastKeyframeSent = null;
        if (restart)
        {
            HostLog($"consumer size changed to {w}x{h}, restarting stream");
            await SafeAsync(link.StopAsync(), "stop").ConfigureAwait(false);
        }
        _streamW = w; _streamH = h;
        HostLog($"start {w}x{h}");
        await SafeAsync(link.StartAsync(w, h), "start").ConfigureAwait(false);
    }

    async Task StopStreamingAsync()
    {
        var link = _link;
        _streamW = _streamH = 0;
        ResetDecoder(0, 0, null);
        _keyframePending = false;
        HostLog("consumer inactive, stop");
        if (link is not null) await SafeAsync(link.StopAsync(), "stop").ConfigureAwait(false);
    }

    async Task TeardownLinkAsync()
    {
        var link = _link;
        if (link is null) return;
        _link = null;
        _ready = false;
        _streamW = _streamH = 0;
        _keyframePending = false;
        _lastSeq = null;
        Volatile.Write(ref _telemetry, null);
        _lastConnectAttempt = _time.GetTimestamp(); // retry no sooner than 1 s from now
        ResetDecoder(0, 0, null);
        // Safe here: we are on the host loop, never inside a session event handler.
        try { await link.DisposeAsync().ConfigureAwait(false); } catch (Exception) { }
        try { await _linkRun.ConfigureAwait(false); } catch (Exception) { }
        _linkRun = Task.CompletedTask;
    }

    async Task ShutdownAsync()
    {
        var link = _link;
        if (link is null) return;
        if (_streamW != 0)
        {
            using var cts = new CancellationTokenSource();
            var stop = SafeAsync(link.StopAsync(), "stop");
            var timeout = Task.Delay(StopTimeout, _time, cts.Token);
            await Task.WhenAny(stop, timeout).ConfigureAwait(false);
            cts.Cancel();
        }
        await TeardownLinkAsync().ConfigureAwait(false);
    }

    async Task SafeAsync(Task t, string what)
    {
        try { await t.ConfigureAwait(false); }
        catch (Exception e) { LogThrottled("cmd-" + what, $"{what} command failed: {e.GetType().Name}: {e.Message}"); }
    }

    // ---- video path (host loop side) ----

    async Task OnVideoAsync(VideoFrame f)
    {
        if (_streamW == 0) return;
        _fpsIn.Add(_qpcNow());
        if (_lastSeq is { } last && f.Seq != unchecked(last + 1))
            await SendKeyframeAsync().ConfigureAwait(false); // gap: the phone dropped frames, refs may be broken
        _lastSeq = f.Seq;
        EnqueueDecode(new DecodeItem(Volatile.Read(ref _epoch), 0, 0, _link, f));
    }

    /// <summary>Keyframe request, at most one per second; a throttled request is remembered and sent on a later tick.</summary>
    async Task SendKeyframeAsync()
    {
        var link = _link;
        if (link is null || _streamW == 0) { _keyframePending = false; return; }
        long now = _time.GetTimestamp();
        if (_lastKeyframeSent is { } t && _time.GetElapsedTime(t, now) < KeyframeMinInterval)
        {
            _keyframePending = true;
            return;
        }
        _keyframePending = false;
        _lastKeyframeSent = now;
        await SafeAsync(link.RequestKeyframeAsync(), "keyframe").ConfigureAwait(false);
    }

    /// <summary>Tells the decode thread to dispose its decoder and (for w &gt; 0) create a new one. Never blocks.</summary>
    void ResetDecoder(int w, int h, IPhoneLink? link)
    {
        Volatile.Write(ref _wantSize, w > 0 ? Pack(w, h) : 0);
        int epoch = Interlocked.Increment(ref _epoch);
        EnqueueDecode(new DecodeItem(epoch, w, h, link, null));
    }

    void EnqueueDecode(DecodeItem item)
    {
        Interlocked.Increment(ref _decodeBacklog);
        _decodeQ.Writer.TryWrite(item);
    }

    static long Pack(int w, int h) => ((long)w << 32) | (uint)h;

    // ---- decode thread ----

    void DecodeLoop()
    {
        var reader = _decodeQ.Reader;
        IVideoDecoder? dec = null;
        int dw = 0, dh = 0, decEpoch = -1;
        byte[] buf = Array.Empty<byte>();
        bool needKey = false;
        try
        {
            while (reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
            {
                while (reader.TryRead(out var item))
                {
                    Interlocked.Decrement(ref _decodeBacklog);
                    if (item.Frame is null)
                    {
                        dec?.Dispose();
                        dec = null; dw = dh = 0; needKey = false;
                        decEpoch = item.Epoch;
                        if (item.Width > 0)
                        {
                            try
                            {
                                dec = _decoders.Create(item.Width, item.Height);
                                dec.KeyframeNeeded += () => Post(new KeyframeWantedEvent());
                                dw = item.Width; dh = item.Height;
                                buf = new byte[dw * dh * 3 / 2];
                            }
                            catch (Exception e)
                            {
                                dec = null;
                                LogThrottled("decoder-create", $"decoder create failed: {e.GetType().Name}: {e.Message}");
                            }
                        }
                        continue;
                    }

                    if (dec is null || item.Epoch != decEpoch || item.Epoch != Volatile.Read(ref _epoch)) continue;
                    var frame = item.Frame;

                    // Back-pressure: never let latency grow. If more than 3 frames are waiting, drop the oldest
                    // non-keyframes and ignore the rest until a keyframe arrives, asking the phone for one.
                    bool behind = Volatile.Read(ref _decodeBacklog) + 1 > MaxQueuedFrames;
                    if (!frame.Keyframe && (behind || needKey))
                    {
                        needKey = true;
                        Post(new KeyframeWantedEvent());
                        continue;
                    }
                    if (frame.Keyframe) needKey = false;

                    try
                    {
                        var r = dec.Decode(frame.AccessUnit.Span, unchecked((long)(frame.CaptureUs * 10)), buf, out int stride);
                        if (r == VideoDecodeResult.Error)
                        {
                            LogThrottled("decode", "decode error (waiting for keyframe)");
                        }
                        else if (r == VideoDecodeResult.Frame &&
                                 item.Epoch == Volatile.Read(ref _epoch) && Volatile.Read(ref _wantSize) == Pack(dw, dh))
                        {
                            long qpc = _qpcNow();
                            if (_sink.WriteFrame(buf, dw, dh, stride, qpc))
                            {
                                _fpsOut.Add(qpc);
                                if (item.Link?.ToPcUs(frame.CaptureUs) is { } capturePcUs)
                                    _latency.Add(qpc, (qpc * (1_000_000.0 / _qpcFreq) - capturePcUs) / 1000.0);
                            }
                        }
                    }
                    catch (Exception e)
                    {
                        LogThrottled("decode-ex", $"decode/write threw: {e.GetType().Name}: {e.Message}");
                    }
                }
            }
        }
        finally
        {
            try { dec?.Dispose(); } catch (Exception) { }
        }
    }

    // ---- logging ----

    void HostLog(string line)
    {
        try { _log.Write("host", line); } catch (Exception) { }
    }

    void LogThrottled(string key, string line)
    {
        long now = _time.GetTimestamp();
        lock (_logGate)
        {
            if (_lastLog.TryGetValue(key, out var t) && _time.GetElapsedTime(t, now) < LogThrottle) return;
            _lastLog[key] = now;
        }
        HostLog(line);
    }

    // ---- types ----

    abstract record HostEvent;
    sealed record TickEvent : HostEvent;
    sealed record VideoEvent(IPhoneLink Link, VideoFrame Frame) : HostEvent;
    sealed record TelemetryEvent(IPhoneLink Link, Telemetry Telemetry) : HostEvent;
    sealed record DisconnectedEvent(IPhoneLink Link, string Reason) : HostEvent;
    sealed record KeyframeWantedEvent : HostEvent;
    sealed record BarrierEvent : HostEvent
    {
        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>Frame == null means "reset": dispose the decoder and, if Width &gt; 0, create one at that size.</summary>
    readonly record struct DecodeItem(int Epoch, int Width, int Height, IPhoneLink? Link, VideoFrame? Frame);

    /// <summary>Timestamped samples of the last second (count = per-second rate).</summary>
    sealed class SampleWindow(long window)
    {
        readonly object _g = new();
        readonly Queue<(long T, double V)> _q = new();

        public void Add(long t, double v = 1) { lock (_g) { _q.Enqueue((t, v)); Prune(t); } }

        public List<double> Snapshot(long now)
        {
            lock (_g)
            {
                Prune(now);
                return _q.Select(x => x.V).ToList();
            }
        }

        void Prune(long now)
        {
            while (_q.Count > 0 && now - _q.Peek().T > window) _q.Dequeue();
        }
    }
}
