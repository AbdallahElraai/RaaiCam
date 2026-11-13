using System.Buffers.Binary;
using System.Text;
using RaaiCam.Core.Pairing;
using RaaiCam.Core.Phone;
using RaaiCam.Protocol;
using RaaiCam.Protocol.Crypto;

namespace RaaiCam.Core.Tests;

sealed class ListLogSink : ILogSink
{
    readonly object _gate = new();
    readonly List<(string Source, string Line)> _lines = new();
    readonly SemaphoreSlim _signal = new(0);

    public void Write(string source, string line)
    {
        lock (_gate) _lines.Add((source, line));
        _signal.Release();
    }

    public List<(string Source, string Line)> Snapshot()
    {
        lock (_gate) return _lines.ToList();
    }

    public async Task WaitForAsync(string source, string line)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!Snapshot().Contains((source, line)))
            await _signal.WaitAsync(cts.Token);
    }
}

enum LinkMode { Paired, Pairing, PairedWithOtherPhoneKey }

/// <summary>
/// Session under test plus a scripted phone: the C# <see cref="ResponderHandshake"/> and <see cref="SealedChannel"/>
/// over an in-memory transport. Before the handshake completes ReadAsync/SendAsync are raw; afterwards they seal/open.
/// </summary>
sealed class Harness : IAsyncDisposable
{
    public readonly PhoneSession Session;
    public readonly Stream Phone;
    public readonly FakeTimeProvider Time = new();
    public readonly ListLogSink Sink = new();
    public readonly TempDir Dir = new();
    public readonly PairingStore Store;
    public readonly PcIdentity Identity;
    public readonly StaticKey PhoneKey = StaticKey.Generate();
    public readonly PairingSession? Pairing;
    public SealedChannel? Chan;
    public ulong NowUs = 1_000_000;
    public readonly List<string> Reasons = new();
    public readonly List<PairedPhone> PairedEvents = new();
    public readonly SemaphoreSlim DisconnectedSignal = new(0);
    public readonly SemaphoreSlim PairedSignal = new(0);
    public Task Run = Task.CompletedTask;
    readonly CancellationTokenSource _cts = new(TimeSpan.FromSeconds(20));
    int _barrier;

    public const string InfoJson = "{\"app\":\"RaaiCam\",\"version\":\"1.0\",\"device\":\"iPhone 15\",\"os\":\"iOS 18\"}";

    public Harness(LinkMode mode = LinkMode.Paired)
    {
        var (client, server) = DuplexStream.CreatePair();
        Phone = server;
        Store = new PairingStore(Dir.File("pairing.bin"), new FakeProtector(), Sink);
        Identity = Store.GetOrCreateIdentity();
        if (mode == LinkMode.Paired) Store.SavePhone(PhoneKey.Public, "iPhone");
        if (mode == LinkMode.PairedWithOtherPhoneKey) Store.SavePhone(StaticKey.Generate().Public, "other");
        if (mode == LinkMode.Pairing) Pairing = PairingSession.Begin(Identity, "Test PC", TimeProvider.System);
        var options = new SecureLinkOptions(Identity, Store, mode == LinkMode.Pairing ? null : Store.Phone, Pairing);
        Session = new PhoneSession(client, () => NowUs, Sink, options, Time);
        Session.Disconnected += r =>
        {
            lock (Reasons) Reasons.Add(r);
            DisconnectedSignal.Release();
        };
        Session.Paired += p =>
        {
            lock (PairedEvents) PairedEvents.Add(p);
            PairedSignal.Release();
        };
    }

    public void StartRun() => Run = Session.RunAsync(_cts.Token);

    public async Task<Frame> ReadRawAsync()
    {
        var f = await FrameCodec.ReadAsync(Phone, _cts.Token);
        return f ?? throw new InvalidOperationException("session closed transport");
    }

    /// <summary>Raw before the handshake finished; the opened inner frame afterwards.</summary>
    public async Task<Frame> ReadAsync()
    {
        var f = await ReadRawAsync();
        if (Chan is null) return f;
        Assert.Equal(FrameType.Sealed, f.Type);
        return Chan.Open(f.Payload.Span);
    }

