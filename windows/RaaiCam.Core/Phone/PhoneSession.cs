using System.Buffers.Binary;
using System.Text;
using RaaiCam.Protocol;

namespace RaaiCam.Core.Phone;

/// <summary>
/// PC side of a phone connection over a byte stream. Sends HELLO immediately, requires the phone's HELLO
/// as the first received frame within 5 s, then pings every second and dispatches received frames.
/// <para>
/// Threading: <see cref="Video"/>, <see cref="TelemetryReceived"/> and the handlers raised for a read-loop
/// failure run on the read loop (never concurrently with each other). <see cref="Disconnected"/> fires exactly
/// once, on the thread that detected the end (read loop, ping loop, or the caller of DisposeAsync).
/// A throwing handler is logged and ignored. All writes are serialized and each frame goes out in one write.
/// The session owns the transport and disposes it on DisposeAsync.
/// </para>
/// </summary>
public sealed class PhoneSession : IAsyncDisposable
{
    static readonly TimeSpan HelloTimeout = TimeSpan.FromSeconds(5);
    static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(1);

    readonly Stream _transport;
    readonly Func<ulong> _pcNowUs;
    readonly ILogSink _log;
    readonly TimeProvider _time;
    readonly SemaphoreSlim _writeLock = new(1, 1);
    readonly CancellationTokenSource _cts = new();
    readonly TaskCompletionSource _runDone = new(TaskCreationOptions.RunContinuationsAsynchronously);
    int _disconnected;
    int _started;
    volatile HelloInfo? _phone;

    public PhoneSession(Stream transport, Func<ulong> pcNowUs, ILogSink log, TimeProvider? time = null)
    {
        _transport = transport;
        _pcNowUs = pcNowUs;
        _log = log;
        _time = time ?? TimeProvider.System;
    }

    public event Action<VideoFrame>? Video;
    public event Action<Telemetry>? TelemetryReceived;
    public event Action<string>? Disconnected;

    public HelloInfo? Phone => _phone;
    public ClockSync Clock { get; } = new();

