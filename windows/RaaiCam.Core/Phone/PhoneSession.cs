using System.Buffers.Binary;
using System.Text;
using RaaiCam.Core.Pairing;
using RaaiCam.Protocol;
using RaaiCam.Protocol.Crypto;

namespace RaaiCam.Core.Phone;

/// <summary>
/// PC side of a phone connection over a byte stream (protocol v2, the PC is the initiator).
/// <list type="number">
/// <item>Sends HELLO immediately and requires the phone's HELLO as the first received frame within 5 s.</item>
/// <item>Runs <see cref="InitiatorHandshake"/> (mode 1 when a paired phone is supplied, mode 2 when a <see cref="PairingSession"/>
/// is supplied) within 5 s for the whole handshake; AUTH1/AUTH3 go out and AUTH2 comes in as plaintext AUTH frames.</item>
/// <item>From AUTH3 on every frame is SEALED. The session stays <b>unconfirmed</b> until the phone's first SEALED frame opens
/// and is an INFO (key confirmation); that must happen within 5 s of AUTH3. Anything else closes the session.
/// <see cref="Phone"/> is set, <see cref="Paired"/> raised and the phone stored (pairing mode) only then.</item>
/// <item>Then pings every second and dispatches received frames.</item>
/// </list>
/// Threading: <see cref="Video"/>, <see cref="TelemetryReceived"/>, <see cref="Paired"/> and the handlers raised for a
/// read-loop failure run on the read loop (never concurrently with each other). <see cref="Disconnected"/> fires exactly
/// once, on the thread that detected the end (read loop, ping loop, or the caller of DisposeAsync). A throwing handler is
/// logged and ignored. Seal and the socket write of every sealed frame happen under one writer lock (strict counter order).
/// The session owns the transport and disposes it on DisposeAsync; the sealed channel and session keys are zeroed on teardown.
/// </summary>
public sealed class PhoneSession : IAsyncDisposable
{
    static readonly TimeSpan HelloTimeout = TimeSpan.FromSeconds(5);
    static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(5);
    static readonly TimeSpan ConfirmTimeout = TimeSpan.FromSeconds(5);
    static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(1);

    readonly Stream _transport;
    readonly Func<ulong> _pcNowUs;
    readonly ILogSink _log;
    readonly TimeProvider _time;
    readonly SecureLinkOptions _secure;
    readonly SemaphoreSlim _writeLock = new(1, 1);
    readonly CancellationTokenSource _cts = new();
    readonly TaskCompletionSource _runDone = new(TaskCreationOptions.RunContinuationsAsynchronously);
    int _disconnected;
    int _started;
    volatile HelloInfo? _phone;
    volatile byte[]? _phoneKey;
    volatile SealedChannel? _channel;
    volatile bool _confirmed;

    public PhoneSession(Stream transport, Func<ulong> pcNowUs, ILogSink log, SecureLinkOptions secure, TimeProvider? time = null)
    {
        secure.Validate();
        _transport = transport;
        _pcNowUs = pcNowUs;
        _log = log;
        _secure = secure;
        _time = time ?? TimeProvider.System;
    }

    public event Action<VideoFrame>? Video;
    public event Action<Telemetry>? TelemetryReceived;
    public event Action<string>? Disconnected;
    /// <summary>Mode 2 only: raised once, after key confirmation, with the phone that was stored in the pairing store.</summary>
    public event Action<PairedPhone>? Paired;

    /// <summary>Null until the phone's INFO frame opened (key confirmation).</summary>
    public HelloInfo? Phone => _phone;
    /// <summary>The phone's static public key; null until confirmation.</summary>
    public byte[]? PhoneStaticKey => _phoneKey is { } k ? (byte[])k.Clone() : null;
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
            if (_secure.Pairing is { } pairing && (pairing.IsExpired || pairing.IsUsed))
            {
                reason = pairing.IsExpired ? "pairing token expired" : "pairing token already used";
                return;
            }