    public async Task SendRawAsync(FrameType t, ReadOnlyMemory<byte> payload)
    {
        var buf = new byte[FrameCodec.HeaderSize + payload.Length];
        buf[0] = (byte)t;
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(1), (uint)payload.Length);
        payload.CopyTo(buf.AsMemory(FrameCodec.HeaderSize));
        await Phone.WriteAsync(buf, _cts.Token);
    }

    public Task SendAsync(FrameType t, ReadOnlyMemory<byte> payload) =>
        Chan is null ? SendRawAsync(t, payload) : SendRawAsync(FrameType.Sealed, Chan.Seal(t, payload.Span));

    public Task SendPhoneHelloAsync() =>
        SendRawAsync(FrameType.Hello, Messages.Hello("phone"));

    /// <summary>HELLO exchange, then the phone side of AUTH1..AUTH3. Leaves the phone sealed channel in <see cref="Chan"/>; no INFO sent.</summary>
    public async Task AuthenticateAsync()
    {
        StartRun();
        var hello = await ReadRawAsync();
        Assert.Equal(FrameType.Hello, hello.Type);
        await SendPhoneHelloAsync();
        var a1 = await ReadRawAsync();
        Assert.Equal(FrameType.Auth, a1.Type);
        PairingTokenCheck? tc = Pairing is null ? null
            : new PairingTokenCheck(Pairing.Token, Pairing.ExpiresAt, Identity.PublicKey, () => true);
        using var resp = new ResponderHandshake(PhoneKey, pub => pub.AsSpan().SequenceEqual(Identity.PublicKey), tc);
        var a2 = resp.ReadAuth1AndCreateAuth2(a1.Payload.ToArray());
        await SendRawAsync(FrameType.Auth, a2);
        var a3 = await ReadRawAsync();
        Assert.Equal(FrameType.Auth, a3.Type);
        resp.ReadAuth3(a3.Payload.ToArray());
        Chan = new SealedChannel(resp.Keys, isInitiator: false);
    }

    public Task SendInfoAsync(string json = InfoJson) => SendAsync(FrameType.Info, Encoding.UTF8.GetBytes(json));

    /// <summary>Full connect; returns the first PING frame (sent right after confirmation).</summary>
    public async Task<Frame> HandshakeAsync()
    {
        await AuthenticateAsync();
        await SendInfoAsync();
        var ping = await ReadAsync();
        Assert.Equal(FrameType.Ping, ping.Type);
        return ping;
    }

    /// <summary>Round-trips a LOG frame so every earlier frame has been processed by the read loop.</summary>
    public async Task BarrierAsync()
    {
        var line = "barrier-" + Interlocked.Increment(ref _barrier);
        await SendAsync(FrameType.Log, Encoding.UTF8.GetBytes(line));
        await Sink.WaitForAsync("phone", line);
    }

    public async Task WaitDisconnectedAsync()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await DisconnectedSignal.WaitAsync(cts.Token);
    }

    public async ValueTask DisposeAsync()
    {
        await Session.DisposeAsync();
        Phone.Dispose();
        _cts.Cancel();
        Chan?.Dispose();
        Dir.Dispose();
    }
}

public class PhoneSessionTests
{
    static string Text(Frame f) => Encoding.UTF8.GetString(f.Payload.Span);

    [Fact]
    public async Task Session_SendsHelloFirst_AndParsesPhoneHello()
    {
        await using var h = new Harness();
        h.StartRun();
        var first = await h.ReadRawAsync();
        Assert.Equal(FrameType.Hello, first.Type);
        var hello = Messages.ParseHello(first.Payload.Span);
        Assert.Equal("pc", hello.Role);
        Assert.Equal(2, hello.Proto);
        Assert.Null(h.Session.Phone);
        Assert.Equal("{\"proto\":2,\"role\":\"pc\"}", Text(first));
    }

    [Fact]
    public async Task Session_FullHandshakeAgainstCSharpResponder()
    {
        await using var h = new Harness();
        await h.AuthenticateAsync();
        Assert.Null(h.Session.Phone); // not before the INFO frame opens
        await h.SendInfoAsync();
        var ping = await h.ReadAsync();
        Assert.Equal(FrameType.Ping, ping.Type);
        var phone = h.Session.Phone!;
        Assert.Equal("iPhone 15", phone.Device);
        Assert.Equal("iOS 18", phone.Os);
        Assert.Equal("RaaiCam", phone.App);
        Assert.Equal("1.0", phone.Version);
        Assert.Equal(h.PhoneKey.Public, h.Session.PhoneStaticKey);
        Assert.Empty(h.Reasons);
        Assert.Empty(h.PairedEvents); // mode 1 never raises Paired
    }