    public async Task RunAsync(CancellationToken ct)
    {
        if (Volatile.Read(ref _disconnected) == 1 || Interlocked.Exchange(ref _started, 1) == 1) return;
        string reason = "closed";
        Task? pingLoop = null;
        using var link = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        var token = link.Token;
        try
        {
            PcLog("session started");
            await WriteFrameAsync(FrameType.Hello, Messages.Hello("pc", AppVersion(), Environment.MachineName,
                Environment.OSVersion.VersionString), token).ConfigureAwait(false);

            Frame? first;
            using (var helloCts = new CancellationTokenSource(HelloTimeout, _time))
            using (var both = CancellationTokenSource.CreateLinkedTokenSource(token, helloCts.Token))
            {
                try { first = await FrameCodec.ReadAsync(_transport, both.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (helloCts.IsCancellationRequested && !token.IsCancellationRequested)
                {
                    reason = "no hello";
                    return;
                }
            }
            if (first is null) { reason = "peer closed before hello"; return; }
            if (first.Value.Type != FrameType.Hello)
                throw new ProtocolException($"expected HELLO first, got {first.Value.Type}");
            var phone = Messages.ParseHello(first.Value.Payload.Span);
            _phone = phone;
            PcLog($"phone hello: {phone.App} {phone.Version} on {phone.Device} ({phone.Os})");

            pingLoop = PingLoopAsync(token);

            while (true)
            {
                var f = await FrameCodec.ReadAsync(_transport, token).ConfigureAwait(false);
                if (f is null) { reason = "peer closed"; return; }
                Handle(f.Value);
            }
        }
        catch (OperationCanceledException)
        {
            reason = "cancelled";
        }
        catch (ProtocolException e)
        {
            reason = "protocol error: " + e.Message;
        }
        catch (Exception e)
        {
            reason = "transport error: " + e.Message;
        }
        finally
        {
            link.Cancel();
            if (pingLoop is not null)
            {
                try { await pingLoop.ConfigureAwait(false); } catch (Exception) { }
            }
            _runDone.TrySetResult();
            Disconnect(reason);
        }
    }

    async Task PingLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await WriteFrameAsync(FrameType.Ping, Messages.Ping(_pcNowUs()), ct).ConfigureAwait(false);
                await Task.Delay(PingInterval, _time, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            Disconnect("transport error: " + e.Message);
        }
    }

    void Handle(Frame f)
    {
        switch (f.Type)
        {
            case FrameType.Video:
                Raise(Video, Messages.ParseVideo(f.Payload));
                break;
            case FrameType.Telemetry:
                {
                    var t = Messages.ParseTelemetry(f.Payload.Span);
                    // The phone may omit these; the record type promises non-null.
                    t = t with { Thermal = t.Thermal ?? "unknown", Lens = t.Lens ?? "wide" };
                    Raise(TelemetryReceived, t);
                    break;
                }
            case FrameType.Log:
                _log.Write("phone", Encoding.UTF8.GetString(f.Payload.Span));
                break;
            case FrameType.Pong:
                {
                    var (pcSend, phoneRecv) = Messages.ParsePong(f.Payload.Span);
                    Clock.AddSample(pcSend, phoneRecv, _pcNowUs());
                    break;
                }
            case FrameType.Hello:
                PcLog("ignoring repeated HELLO");
                break;
            default:
                break; // unknown or PC-bound-only types: ignore for forward compatibility
        }
    }

    void Raise<T>(Action<T>? handlers, T arg)
    {
        if (handlers is null) return;
        foreach (var h in handlers.GetInvocationList().Cast<Action<T>>())
        {
            try { h(arg); }
            catch (Exception e) { PcLog($"event handler threw: {e.GetType().Name}: {e.Message}"); }
        }
    }

    void PcLog(string line)
    {
        try { _log.Write("pc", line); } catch (Exception) { }
    }

    void Disconnect(string reason)
    {
        if (Interlocked.Exchange(ref _disconnected, 1) == 1) return;
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
        PcLog("disconnected: " + reason);
        Raise(Disconnected, reason);
    }

    static string AppVersion() =>
        typeof(PhoneSession).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    // ---- writes ----

    async Task WriteFrameAsync(FrameType type, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        if (payload.Length > FrameCodec.MaxPayload)
            throw new ProtocolException($"payload {payload.Length} exceeds max {FrameCodec.MaxPayload}");
        var buf = new byte[FrameCodec.HeaderSize + payload.Length];
        buf[0] = (byte)type;
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(1), (uint)payload.Length);
        payload.CopyTo(buf.AsMemory(FrameCodec.HeaderSize));
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try { await _transport.WriteAsync(buf, ct).ConfigureAwait(false); }
        finally { _writeLock.Release(); }
    }

    async Task SendCmdAsync(object cmd)
    {
        if (Volatile.Read(ref _disconnected) == 1) return;
        try
        {
            await WriteFrameAsync(FrameType.Cmd, Messages.Cmd(cmd), _cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            Disconnect("transport error: " + e.Message);
        }
    }

    public Task StartAsync(int w, int h, string lens = "wide", int kbps = 8000) =>
        SendCmdAsync(new StartCmd(w, h, 30, lens, kbps));

    public Task StopAsync() => SendCmdAsync(new StopCmd());
    public Task RequestKeyframeAsync() => SendCmdAsync(new KeyframeCmd());
    public Task SetBitrateAsync(int kbps) => SendCmdAsync(new BitrateCmd(kbps));
    public Task SetLensAsync(string lens) => SendCmdAsync(new LensCmd(lens));

    public async ValueTask DisposeAsync()
    {
        Disconnect("disposed");
        if (Volatile.Read(ref _started) == 1)
            await _runDone.Task.ConfigureAwait(false);
        try { await _transport.DisposeAsync().ConfigureAwait(false); } catch (Exception) { }
    }
}
