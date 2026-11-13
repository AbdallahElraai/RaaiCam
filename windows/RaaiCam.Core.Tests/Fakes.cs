using System.Net;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using RaaiCam.Core.Hosting;
using RaaiCam.Protocol;

namespace RaaiCam.Core.Tests;

// ---- fakes (deterministic: FakeTimeProvider drives every timer, waits are signal based) ----

/// <summary>Pulsed by every fake whenever something observable changes; tests wait on conditions, never sleep.</summary>
sealed class Observed
{
    readonly SemaphoreSlim _s = new(0);
    public void Pulse() => _s.Release();
    public Task WaitAsync(CancellationToken ct) => _s.WaitAsync(ct);

    public async Task Until(Func<bool> cond)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!cond()) await _s.WaitAsync(cts.Token);
    }
}

sealed class FakeConsumer : IConsumerSource
{
    readonly object _gate = new();
    ConsumerRequest? _state;
    public ConsumerRequest? Read() { lock (_gate) return _state; }
    public void Set(bool active, int w, int h) { lock (_gate) _state = new ConsumerRequest(active, w, h); }
    public void Inactive() => Set(false, 0, 0);
}

sealed class FakeLink(Observed obs) : IPhoneLink
{
    readonly object _gate = new();
    readonly List<string> _commands = new();
    readonly List<int> _startKbps = new();
    readonly TaskCompletionSource _run = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public event Action<VideoFrame>? Video;
    public event Action<Telemetry>? TelemetryReceived;
    public event Action<string>? Disconnected;

    public HelloInfo? Phone { get; set; } = new(2, "phone", "RaaiCam", "1.0", "iPhone 15", "iOS 18");
    public byte[]? PhoneStaticKey { get; set; } = new byte[32];
    public double? RttMs { get; set; } = 3.0;
    public long? OffsetUs { get; set; } = 0;
    public volatile bool Disposed;
    public TaskCompletionSource? StartGate;
    public TaskCompletionSource? DisposeGate;
    public int StartFailures;                // the next N StartAsync calls throw
    public Exception? UnpairThrows;
    public bool HangRun;                     // RunAsync never completes, even after DisposeAsync

    public long? ToPcUs(ulong phoneUs) => OffsetUs is { } o ? (long)phoneUs - o : null;

    public List<string> Commands { get { lock (_gate) return _commands.ToList(); } }
    public List<int> StartKbps { get { lock (_gate) return _startKbps.ToList(); } }
    public int Count(string cmd) { lock (_gate) return _commands.Count(c => c == cmd); }
    public int CountPrefix(string prefix) { lock (_gate) return _commands.Count(c => c.StartsWith(prefix, StringComparison.Ordinal)); }
    /// <summary>True when the phone would currently send video here: the last route command was "video".</summary>
    public bool Carrying { get { lock (_gate) return _commands.LastOrDefault(c => c.StartsWith("route")) == "route video"; } }
    void Add(string c) { lock (_gate) _commands.Add(c); obs.Pulse(); }

    public Task RunAsync(CancellationToken ct) => _run.Task;

    public async Task StartAsync(int width, int height, int kbps)
    {
        lock (_gate) _startKbps.Add(kbps);
        Add($"start {width}x{height}");
        if (StartGate is { } g) await g.Task;
        if (Interlocked.Decrement(ref StartFailures) >= 0) throw new IOException("start failed");
        Interlocked.Exchange(ref StartFailures, 0);
    }

    public Task StopAsync() { Add("stop"); return Task.CompletedTask; }
    public Task RequestKeyframeAsync() { Add("keyframe"); return Task.CompletedTask; }
    public Task SendRouteAsync(bool video) { Add(video ? "route video" : "route idle"); return Task.CompletedTask; }
    public Task SetBitrateAsync(int kbps) { Add($"bitrate {kbps}"); return Task.CompletedTask; }

    public Task UnpairAsync()
    {
        Add("unpair");
        return UnpairThrows is { } e ? Task.FromException(e) : Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        Disposed = true;
        obs.Pulse();
        if (DisposeGate is { } g) await g.Task;
        if (!HangRun) _run.TrySetResult();
    }

    public void RaiseVideo(uint seq, bool key, ulong captureUs = 0) =>
        Video?.Invoke(new VideoFrame(captureUs, seq, key, new[] { (byte)seq }));
    public void RaiseTelemetry(Telemetry t) => TelemetryReceived?.Invoke(t);
    public void RaiseDisconnected(string reason) => Disconnected?.Invoke(reason);
}

sealed class FakeConnector(Observed obs, TimeProvider? time = null) : IPhoneConnector
{
    readonly object _gate = new();
    readonly Queue<Func<CancellationToken, Task<IPhoneLink?>>> _script = new();
    readonly List<long> _attemptTimes = new();
    public readonly List<FakeLink> Links = new();
    int _attempts;
    /// <summary>Unscripted attempts find no phone attached (null) instead of creating a link.</summary>
    public volatile bool NoPhone;

