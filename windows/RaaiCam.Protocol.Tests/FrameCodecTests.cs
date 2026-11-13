using System.Text;
using System.Text.Json;
using RaaiCam.Protocol;
using Xunit;

namespace RaaiCam.Protocol.Tests;

public class FrameCodecTests
{
    record Vector(string Name, FrameType Type, byte[] Payload, byte[] Frame);

    static byte[] FromHex(string h) =>
        h.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(x => Convert.ToByte(x, 16)).ToArray();

    static List<Vector> LoadVectors()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "test-vectors.json")));
        var list = new List<Vector>();
        foreach (var f in doc.RootElement.GetProperty("frames").EnumerateArray())
        {
            var payload = f.TryGetProperty("payloadUtf8", out var u)
                ? Encoding.UTF8.GetBytes(u.GetString()!)
                : FromHex(f.GetProperty("payloadHex").GetString()!);
            list.Add(new Vector(f.GetProperty("name").GetString()!, (FrameType)f.GetProperty("type").GetInt32(),
                payload, FromHex(f.GetProperty("hex").GetString()!)));
        }
        return list;
    }

    static Vector V(string name) => LoadVectors().Single(x => x.Name == name);

    sealed class HeaderOnlyStream(byte[] header) : Stream
    {
        int _pos;
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            if (_pos >= header.Length) throw new InvalidOperationException("read past header");
            int n = Math.Min(buffer.Length, header.Length - _pos);
            header.AsSpan(_pos, n).CopyTo(buffer);
            _pos += n;
            return n;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => new(Read(buffer.Span));
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task Vectors_RoundTrip()
    {
        var vectors = LoadVectors();
        Assert.True(vectors.Count >= 9);
        foreach (var v in vectors)
        {
            var ms = new MemoryStream();
            FrameCodec.Write(ms, v.Type, v.Payload);
            Assert.True(v.Frame.AsSpan().SequenceEqual(ms.ToArray()), v.Name);

            var frame = await FrameCodec.ReadAsync(new MemoryStream(v.Frame), CancellationToken.None);
            Assert.NotNull(frame);
            Assert.Equal(v.Type, frame!.Value.Type);
            Assert.True(v.Payload.AsSpan().SequenceEqual(frame.Value.Payload.Span), v.Name);
        }
    }

    [Fact]
    public async Task Read_NullOnCleanEof()
    {
        Assert.Null(await FrameCodec.ReadAsync(new MemoryStream(), CancellationToken.None));
    }

    [Fact]
    public async Task Read_ThrowsOnTruncatedPayload()
    {
        var bytes = new byte[] { 5, 0, 0, 0, 10, 1, 2, 3, 4 };
        await Assert.ThrowsAsync<ProtocolException>(async () =>
            await FrameCodec.ReadAsync(new MemoryStream(bytes), CancellationToken.None));
    }

    [Fact]
    public async Task Codec_RejectsOversizedLength()
    {
        uint len = FrameCodec.MaxPayload + 1;
        var header = new byte[] { 6, (byte)(len >> 24), (byte)(len >> 16), (byte)(len >> 8), (byte)len };
        await Assert.ThrowsAsync<ProtocolException>(async () =>
            await FrameCodec.ReadAsync(new HeaderOnlyStream(header), CancellationToken.None));
        Assert.Throws<ProtocolException>(() =>
            FrameCodec.Write(new MemoryStream(), FrameType.Video, new byte[FrameCodec.MaxPayload + 1]));
    }

    [Fact]
    public void Video_ParsesHeaderAndKeyframeFlag()
    {
        var v = V("video-keyframe");
        var vf = Messages.ParseVideo(v.Payload);
        Assert.Equal(0x0102030405060708UL, vf.CaptureUs);
        Assert.Equal(42u, vf.Seq);
        Assert.True(vf.Keyframe);
        Assert.Equal(16, vf.AccessUnit.Length);
        Assert.Equal(new byte[] { 0, 0, 0, 1, 0x67 }, vf.AccessUnit.Span[..5].ToArray());

        var p = (byte[])v.Payload.Clone();
        p[12] = 0;
        Assert.False(Messages.ParseVideo(p).Keyframe);
        Assert.Throws<ProtocolException>(() => Messages.ParseVideo(new byte[12]));
    }

    [Fact]
    public void Pong_ParsesBothTimes()
    {
        var (pc, phone) = Messages.ParsePong(V("pong").Payload);
        Assert.Equal(1000000UL, pc);
        Assert.Equal(2000000UL, phone);
        Assert.True(V("ping").Payload.AsSpan().SequenceEqual(Messages.Ping(1000000)));
    }

    [Fact]
    public void Hello_RejectsWrongProto()
    {
        var bad = Encoding.UTF8.GetBytes("{\"proto\":1,\"role\":\"phone\",\"app\":\"RaaiCam\",\"version\":\"1.0.0\",\"device\":\"x\",\"os\":\"y\"}");
        Assert.Throws<ProtocolException>(() => Messages.ParseHello(bad));

        var v = V("hello-pc");
        var hi = Messages.ParseHello(v.Payload);
        Assert.Equal(2, hi.Proto);
        Assert.Equal("pc", hi.Role);
        Assert.True(v.Payload.AsSpan().SequenceEqual(Messages.Hello("pc")));
    }

    [Fact]
    public void Cmd_StartSerializesExactJson()
    {
        var json = Encoding.UTF8.GetString(Messages.Cmd(new StartCmd(1920, 1080, 30, "wide", 8000)));
        Assert.Equal("{\"cmd\":\"start\",\"width\":1920,\"height\":1080,\"fps\":30,\"lens\":\"wide\",\"bitrateKbps\":8000}", json);
        Assert.Equal("{\"cmd\":\"stop\"}", Encoding.UTF8.GetString(Messages.Cmd(new StopCmd())));
        Assert.Equal("{\"cmd\":\"keyframe\"}", Encoding.UTF8.GetString(Messages.Cmd(new KeyframeCmd())));
        Assert.Equal("{\"cmd\":\"bitrate\",\"kbps\":6000}", Encoding.UTF8.GetString(Messages.Cmd(new BitrateCmd(6000))));
        Assert.Equal("{\"cmd\":\"lens\",\"lens\":\"ultra\"}", Encoding.UTF8.GetString(Messages.Cmd(new LensCmd("ultra"))));
        Assert.True(V("cmd-start").Payload.AsSpan().SequenceEqual(Messages.Cmd(new StartCmd(1920, 1080, 30, "wide", 8000))));
    }

    [Fact]
    public void Telemetry_Parses()
    {
        var t = Messages.ParseTelemetry(V("telemetry").Payload);
        Assert.Equal("nominal", t.Thermal);
        Assert.Equal(0.85, t.Battery);
        Assert.True(t.Charging && t.Streaming && !t.Paused);
        Assert.Equal(1920, t.Width);
        Assert.Equal(29.9, t.FpsEncode);
        Assert.Equal(8000, t.BitrateKbps);
        Assert.Equal("wide", t.Lens);
    }
}
