using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using RaaiCam.Protocol;
using RaaiCam.Protocol.Crypto;
using Xunit;

namespace RaaiCam.Protocol.Tests;

public class SealedChannelTests
{
    static SessionKeys Keys() => new(RandomNumberGenerator.GetBytes(32), RandomNumberGenerator.GetBytes(32));

    static (SealedChannel i, SealedChannel r) Pair()
    {
        var k = Keys();
        return (new SealedChannel(k, true), new SealedChannel(k, false));
    }

    static readonly byte[] Stop = Encoding.UTF8.GetBytes("{\"cmd\":\"stop\"}");

    [Fact]
    public void Vectors_SealedFrameMatches()
    {
        var v = V2Vectors.Root.GetProperty("sealed");
        var m = V2Vectors.Root.GetProperty("pairedMode");
        var keys = new SessionKeys(V2Vectors.Hex(m, "kI2R"), V2Vectors.Hex(m, "kR2I"));
        var inner = Encoding.UTF8.GetBytes(v.GetProperty("innerPayloadUtf8").GetString()!);
        var payload = new SealedChannel(keys, true).Seal((FrameType)v.GetProperty("innerType").GetInt32(), inner);
        Assert.Equal(V2Vectors.Hex(v, "payloadHex"), payload);

        var f = new SealedChannel(keys, false).Open(payload);
        Assert.Equal(FrameType.Cmd, f.Type);
        Assert.Equal(inner, f.Payload.ToArray());
    }

    [Fact]
    public void Cipher_RoundTripBothDirections()
    {
        var (i, r) = Pair();
        var f = r.Open(i.Seal(FrameType.Cmd, Stop));
        Assert.Equal(FrameType.Cmd, f.Type);
        Assert.Equal(Stop, f.Payload.ToArray());
        var g = i.Open(r.Seal(FrameType.Telemetry, new byte[] { 1, 2, 3 }));
        Assert.Equal(FrameType.Telemetry, g.Type);
        Assert.Equal(new byte[] { 1, 2, 3 }, g.Payload.ToArray());
        Assert.Equal(FrameType.Ping, r.Open(i.Seal(FrameType.Ping, [])).Type);
    }

    [Fact]
    public void Cipher_TamperClosesSession()
    {
        var (i, r) = Pair();
        var good0 = i.Seal(FrameType.Cmd, Stop);
        var bad = (byte[])good0.Clone(); bad[12] ^= 0x80;
        Assert.Throws<CryptoProtocolException>(() => r.Open(bad));
        // Session is closed: even the untampered frame is refused afterwards, and sealing fails too.
        Assert.Throws<CryptoProtocolException>(() => r.Open(good0));
        Assert.Throws<CryptoProtocolException>(() => r.Seal(FrameType.Cmd, Stop));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(-1)]
    public void Cipher_TamperedCounterCiphertextOrTagRejected(int idx)
    {
        var (i, r) = Pair();
        var p = i.Seal(FrameType.Cmd, Stop);
        p[idx < 0 ? p.Length - 1 : idx] ^= 1;
        Assert.Throws<CryptoProtocolException>(() => r.Open(p));
    }

    [Fact]
    public void Cipher_NoncesStrictlyIncrease()
    {
        var (i, _) = Pair();
        ulong prev = 0;
        var seen = new HashSet<string>();
        for (int n = 0; n < 50; n++)
        {
            var p = i.Seal(FrameType.Cmd, Stop);
            ulong c = BinaryPrimitives.ReadUInt64BigEndian(p);
            Assert.Equal((ulong)n, c);
            if (n > 0) Assert.True(c > prev);
            prev = c;
            Assert.True(seen.Add(Convert.ToHexString(p))); // identical plaintext never yields identical output
        }
    }

    [Fact]
    public void Cipher_CounterRegressionRejected()
    {
        var (i1, r1) = Pair();
        var a0 = i1.Seal(FrameType.Cmd, Stop);
        var a1 = i1.Seal(FrameType.Cmd, Stop);
        r1.Open(a0); r1.Open(a1);
        Assert.Throws<CryptoProtocolException>(() => r1.Open(a1)); // replay

        var (i2, r2) = Pair();
        i2.Seal(FrameType.Cmd, Stop);
        var q1 = i2.Seal(FrameType.Cmd, Stop);
        Assert.Throws<CryptoProtocolException>(() => r2.Open(q1)); // skip ahead
    }

    [Fact]
    public void Cipher_ReflectedFrameRejected()
    {
        var k = Keys();
        var p = new SealedChannel(k, true).Seal(FrameType.Cmd, Stop);
        // A same-role peer (reflection) cannot open it: direction keys/nonces differ.
        Assert.Throws<CryptoProtocolException>(() => new SealedChannel(k, true).Open(p));
    }

    [Fact]
    public void Cipher_ForbiddenInnerTypesRejected()
    {
        var (i, _) = Pair();
        Assert.Throws<CryptoProtocolException>(() => i.Seal(FrameType.Hello, []));
        Assert.Throws<CryptoProtocolException>(() => i.Seal(FrameType.Auth, []));
        Assert.Throws<CryptoProtocolException>(() => i.Seal(FrameType.Sealed, []));
    }

    [Fact]
    public void Cipher_TruncatedPayloadRejected()
    {
        var (_, r) = Pair();
        Assert.Throws<CryptoProtocolException>(() => r.Open(new byte[10]));
    }
}
