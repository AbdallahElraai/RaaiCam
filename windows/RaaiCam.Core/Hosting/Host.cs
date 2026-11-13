using System.Diagnostics;
using System.Threading.Channels;
using RaaiCam.Core.Phone;
using RaaiCam.Protocol;

namespace RaaiCam.Core.Hosting;

/// <summary>
/// Wires the virtual camera's consumer state, the phone links (USB and Wi-Fi, see <see cref="LinkManager"/>), the H.264
/// decoder and the frame sink together.
/// <para>
/// Threading model. One loop (<see cref="RunAsync"/>) owns all state transitions and reads a single unbounded
/// <c>Channel&lt;HostEvent&gt;</c>. Everything that happens elsewhere is only ever turned into an event and posted
/// (never awaited, never blocked on): the 100 ms ticker, session <c>Video</c>/<c>TelemetryReceived</c>/<c>Disconnected</c>
/// handlers, connect results and keyframe requests raised by the decoder. Decoding happens on one dedicated thread fed by
/// its own <c>Channel</c>; that thread creates, uses and disposes every decoder, and writes decoded frames to the sink.
/// </para>
/// <para>
/// Phone discovery uses connect-retry polling with a 0.5 s to 5 s backoff (USB through usbmux, Wi-Fi through discovery
/// candidates), not usbmux Listen, so a dropped or reset daemon connection needs no special handling.
/// </para>
/// </summary>
public sealed class Host : ILinkHost
{
    static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);
    static readonly TimeSpan DecoderRetry = TimeSpan.FromSeconds(1);
    static readonly TimeSpan LogThrottle = TimeSpan.FromSeconds(5);
    const int MaxQueuedFrames = 3;

    readonly IConsumerSource _consumer;
    readonly IDecoderFactory _decoders;
    readonly IFrameSink _sink;
    readonly Func<long> _qpcNow;
    readonly long _qpcFreq;
    readonly ILogSink _log;
    readonly TimeProvider _time;
    readonly LinkManager _links;

    // Host loop mailbox, and the decode thread's own queue.
    readonly Channel<HostEvent> _events = Channel.CreateUnbounded<HostEvent>(new() { SingleReader = true });
    readonly Channel<DecodeItem> _decodeQ = Channel.CreateUnbounded<DecodeItem>(new() { SingleReader = true });

    // Shared with other threads.
    Telemetry? _telemetry;
    int _reqW, _reqH;
    int _decodeBacklog;          // items in _decodeQ not yet taken by the decode thread (Channel.Count is unsupported here)
    int _epoch;                  // bumped whenever the decode pipeline is reset
    long _wantSize;              // (w << 32) | h the sink may currently receive; 0 = none
    long _createFailedAt = long.MinValue;   // fake/real timestamp of the last decoder create failure, MinValue = none
    int _createFailedEpoch = -1;

    readonly SampleWindow _fpsIn;
    readonly SampleWindow _fpsOut;
    readonly SampleWindow _latency;
    readonly object _logGate = new();
    readonly Dictionary<string, long> _lastLog = new();

    /// <summary>Test hook: invoked after each poll tick has been fully processed.</summary>
    internal Action? OnTickProcessed { get; set; }

    /// <summary>Test hook: invoked once the ticker's next 100 ms delay has been armed.</summary>
    internal Action? OnTickerArmed { get; set; }

    /// <param name="wifi">Wi-Fi side (discovery plus connect); null = USB only.</param>
    /// <param name="pairedKey">The paired phone's static public key; phones with another key are ignored.
    /// A null function or a null result accepts any confirmed phone. A throwing function means "cannot verify": the phone is dropped.</param>
    public Host(IConsumerSource consumer, IPhoneConnector connector, IDecoderFactory decoders, IFrameSink sink,
        Func<long> qpcNow, ILogSink log, TimeProvider? time = null, long qpcFrequency = 0,
        IWifiConnector? wifi = null, Func<byte[]?>? pairedKey = null)
    {
        _consumer = consumer;
        _decoders = decoders;
        _sink = sink;
        _qpcNow = qpcNow;
        _qpcFreq = qpcFrequency > 0 ? qpcFrequency : Stopwatch.Frequency;
        _log = log;
        _time = time ?? TimeProvider.System;
        _fpsIn = new SampleWindow(_qpcFreq);
        _fpsOut = new SampleWindow(_qpcFreq);
        _latency = new SampleWindow(_qpcFreq);
        _links = new LinkManager(connector, wifi, pairedKey ?? (() => null), _time, this);
    }

    // ---- status ----

    public HostStatus Status
    {
        get
        {
            var snap = _links.Current;
            var tel = Volatile.Read(ref _telemetry);
            long now = _qpcNow();
            var lat = _latency.Snapshot(now);
            lat.Sort();
            return new HostStatus(
                Link: snap.Link,
                Phone: snap.Phone,
                ConsumerWidth: Volatile.Read(ref _reqW),
                ConsumerHeight: Volatile.Read(ref _reqH),
                FpsIn: _fpsIn.Snapshot(now).Count,
                FpsOut: _fpsOut.Snapshot(now).Count,
                LatencyP50Ms: Percentile(lat, 0.50),
                LatencyP95Ms: Percentile(lat, 0.95),
                RttMs: snap.Active?.RttMs,
                BitrateMbps: tel is null ? null : tel.BitrateKbps / 1000.0,
                Thermal: tel?.Thermal,
                BatteryPercent: tel is null ? null : (int)Math.Round(tel.Battery * 100),
                Charging: tel?.Charging,
                Standby: snap.Standby,
                TargetBitrateKbps: snap.Link == "none" ? 0 : _links.TargetKbps,
                DroppedStandbyFrames: _links.DroppedStandbyFrames);
        }
    }

    static double? Percentile(List<double> sorted, double p) =>
        sorted.Count == 0 ? null : sorted[Math.Max(0, (int)Math.Ceiling(p * sorted.Count) - 1)];

    /// <summary>
    /// Unpairs the phone of the video-carrying link (sealed unpair command + local store update), then closes both links.
    /// False (with a log line) when no confirmed link exists or the store fails. Completes false if the host is not running.
    /// </summary>
    public Task<bool> UnpairAsync()
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ev = new CallEvent(async () =>
        {
            try { tcs.TrySetResult(await _links.UnpairAsync().ConfigureAwait(false)); }
            catch (Exception e)
            {
                LogThrottled("unpair", $"unpair failed: {e.GetType().Name}");
                tcs.TrySetResult(false);
            }
        }, () => tcs.TrySetResult(false));
        if (!_events.Writer.TryWrite(ev)) tcs.TrySetResult(false);
        return tcs.Task;
    }

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

                try { await HandleAsync(ev).ConfigureAwait(false); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception e) { LogThrottled("loop", $"host loop error: {e.GetType().Name}: {e.Message}"); }

                if (ev is TickEvent) CallTickHook();
            }
        }
        finally
        {
            tickerCts.Cancel();
            try { await ticker.ConfigureAwait(false); } catch (OperationCanceledException) { }
            try { await _links.ShutdownAsync().ConfigureAwait(false); } catch (Exception) { }
            _events.Writer.TryComplete();
            while (_events.Reader.TryRead(out var left))
                if (left is CallEvent c) c.Dropped?.Invoke();
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
                var delay = Task.Delay(PollInterval, _time, ct);
                try { OnTickerArmed?.Invoke(); } catch (Exception) { }
                await delay.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
    }

    bool Post(HostEvent ev) => _events.Writer.TryWrite(ev);

    bool ILinkHost.Post(Func<Task> work) => Post(new CallEvent(work, null));

    /// <summary>Test hook: completes once every event posted before this call has been handled by the loop.</summary>
    internal Task FlushAsync()
    {
        var b = new BarrierEvent();
        Post(b);
        return b.Done.Task;
    }

    async Task HandleAsync(HostEvent ev)
    {
        switch (ev)
        {
            case TickEvent:
                PollConsumer();
                _links.OnTick(_active, _wantW, _wantH);
                RetryDecoderIfNeeded();
                break;
            case CallEvent c:
                await c.Work().ConfigureAwait(false);
                break;
            case KeyframeWantedEvent:
                _links.RequestKeyframe(false);
                break;
            case BarrierEvent b:
                b.Done.TrySetResult();
                break;
        }
    }

    // ---- consumer polling ----

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

    /// <summary>A failed decoder create is retried, but not more often than once a second.</summary>
    void RetryDecoderIfNeeded()
    {
        long failedAt = Interlocked.Read(ref _createFailedAt);
        if (failedAt == long.MinValue) return;
        if (Volatile.Read(ref _createFailedEpoch) != Volatile.Read(ref _epoch)) { Interlocked.Exchange(ref _createFailedAt, long.MinValue); return; }
        if (_time.GetElapsedTime(failedAt, _time.GetTimestamp()) < DecoderRetry) return;
        Interlocked.Exchange(ref _createFailedAt, long.MinValue);
        _links.RetryDecoder();
    }

    // ---- ILinkHost (called on the host loop, except where noted) ----

    void ILinkHost.Log(string line) => HostLog(line);
    void ILinkHost.LogThrottled(string key, string line) => LogThrottled(key, line);
    int ILinkHost.DecodeBacklog => Volatile.Read(ref _decodeBacklog);
    void ILinkHost.SetTelemetry(Telemetry? t) => Volatile.Write(ref _telemetry, t);

    void ILinkHost.EnqueueVideo(IPhoneLink link, VideoFrame frame)
    {
        _fpsIn.Add(_qpcNow());
        EnqueueDecode(new DecodeItem(Volatile.Read(ref _epoch), 0, 0, link, frame));
    }

    /// <summary>Tells the decode thread to dispose its decoder and (for w &gt; 0) create a new one. Never blocks.</summary>
    void ILinkHost.ResetDecoder(int w, int h, IPhoneLink? link)
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
                        dec = null; dw = dh = 0;
                        needKey = item.Width > 0; // a fresh decoder must start on a keyframe, never on old P-frames
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
                                Volatile.Write(ref _createFailedEpoch, item.Epoch);
                                Interlocked.Exchange(ref _createFailedAt, _time.GetTimestamp());
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
    sealed record KeyframeWantedEvent : HostEvent;
    /// <summary>Work to run on the host loop. <paramref name="Dropped"/> runs instead if the loop ends first.</summary>
    sealed record CallEvent(Func<Task> Work, Action? Dropped) : HostEvent;
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