            await WriteRawAsync(FrameType.Hello, Messages.Hello("pc"), token).ConfigureAwait(false);

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
            var hello = Messages.ParseHello(first.Value.Payload.Span);
            if (hello.Role != "phone") throw new ProtocolException("HELLO role is not phone");
            PcLog($"phone hello: proto {hello.Proto}");

            // ---- AUTH (whole handshake within 5 s) ----
            byte[] phoneKey;
            SealedChannel channel;
            using (var hsCts = new CancellationTokenSource(HandshakeTimeout, _time))
            using (var hsBoth = CancellationTokenSource.CreateLinkedTokenSource(token, hsCts.Token))
            {
                try { (channel, phoneKey) = await HandshakeAsync(hsBoth.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (hsCts.IsCancellationRequested && !token.IsCancellationRequested)
                {
                    reason = "handshake timeout";
                    return;
                }
            }
            _channel = channel;

            // ---- key confirmation: the phone's first SEALED frame must be INFO, within 5 s of AUTH3 ----
            HelloInfo info;
            using (var confCts = new CancellationTokenSource(ConfirmTimeout, _time))
            using (var confBoth = CancellationTokenSource.CreateLinkedTokenSource(token, confCts.Token))
            {
                PcLog("authenticated; awaiting INFO");
                Frame? f1;
                try { f1 = await FrameCodec.ReadAsync(_transport, confBoth.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (confCts.IsCancellationRequested && !token.IsCancellationRequested)
                {
                    reason = "not confirmed";
                    return;
                }
                if (f1 is null) { reason = "peer closed before confirmation"; return; }
                if (f1.Value.Type != FrameType.Sealed)
                    throw new ProtocolException($"expected SEALED after AUTH, got {f1.Value.Type}");
                var inner = channel.Open(f1.Value.Payload.Span);
                if (inner.Type != FrameType.Info)
                    throw new ProtocolException($"first sealed frame must be INFO, got {inner.Type}");
                info = Messages.ParseInfo(inner.Payload.Span);
            }

            // Confirmed: the phone proved it holds the session keys.
            _phoneKey = phoneKey;
            _phone = info;
            _confirmed = true;
            PcLog($"phone confirmed: {info.Device}");
            if (_secure.Pairing is { } p)
            {
                if (!p.TryMarkUsed()) throw new ProtocolException("pairing token already used");
                PairedPhone saved;
                try { saved = _secure.Store.SavePhone(phoneKey, string.IsNullOrWhiteSpace(info.Device) ? "iPhone" : info.Device); }
                catch (Exception e)
                {
                    reason = "could not save pairing: " + e.GetType().Name;
                    return;
                }
                PcLog("paired");
                Raise(Paired, saved);
            }

            pingLoop = PingLoopAsync(token);

            while (true)
            {
                var f = await FrameCodec.ReadAsync(_transport, token).ConfigureAwait(false);
                if (f is null) { reason = "peer closed"; return; }
                if (f.Value.Type != FrameType.Sealed)
                    throw new ProtocolException($"unsealed {f.Value.Type} frame after AUTH");
                Handle(channel.Open(f.Value.Payload.Span));
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
            await TeardownCryptoAsync().ConfigureAwait(false);
            _runDone.TrySetResult();
            Disconnect(reason);
        }
    }

    /// <summary>AUTH1 out, AUTH2 in, AUTH3 out. Returns the sealed channel and the phone's static key (not yet confirmed).</summary>
    async Task<(SealedChannel Channel, byte[] PhoneKey)> HandshakeAsync(CancellationToken ct)
    {
        var pairing = _secure.Pairing;
        using var pcKey = _secure.Identity.ToStaticKey();
        var hs = new InitiatorHandshake(pcKey, _secure.Identity.PcId, pairing is null ? _secure.Phone!.PublicKey : null, pairing?.Token);
        await WriteRawAsync(FrameType.Auth, hs.CreateAuth1(), ct).ConfigureAwait(false);

        var a2 = await FrameCodec.ReadAsync(_transport, ct).ConfigureAwait(false);
        if (a2 is null) throw new ProtocolException("peer closed during handshake");
        if (a2.Value.Type != FrameType.Auth) throw new ProtocolException($"expected AUTH, got {a2.Value.Type}");
        var phoneKey = hs.ReadAuth2(a2.Value.Payload.ToArray());
        var a3 = hs.CreateAuth3();
        await WriteRawAsync(FrameType.Auth, a3, ct).ConfigureAwait(false);

        using var keys = hs.Keys; // the channel keeps its own copies; zero ours
        return (new SealedChannel(keys, isInitiator: true), phoneKey);
    }

    async Task TeardownCryptoAsync()
    {
        var ch = _channel;
        if (ch is null) return;
        bool locked = false;
        try { locked = await _writeLock.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); }
        catch (Exception) { }
        try { ch.Dispose(); }
        finally { if (locked) _writeLock.Release(); }
    }

    async Task PingLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await WriteSealedAsync(FrameType.Ping, Messages.Ping(_pcNowUs()), ct).ConfigureAwait(false);
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
            case FrameType.Info:
                PcLog("ignoring repeated INFO");
                break;
            default:
                break; // other allowed inner types (CMD/PING are phone-bound): ignore for forward compatibility
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

    // ---- writes ----

    static byte[] BuildFrame(FrameType type, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > FrameCodec.MaxPayload)
            throw new ProtocolException($"payload {payload.Length} exceeds max {FrameCodec.MaxPayload}");
        var buf = new byte[FrameCodec.HeaderSize + payload.Length];
        buf[0] = (byte)type;
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(1), (uint)payload.Length);
        payload.CopyTo(buf.AsSpan(FrameCodec.HeaderSize));
        return buf;
    }