    [Fact]
    public async Task Session_PairingModeRaisesPaired()
    {
        await using var h = new Harness(LinkMode.Pairing);
        await h.HandshakeAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await h.PairedSignal.WaitAsync(cts.Token);
        var p = Assert.Single(h.PairedEvents);
        Assert.Equal(h.PhoneKey.Public, p.PublicKey);
        Assert.Equal("iPhone 15", p.Label);
        Assert.Equal(h.PhoneKey.Public, h.Store.Phone!.PublicKey);
        Assert.True(h.Pairing!.IsUsed);
        // a fresh store instance sees the pairing too
        Assert.Equal(h.PhoneKey.Public, new PairingStore(h.Dir.File("pairing.bin"), new FakeProtector()).Phone!.PublicKey);
    }

    [Fact]
    public async Task Session_PairingSavesPhoneOnlyAfterConfirmation()
    {
        await using var h = new Harness(LinkMode.Pairing);
        await h.AuthenticateAsync();
        // AUTH3 sent and verified by the phone, but no INFO yet: nothing stored, nothing raised
        await Task.Delay(200);
        Assert.Null(h.Store.Phone);
        Assert.Empty(h.PairedEvents);
        Assert.Null(h.Session.Phone);
        await h.SendInfoAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await h.PairedSignal.WaitAsync(cts.Token);
        Assert.NotNull(h.Store.Phone);
    }

    [Fact]
    public async Task Session_PairingNotSavedWhenConfirmationFails()
    {
        await using var h = new Harness(LinkMode.Pairing);
        await h.AuthenticateAsync();
        await h.SendAsync(FrameType.Log, "W nope"u8.ToArray()); // first frame is not INFO
        await h.WaitDisconnectedAsync();
        Assert.Null(h.Store.Phone);
        Assert.Empty(h.PairedEvents);
    }

    [Fact]
    public async Task Session_PairingWithExpiredTokenNeverConnects()
    {
        using var dir = new TempDir();
        var store = new PairingStore(dir.File("pairing.bin"), new FakeProtector());
        var clock = new ManualClock();
        var pairing = PairingSession.Begin(store.GetOrCreateIdentity(), "PC", clock);
        clock.Advance(TimeSpan.FromMinutes(6));
        var (client, server) = DuplexStream.CreatePair();
        var sink = new ListLogSink();
        await using var s = new PhoneSession(client, () => 0, sink,
            new SecureLinkOptions(store.GetOrCreateIdentity(), store, null, pairing), new FakeTimeProvider());
        var reasons = new List<string>();
        s.Disconnected += r => { lock (reasons) reasons.Add(r); };
        await s.RunAsync(CancellationToken.None);
        server.Dispose();
        Assert.Single(reasons);
        Assert.Contains("expired", reasons[0]);
    }

    [Fact]
    public async Task Session_FirstFrameNotInfo_Disconnects()
    {
        await using var h = new Harness();
        await h.AuthenticateAsync();
        await h.SendAsync(FrameType.Log, "I hi"u8.ToArray());
        await h.WaitDisconnectedAsync();
        await h.Run;
        Assert.Contains("protocol", Assert.Single(h.Reasons));
        Assert.Null(h.Session.Phone);
    }

    [Fact]
    public async Task Session_UnconfirmedTimeout_Disconnects()
    {
        await using var h = new Harness();
        await h.AuthenticateAsync();
        await h.Time.WaitForTimerAsync(TimeSpan.FromSeconds(5));
        h.Time.Advance(TimeSpan.FromSeconds(5));
        await h.WaitDisconnectedAsync();
        await h.Run;
        Assert.Equal(new[] { "not confirmed" }, h.Reasons);
        Assert.Null(h.Session.Phone);
    }

