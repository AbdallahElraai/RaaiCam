using System.Buffers.Binary;
using System.Text;
using RaaiCam.Core.Phone;
using RaaiCam.Protocol;

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

/// <summary>Session under test plus the scripted phone end of an in-memory transport.</summary>
sealed class Harness : IAsyncDisposable
{
    public readonly PhoneSession Session;
    public readonly Stream Phone;
    public readonly FakeTimeProvider Time = new();
    public readonly ListLogSink Sink = new();
    public ulong NowUs = 1_000_000;
    public readonly List<string> Reasons = new();
    public readonly SemaphoreSlim DisconnectedSignal = new(0);
    public Task Run = Task.CompletedTask;
    readonly CancellationTokenSource _cts = new(TimeSpan.FromSeconds(20));
    int _barrier;

    public Harness()
    {
        var (client, server) = DuplexStream.CreatePair();
        Phone = server;
        Session = new PhoneSession(client, () => NowUs, Sink, Time);
        Session.Disconnected += r =>
        {
            lock (Reasons) Reasons.Add(r);
            DisconnectedSignal.Release();
        };
    }

    public void StartRun() => Run = Session.RunAsync(_cts.Token);

    public async Task<Frame> ReadAsync()
    {
        var f = await FrameCodec.ReadAsync(Phone, _cts.Token);
        return f ?? throw new InvalidOperationException("session closed transport");
    }

    public async Task SendAsync(FrameType t, ReadOnlyMemory<byte> payload)
    {
        var buf = new byte[FrameCodec.HeaderSize + payload.Length];
        buf[0] = (byte)t;
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(1), (uint)payload.Length);
        payload.CopyTo(buf.AsMemory(FrameCodec.HeaderSize));
        await Phone.WriteAsync(buf, _cts.Token);
    }

    public Task SendPhoneHelloAsync() =>
        SendAsync(FrameType.Hello, Messages.Hello("phone"));

    /// <summary>HELLO exchange; returns the first PING frame (sent right after the exchange).</summary>
    public async Task<Frame> HandshakeAsync()
    {
        StartRun();
        var hello = await ReadAsync();
        Assert.Equal(FrameType.Hello, hello.Type);
        await SendPhoneHelloAsync();
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
        var first = await h.ReadAsync();
        Assert.Equal(FrameType.Hello, first.Type);
        var hello = Messages.ParseHello(first.Payload.Span);
        Assert.Equal("pc", hello.Role);
        Assert.Equal(2, hello.Proto);
        Assert.Null(h.Session.Phone);

        await h.SendPhoneHelloAsync();
        Assert.Equal(FrameType.Ping, (await h.ReadAsync()).Type);
        Assert.Equal("phone", h.Session.Phone!.Role);
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
        await h.ReadAsync();
        await h.SendAsync(FrameType.Log, new byte[] { 104, 105 });
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
        await h.ReadAsync();
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
        Assert.Single(h.Reasons);
        h.Phone.Dispose();
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
}