    /// <summary>Plaintext frame (HELLO and AUTH only, before the channel exists).</summary>
    async Task WriteRawAsync(FrameType type, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        var buf = BuildFrame(type, payload.Span);
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try { await _transport.WriteAsync(buf, ct).ConfigureAwait(false); }
        finally { _writeLock.Release(); }
    }

    /// <summary>Seal and write under ONE lock, so frames reach the wire in counter order.</summary>
    async Task WriteSealedAsync(FrameType inner, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        var ch = _channel ?? throw new InvalidOperationException("no secure channel");
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var buf = BuildFrame(FrameType.Sealed, ch.Seal(inner, payload.Span));
            await _transport.WriteAsync(buf, ct).ConfigureAwait(false);
        }
        finally { _writeLock.Release(); }
    }

    async Task SendCmdAsync(object cmd)
    {
        // Commands only go out on a confirmed link (the host sends them once Phone is set).
        if (Volatile.Read(ref _disconnected) == 1 || !_confirmed) return;
        try
        {
            await WriteSealedAsync(FrameType.Cmd, Messages.Cmd(cmd), _cts.Token).ConfigureAwait(false);
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

    /// <summary>Tells the phone whether to stream video over this link (<c>{"cmd":"route","video":...}</c>).</summary>
    public Task SendRouteAsync(bool video) => SendCmdAsync(new RouteCmd(video));

    /// <summary>Sends <c>{"cmd":"unpair"}</c>, then removes the paired phone from the local store (even if the send failed).</summary>
    public async Task UnpairAsync()
    {
        try { await SendCmdAsync(new UnpairCmd()).ConfigureAwait(false); }
        finally
        {
            try { _secure.Store.Unpair(); }
            catch (Exception e) { PcLog($"unpair: could not update the store: {e.GetType().Name}"); }
        }
    }

    public async ValueTask DisposeAsync()
    {
        Disconnect("disposed");
        if (Volatile.Read(ref _started) == 1)
            await _runDone.Task.ConfigureAwait(false);
        try { await _transport.DisposeAsync().ConfigureAwait(false); } catch (Exception) { }
    }
}