    [Fact]
    public async Task Session_HandshakeTimeout_Disconnects()
    {
        await using var h = new Harness();
        h.StartRun();
        await h.ReadRawAsync();
        await h.SendPhoneHelloAsync();
        var a1 = await h.ReadRawAsync();
        Assert.Equal(FrameType.Auth, a1.Type);
        await h.Time.WaitForTimerAsync(TimeSpan.FromSeconds(5));
        h.Time.Advance(TimeSpan.FromSeconds(5));
        await h.WaitDisconnectedAsync();
        await h.Run;
        Assert.Equal(new[] { "handshake timeout" }, h.Reasons);
    }

    [Fact]
    public async Task Session_PlainFrameAfterAuth_Disconnects()
    {
        await using var h = new Harness();
        await h.AuthenticateAsync();
        await h.SendRawAsync(FrameType.Log, "I hi"u8.ToArray());
        await h.WaitDisconnectedAsync();
        await h.Run;
        Assert.Contains("protocol", Assert.Single(h.Reasons));
    }

    [Fact]
    public async Task Session_PairedPhoneKeyMismatch_Disconnects()
    {
        await using var h = new Harness(LinkMode.PairedWithOtherPhoneKey);
        h.StartRun();
        await h.ReadRawAsync();
        await h.SendPhoneHelloAsync();
        var a1 = await h.ReadRawAsync();
        using var resp = new ResponderHandshake(h.PhoneKey, pub => true, null);
        await h.SendRawAsync(FrameType.Auth, resp.ReadAuth1AndCreateAuth2(a1.Payload.ToArray()));
        await h.WaitDisconnectedAsync();
        await h.Run;
        Assert.Contains("protocol", Assert.Single(h.Reasons));
        Assert.Null(h.Session.Phone);
    }

    [Fact]
    public async Task Session_TamperedSealedFrameDisconnects()
    {
        await using var h = new Harness();
        await h.HandshakeAsync();
        var sealedPayload = h.Chan!.Seal(FrameType.Log, "I fine"u8.ToArray());
        sealedPayload[^1] ^= 0x01;
        await h.SendRawAsync(FrameType.Sealed, sealedPayload);
        await h.WaitDisconnectedAsync();
        await h.Run;
        Assert.Contains("protocol", Assert.Single(h.Reasons));
    }

    [Fact]
    public async Task Session_RouteCommandSealed()
    {
        await using var h = new Harness();
        await h.HandshakeAsync();
        await h.Session.SendRouteAsync(true);
        var f = await h.ReadAsync();
        Assert.Equal(FrameType.Cmd, f.Type);
        Assert.Equal("{\"cmd\":\"route\",\"video\":true}", Text(f));
        await h.Session.SendRouteAsync(false);
        Assert.Equal("{\"cmd\":\"route\",\"video\":false}", Text(await h.ReadAsync()));
    }

    [Fact]
    public async Task Session_UnpairSendsCommandThenRemovesPhone()
    {
        await using var h = new Harness();
        await h.HandshakeAsync();
        Assert.NotNull(h.Store.Phone);
        await h.Session.UnpairAsync();
        var f = await h.ReadAsync();
        Assert.Equal("{\"cmd\":\"unpair\"}", Text(f));
        Assert.Null(h.Store.Phone);
        Assert.NotNull(new PairingStore(h.Dir.File("pairing.bin"), new FakeProtector()).GetOrCreateIdentity());
        Assert.Null(new PairingStore(h.Dir.File("pairing.bin"), new FakeProtector()).Phone);
    }

    [Fact]
    public async Task Session_StartSendsExactJson()
    {
        await using var h = new Harness();
        await h.HandshakeAsync();
        await h.Session.StartAsync(1920, 1080);
        var f = await h.ReadAsync();
        Assert.Equal(FrameType.Cmd, f.Type);
        Assert.Equal("{\"cmd\":\"start\",\"width\":1920,\"height\":1080,\"fps\":30,\"lens\":\"wide\",\"bitrateKbps\":8000}", Text(f));

        await h.Session.StopAsync();
        Assert.Equal("{\"cmd\":\"stop\"}", Text(await h.ReadAsync()));
        await h.Session.RequestKeyframeAsync();
        Assert.Equal("{\"cmd\":\"keyframe\"}", Text(await h.ReadAsync()));
        await h.Session.SetBitrateAsync(6000);
        Assert.Equal("{\"cmd\":\"bitrate\",\"kbps\":6000}", Text(await h.ReadAsync()));
        await h.Session.SetLensAsync("ultra");
        Assert.Equal("{\"cmd\":\"lens\",\"lens\":\"ultra\"}", Text(await h.ReadAsync()));
    }