    public int Attempts => Volatile.Read(ref _attempts);
    /// <summary>Fake timestamp (ticks) of every attempt.</summary>
    public List<long> AttemptTimes { get { lock (_gate) return _attemptTimes.ToList(); } }

    /// <summary>Queue the outcome of the next attempt. Unscripted attempts create a fresh link.</summary>
    public void Script(Func<IPhoneLink?> next) => ScriptAsync(_ => Task.FromResult(next()));
    public void ScriptAsync(Func<CancellationToken, Task<IPhoneLink?>> next) { lock (_gate) _script.Enqueue(next); }

    public Task<IPhoneLink?> ConnectAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref _attempts);
        Func<CancellationToken, Task<IPhoneLink?>>? next;
        lock (_gate)
        {
            _attemptTimes.Add(time?.GetTimestamp() ?? 0);
            next = _script.Count > 0 ? _script.Dequeue() : null;
        }
        obs.Pulse();
        if (next is not null)
        {
            try { return next(ct); }
            catch (Exception e) { return Task.FromException<IPhoneLink?>(e); }
        }
        return Task.FromResult<IPhoneLink?>(NoPhone ? null : NewLink());
    }

    public FakeLink NewLink()
    {
        var l = new FakeLink(obs);
        lock (_gate) Links.Add(l);
        return l;
    }
}

sealed class FakeWifi(Observed obs) : IWifiConnector
{
    readonly object _gate = new();
    readonly Channel<WifiCandidate> _candidates = Channel.CreateUnbounded<WifiCandidate>();
    readonly List<WifiCandidate> _attempts = new();
    readonly List<IPAddress> _recorded = new();
    int _browses;

    public readonly List<FakeLink> Links = new();
    /// <summary>Decides each connect: return a link, or throw. Default creates a fresh link.</summary>
    public Func<WifiCandidate, FakeLink?>? OnConnect;
    /// <summary>Takes over the whole connect (e.g. to hang until the token fires).</summary>
    public Func<WifiCandidate, CancellationToken, Task<IPhoneLink>>? OnConnectAsync;
    int _browseActive;
    public int BrowseActive => Volatile.Read(ref _browseActive);

    public int Browses => Volatile.Read(ref _browses);
    public List<WifiCandidate> Attempts { get { lock (_gate) return _attempts.ToList(); } }
    public List<IPAddress> Recorded { get { lock (_gate) return _recorded.ToList(); } }

    public void Offer(string ip, int port = 47820)
    {
        _candidates.Writer.TryWrite(new WifiCandidate(IPAddress.Parse(ip), port));
        obs.Pulse();
    }

    public async IAsyncEnumerable<WifiCandidate> BrowseAsync([EnumeratorCancellation] CancellationToken ct)
    {
        Interlocked.Increment(ref _browses);
        Interlocked.Increment(ref _browseActive);
        obs.Pulse();
        try { await foreach (var c in _candidates.Reader.ReadAllAsync(ct)) yield return c; }
        finally { Interlocked.Decrement(ref _browseActive); obs.Pulse(); }
    }

    public Task<IPhoneLink> ConnectAsync(WifiCandidate candidate, CancellationToken ct)
    {
        lock (_gate) _attempts.Add(candidate);
        if (OnConnectAsync is { } oca) { obs.Pulse(); return oca(candidate, ct); }
        obs.Pulse();
        FakeLink? link;
        try { link = OnConnect is null ? new FakeLink(obs) : OnConnect(candidate); }
        catch (Exception e) { return Task.FromException<IPhoneLink>(e); }
        if (link is null) return Task.FromException<IPhoneLink>(new IOException("connect refused"));
        lock (_gate) Links.Add(link);
        return Task.FromResult<IPhoneLink>(link);
    }

    public void RecordWorkingAddress(IPAddress address)
    {
        lock (_gate) _recorded.Add(address);
        obs.Pulse();
    }
}

sealed class FakeDecoder : IVideoDecoder
{
    readonly FakeDecoderFactory _f;
    public readonly int W, H;
    public readonly int CreatedOnThread = Environment.CurrentManagedThreadId;
    public readonly List<int> UsedOnThreads = new();
    public int DisposedOnThread = -1;

    public FakeDecoder(FakeDecoderFactory f, int w, int h) { _f = f; W = w; H = h; }

    public event Action? KeyframeNeeded;
    public void RaiseKeyframeNeeded() => KeyframeNeeded?.Invoke();

    public VideoDecodeResult Decode(ReadOnlySpan<byte> accessUnit, long sampleTime100ns, Span<byte> nv12Out, out int stride)
    {
        stride = W;
        UsedOnThreads.Add(Environment.CurrentManagedThreadId);
        return _f.OnDecode(this, accessUnit[0]);
    }

    public void Dispose()
    {
        DisposedOnThread = Environment.CurrentManagedThreadId;
        _f.Obs.Pulse();
    }
}