    [Fact]
    public async Task Session_ConcurrentWrites_AreNotInterleaved()
    {
        await using var h = new Harness();
        await h.HandshakeAsync();
        // Seal + write under one lock: the phone's strict counter check fails if frames hit the wire out of order.
        await Task.WhenAll(Enumerable.Range(0, 50).Select(i => Task.Run(() => h.Session.SetBitrateAsync(1000 + i))));
        var prefix = "{\"cmd\":\"bitrate\",\"kbps\":";
        var seen = new HashSet<int>();
        for (int i = 0; i < 50; i++)
        {
            var f = await h.ReadAsync();
            Assert.Equal(FrameType.Cmd, f.Type);
            var json = Text(f);
            Assert.StartsWith(prefix, json);
            seen.Add(int.Parse(json[prefix.Length..^1]));
        }
        Assert.Equal(50, seen.Count);
    }

    [Fact]
    public async Task Session_VideoEventCarriesAccessUnit()
    {
        await using var h = new Harness();
        var got = new TaskCompletionSource<VideoFrame>();
        h.Session.Video += v => got.TrySetResult(v);
        await h.HandshakeAsync();

        var au = new byte[] { 0, 0, 0, 1, 0x65, 9, 8, 7 };
        var payload = new byte[13 + au.Length];
        BinaryPrimitives.WriteUInt64BigEndian(payload, 123456789UL);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(8), 42);
        payload[12] = 1;
        au.CopyTo(payload, 13);
        await h.SendAsync(FrameType.Video, payload);