sealed class FakeDecoderFactory(Observed obs) : IDecoderFactory
{
    readonly object _gate = new();
    readonly List<FakeDecoder> _created = new();
    readonly List<int> _decodedSeqs = new();
    int _createAttempts;
    public Observed Obs => obs;

    public Func<FakeDecoder, byte, VideoDecodeResult>? Behavior;
    public int CreateFailures;   // the next N Create calls throw

    public List<FakeDecoder> Created { get { lock (_gate) return _created.ToList(); } }
    public List<int> DecodedSeqs { get { lock (_gate) return _decodedSeqs.ToList(); } }
    public int CreateAttempts => Volatile.Read(ref _createAttempts);

    public IVideoDecoder Create(int width, int height)
    {
        Interlocked.Increment(ref _createAttempts);
        if (Interlocked.Decrement(ref CreateFailures) >= 0)
        {
            obs.Pulse();
            throw new InvalidOperationException("decoder unavailable");
        }
        Interlocked.Exchange(ref CreateFailures, 0);
        var d = new FakeDecoder(this, width, height);
        lock (_gate) _created.Add(d);
        obs.Pulse();
        return d;
    }

    internal VideoDecodeResult OnDecode(FakeDecoder d, byte seq)
    {
        lock (_gate) _decodedSeqs.Add(seq);
        obs.Pulse();
        return Behavior?.Invoke(d, seq) ?? VideoDecodeResult.Frame;
    }
}

sealed class FakeSink(Observed obs) : IFrameSink
{
    readonly object _gate = new();
    readonly List<(int W, int H, int Stride, long Qpc)> _writes = new();
    public List<(int W, int H, int Stride, long Qpc)> Writes { get { lock (_gate) return _writes.ToList(); } }

    public bool WriteFrame(ReadOnlySpan<byte> nv12, int width, int height, int stride, long timestampQpc)
    {
        lock (_gate) _writes.Add((width, height, stride, timestampQpc));
        obs.Pulse();
        return true;
    }
}

sealed class HostRig : IAsyncDisposable
{
    public readonly Observed Obs = new();
    public readonly FakeTimeProvider Time = new();
    public readonly FakeConsumer Consumer = new();
    public readonly FakeConnector Connector;
    public readonly FakeWifi? Wifi;
    public readonly FakeDecoderFactory Decoders;
    public readonly FakeSink Sink;
    public readonly ListLogSink Log = new();
    public readonly Host Host;
    public byte[] PairedKey = new byte[32];
    public Func<byte[]?>? PairedKeyFn;
    public long Qpc = 1_000;
    readonly SemaphoreSlim _ticks = new(0);
    readonly SemaphoreSlim _armed = new(0);
    readonly CancellationTokenSource _cts = new();
    public Task Run = Task.CompletedTask;

    public HostRig(bool wifi = false)
    {
        Connector = new FakeConnector(Obs, Time);
        Wifi = wifi ? new FakeWifi(Obs) : null;
        Decoders = new FakeDecoderFactory(Obs);
        Sink = new FakeSink(Obs);
        Host = new Host(Consumer, Connector, Decoders, Sink, () => Interlocked.Read(ref Qpc), Log, Time, 1_000_000,
            wifi: Wifi, pairedKey: () => PairedKeyFn is { } f ? f() : PairedKey);
        Host.OnTickProcessed = () => _ticks.Release();
        Host.OnTickerArmed = () => _armed.Release();
    }

    public FakeLink Link => Connector.Links[0];

    /// <summary>Starts the host and (by default) waits for its first, immediate tick to be fully processed.</summary>
    public async Task StartAsync(bool waitFirstTick = true)
    {
        Run = Host.RunAsync(_cts.Token);
        if (waitFirstTick) await WaitTick();
    }

    /// <summary>Starts the host and waits until the first USB link has been connected, adopted and sent a start command.</summary>
    public async Task StartStreamingAsync()
    {
        await StartAsync();
        await Obs.Until(() => Connector.Links.Count > 0 && Connector.Links[0].CountPrefix("start") > 0);
    }

    async Task WaitTick()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await _ticks.WaitAsync(cts.Token);
    }

    /// <summary>Advances fake time by 100 ms per step; each step is one full host poll tick.</summary>
    public async Task StepAsync(int n = 1)
    {
        for (int i = 0; i < n; i++)
        {
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10))) await _armed.WaitAsync(cts.Token);
            Time.Advance(TimeSpan.FromMilliseconds(100));
            await WaitTick();
        }
    }

    /// <summary>Waits until a host log line containing the text appears (polls; the log sink does not signal).</summary>
    public async Task WaitLogAsync(string text)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!Log.Lines.Any(l => l.Contains(text))) await Task.Delay(5, cts.Token);
    }

    /// <summary>Waits until every event raised so far has been handled by the host loop.</summary>
    public Task FlushAsync() => Host.FlushAsync().WaitAsync(TimeSpan.FromSeconds(10));

    public void Cancel() => _cts.Cancel();

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { await Run.WaitAsync(TimeSpan.FromSeconds(10)); } catch (OperationCanceledException) { }
    }
}