        var v = await got.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(123456789UL, v.CaptureUs);
        Assert.Equal(42u, v.Seq);
        Assert.True(v.Keyframe);
        Assert.True(v.AccessUnit.Span.SequenceEqual(au));
    }

    [Fact]
    public async Task Session_Telemetry_RaisedWithDefaultsForMissingStrings()
    {
        await using var h = new Harness();
        var got = new TaskCompletionSource<Telemetry>();
        h.Session.TelemetryReceived += t => got.TrySetResult(t);
        await h.HandshakeAsync();
        await h.SendAsync(FrameType.Telemetry, Encoding.UTF8.GetBytes("{\"battery\":0.5,\"streaming\":true,\"width\":1920,\"height\":1080}"));
        var t = await got.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("unknown", t.Thermal);
        Assert.Equal("wide", t.Lens);
        Assert.Equal(0.5, t.Battery);
        Assert.True(t.Streaming);
    }

    static ulong PingSendUs(Frame ping)
    {
        Assert.Equal(8, ping.Payload.Length);
        return BinaryPrimitives.ReadUInt64BigEndian(ping.Payload.Span);
    }

    static byte[] PongPayload(ulong send, ulong phoneRecv)
    {
        var b = new byte[16];
        BinaryPrimitives.WriteUInt64BigEndian(b, send);
        BinaryPrimitives.WriteUInt64BigEndian(b.AsSpan(8), phoneRecv);
        return b;
    }

    [Fact]
    public async Task Session_PingEverySecond_UpdatesClock()
    {
        await using var h = new Harness();
        var ping1 = await h.HandshakeAsync();
        Assert.Equal(1_000_000UL, PingSendUs(ping1));

        h.NowUs = 1_002_000; // PC receive time
        await h.SendAsync(FrameType.Pong, PongPayload(1_000_000, 5_000_000));
        await h.BarrierAsync();
        Assert.Equal(3_999_000, h.Session.Clock.OffsetUs); // 5_000_000 - (1_000_000 + 1000)
        Assert.Equal(2.0, h.Session.Clock.RttMs);

        h.NowUs = 2_000_000;
        await h.Time.WaitForTimerAsync(TimeSpan.FromSeconds(1));
        h.Time.Advance(TimeSpan.FromSeconds(1));
        var ping2 = await h.ReadAsync();
        Assert.Equal(FrameType.Ping, ping2.Type);
        Assert.Equal(2_000_000UL, PingSendUs(ping2));
    }

    [Fact]
    public async Task Session_PeerClosed_RaisesDisconnected()
    {
        await using var h = new Harness();
        await h.HandshakeAsync();
        h.Phone.Dispose();
        await h.WaitDisconnectedAsync();
        await h.Run;
        Assert.Single(h.Reasons);
        Assert.False(string.IsNullOrEmpty(h.Reasons[0]));
    }

    [Fact]
    public async Task Session_ProtocolError_DisconnectsOnce()
    {
        await using var h = new Harness();
        await h.HandshakeAsync();
        await h.SendAsync(FrameType.Pong, new byte[3]); // PONG must be 16 bytes
        await h.WaitDisconnectedAsync();
        await h.Run;
        h.Phone.Dispose();
        await h.Session.DisposeAsync();
        Assert.Single(h.Reasons);
        Assert.Contains("protocol", h.Reasons[0]);
    }

    [Fact]
    public async Task Session_NonHelloFirst_IsProtocolError()
    {
        await using var h = new Harness();
        h.StartRun();
        await h.ReadRawAsync();
        await h.SendRawAsync(FrameType.Log, new byte[] { 104, 105 });
        await h.WaitDisconnectedAsync();
        await h.Run;
        Assert.Single(h.Reasons);
        Assert.Contains("protocol", h.Reasons[0]);
        Assert.Null(h.Session.Phone);
    }

    [Fact]
    public async Task Session_NoHelloWithin5s_Disconnects()
    {
        await using var h = new Harness();
        h.StartRun();
        await h.ReadRawAsync();
        await h.Time.WaitForTimerAsync(TimeSpan.FromSeconds(5));
        h.Time.Advance(TimeSpan.FromSeconds(5));
        await h.WaitDisconnectedAsync();
        await h.Run;
        Assert.Equal(new[] { "no hello" }, h.Reasons);
    }

    [Fact]
    public async Task Session_DisposeFiresOnce_AndLaterCallsAreNoOps()
    {
        var h = new Harness();
        await h.HandshakeAsync();
        await h.Session.DisposeAsync();
        await h.Session.DisposeAsync();
        await h.Run;
        Assert.Single(h.Reasons);
        await h.Session.StartAsync(1280, 720);
        await h.Session.StopAsync();
        await h.Session.RequestKeyframeAsync();
        await h.Session.SetBitrateAsync(1);
        await h.Session.SetLensAsync("wide");
        await h.Session.SendRouteAsync(true);
        Assert.Single(h.Reasons);
        h.Phone.Dispose();
        await h.DisposeAsync();
    }

    [Fact]
    public async Task Session_ThrowingHandler_DoesNotKillSession()
    {
        await using var h = new Harness();
        var second = new TaskCompletionSource<Telemetry>();
        h.Session.TelemetryReceived += _ => throw new InvalidOperationException("boom");
        h.Session.TelemetryReceived += t => second.TrySetResult(t);
        await h.HandshakeAsync();
        await h.SendAsync(FrameType.Telemetry, Encoding.UTF8.GetBytes("{\"thermal\":\"fair\",\"lens\":\"ultra\"}"));
        await second.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await h.BarrierAsync();
        Assert.Empty(h.Reasons);
        Assert.Contains(h.Sink.Snapshot(), l => l.Source == "pc" && l.Line.Contains("boom"));
    }

    [Fact]
    public async Task Session_LogLinesGoToSink()
    {
        await using var h = new Harness();
        await h.HandshakeAsync();
        await h.SendAsync(FrameType.Log, Encoding.UTF8.GetBytes("W camera interrupted"));
        await h.Sink.WaitForAsync("phone", "W camera interrupted");
        Assert.Contains(h.Sink.Snapshot(), l => l.Source == "pc");
    }

    [Fact]
    public async Task Session_NeverLogsKeyMaterial()
    {
        await using var h = new Harness(LinkMode.Pairing);
        await h.HandshakeAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await h.PairedSignal.WaitAsync(cts.Token);
        var all = string.Join("\n", h.Sink.Snapshot().Select(l => l.Line));
        Assert.DoesNotContain(Convert.ToBase64String(h.Pairing!.Token), all);
        Assert.DoesNotContain(Convert.ToHexString(h.Pairing.Token), all, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Convert.ToBase64String(h.Identity.PrivateKey), all);
        Assert.DoesNotContain("raaicam://", all);
    }
}
