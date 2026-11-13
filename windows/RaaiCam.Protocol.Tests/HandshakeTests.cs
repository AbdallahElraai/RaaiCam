using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RaaiCam.Protocol;
using RaaiCam.Protocol.Crypto;
using Xunit;

namespace RaaiCam.Protocol.Tests;

public sealed class FakeTime(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now = now;
    public override DateTimeOffset GetUtcNow() => Now;
}

public static class V2Vectors
{
    public static JsonElement Root { get; } =
        JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "test-vectors-v2.json"))).RootElement;

    public static byte[] Hex(JsonElement parent, string name) => Convert.FromHexString(parent.GetProperty(name).GetString()!);
    public static byte[] Hex(string name) => Hex(Root.GetProperty("keys"), name);
}

public class HandshakeTests
{
    static readonly DateTimeOffset T0 = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);

    sealed class Rig
    {
        public StaticKey Pc = StaticKey.Generate();
        public StaticKey Phone = StaticKey.Generate();
        public byte[] PcId = RandomNumberGenerator.GetBytes(16);
        public byte[] Token = RandomNumberGenerator.GetBytes(16);
        public FakeTime Time = new(T0);
        public int MarkUsedCalls;
        public PairingTokenCheck TokenCheck() =>
            new(Token, T0.AddMinutes(5), Pc.Public, () => { MarkUsedCalls++; return true; }, Time);
        public InitiatorHandshake Paired() => new(Pc, PcId, Phone.Public, null);
        public InitiatorHandshake Pairing(byte[]? token = null) => new(Pc, PcId, null, token ?? Token);
        public ResponderHandshake Resp(PairingTokenCheck? tc = null, bool pcKnown = true) =>
            new(Phone, pub => pcKnown && pub.AsSpan().SequenceEqual(Pc.Public), tc);
    }

    static void Complete(InitiatorHandshake i, ResponderHandshake r)
    {
        var a2 = r.ReadAuth1AndCreateAuth2(i.CreateAuth1());
        i.ReadAuth2(a2);
        r.ReadAuth3(i.CreateAuth3());
    }

    // ---- vectors ----

    static (InitiatorHandshake i, ResponderHandshake r) VectorPair(bool pairing)
    {
        var sI = StaticKey.FromPrivate(V2Vectors.Hex("sIPriv"));
        var eI = StaticKey.FromPrivate(V2Vectors.Hex("eIPriv"));
        var sR = StaticKey.FromPrivate(V2Vectors.Hex("sRPriv"));
        var eR = StaticKey.FromPrivate(V2Vectors.Hex("eRPriv"));
        var pcId = V2Vectors.Hex("pcId");
        var token = V2Vectors.Hex("token");
        var i = pairing
            ? new InitiatorHandshake(sI, pcId, null, token, eI)
            : new InitiatorHandshake(sI, pcId, sR.Public, null, eI);
        PairingTokenCheck? tc = pairing
            ? new PairingTokenCheck(token, DateTimeOffset.UtcNow.AddMinutes(5), sI.Public, () => true)
            : null;
        var r = new ResponderHandshake(sR, pub => pub.AsSpan().SequenceEqual(sI.Public), tc, eR);
        return (i, r);
    }

    [Theory]
    [InlineData(false, "pairedMode")]
    [InlineData(true, "pairingMode")]
    public void Vectors_HandshakeBytesMatch(bool pairing, string mode)
    {
        var v = V2Vectors.Root.GetProperty(mode);
        var (i, r) = VectorPair(pairing);
        var a1 = i.CreateAuth1();
        Assert.Equal(V2Vectors.Hex(v, "auth1"), a1);
        var a2 = r.ReadAuth1AndCreateAuth2(a1);
        Assert.Equal(V2Vectors.Hex(v, "auth2"), a2);
        i.ReadAuth2(a2);
        var a3 = i.CreateAuth3();
        Assert.Equal(V2Vectors.Hex(v, "auth3"), a3);
        r.ReadAuth3(a3);
    }

    [Theory]
    [InlineData(false, "pairedMode")]
    [InlineData(true, "pairingMode")]
    public void Vectors_SessionKeysMatch(bool pairing, string mode)
    {
        var v = V2Vectors.Root.GetProperty(mode);
        var (i, r) = VectorPair(pairing);
        Complete(i, r);
        Assert.Equal(V2Vectors.Hex(v, "kI2R"), i.Keys.InitiatorToResponder);
        Assert.Equal(V2Vectors.Hex(v, "kR2I"), i.Keys.ResponderToInitiator);
        Assert.Equal(i.Keys.InitiatorToResponder, r.Keys.InitiatorToResponder);
        Assert.Equal(i.Keys.ResponderToInitiator, r.Keys.ResponderToInitiator);
    }

    // ---- auth ----

    [Fact]
    public void Auth_PairedModeSucceeds()
    {
        var g = new Rig();
        var i = g.Paired(); var r = g.Resp();
        Complete(i, r);
        Assert.False(r.WasPairing);
        Assert.Equal(g.Pc.Public, r.PeerStatic);
        Assert.Equal(i.Keys.InitiatorToResponder, r.Keys.InitiatorToResponder);
        Assert.NotEqual(i.Keys.InitiatorToResponder, i.Keys.ResponderToInitiator);
    }

    [Fact]
    public void Auth_PairingModeSucceeds_StoresPeer()
    {
        var g = new Rig();
        var i = g.Pairing(); var r = g.Resp(g.TokenCheck());
        var a2 = r.ReadAuth1AndCreateAuth2(i.CreateAuth1());
        Assert.Equal(g.Phone.Public, i.ReadAuth2(a2));
        r.ReadAuth3(i.CreateAuth3());
        Assert.True(r.WasPairing);
        Assert.Equal(g.Pc.Public, r.PeerStatic);
        Assert.Equal(i.Keys.ResponderToInitiator, r.Keys.ResponderToInitiator);
        Assert.Equal(1, g.MarkUsedCalls);
    }

    [Fact]
    public void Auth_UnknownPcRejected()
    {
        var g = new Rig();
        var r = g.Resp(pcKnown: false);
        Assert.Throws<CryptoProtocolException>(() => r.ReadAuth1AndCreateAuth2(g.Paired().CreateAuth1()));
    }

    [Fact]
    public void Auth_PairedMode_WrongPhoneKeyRejectedByInitiator()
    {
        var g = new Rig();
        var i = new InitiatorHandshake(g.Pc, g.PcId, StaticKey.Generate().Public, null);
        var r = g.Resp();
        var a2 = r.ReadAuth1AndCreateAuth2(i.CreateAuth1());
        Assert.Throws<CryptoProtocolException>(() => i.ReadAuth2(a2));
    }

    [Fact]
    public void Auth_ReplayedTranscriptRejected()
    {
        var g = new Rig();
        // Record a full honest session.
        var i1 = g.Paired(); var r1 = g.Resp();
        var auth1 = i1.CreateAuth1();
        i1.ReadAuth2(r1.ReadAuth1AndCreateAuth2(auth1));
        var auth3 = i1.CreateAuth3();
        r1.ReadAuth3(auth3);

        // Attacker replays AUTH1 and AUTH3: fresh responder ephemeral => AUTH3 does not verify.
        var r2 = g.Resp();
        r2.ReadAuth1AndCreateAuth2(auth1);
        Assert.Throws<CryptoProtocolException>(() => r2.ReadAuth3(auth3));
        Assert.Throws<CryptoProtocolException>(() => _ = r2.Keys);
    }

    [Fact]
    public void Auth_ReflectedMessageRejected()
    {
        var g = new Rig();
        // Initiator's own AUTH1 reflected back as AUTH2.
        var i = g.Paired();
        var a1 = i.CreateAuth1();
        Assert.Throws<CryptoProtocolException>(() => i.ReadAuth2(a1));

        // Responder's AUTH2 reflected into a responder as AUTH1.
        var r = g.Resp();
        var a2 = g.Resp().ReadAuth1AndCreateAuth2(g.Paired().CreateAuth1());
        Assert.Throws<CryptoProtocolException>(() => r.ReadAuth1AndCreateAuth2(a2));

        // Out-of-order calls.
        var r3 = g.Resp();
        Assert.Throws<CryptoProtocolException>(() => r3.ReadAuth3(a1));
        var i3 = g.Paired();
        Assert.Throws<CryptoProtocolException>(() => i3.CreateAuth3());
    }

    [Fact]
    public void Auth_V1HelloRejected()
    {
        var v1 = Encoding.UTF8.GetBytes(
            "{\"proto\":1,\"role\":\"pc\",\"app\":\"RaaiCam\",\"version\":\"1.0.0\",\"device\":\"x\",\"os\":\"y\"}");
        Assert.Throws<ProtocolException>(() => Messages.ParseHello(v1));
        Assert.Throws<ProtocolException>(() => Messages.ParseHello("{\"role\":\"pc\"}"u8));
        Assert.Equal("{\"proto\":2,\"role\":\"pc\"}", Encoding.UTF8.GetString(Messages.Hello("pc")));
    }

    [Fact]
    public void Auth_TamperedAuth2OrAuth3Rejected()
    {
        var g = new Rig();
        var i = g.Paired(); var r = g.Resp();
        var a2 = r.ReadAuth1AndCreateAuth2(i.CreateAuth1());
        var bad2 = (byte[])a2.Clone(); bad2[40] ^= 1;
        Assert.Throws<CryptoProtocolException>(() => i.ReadAuth2(bad2));

        var i2 = g.Paired(); var r2 = g.Resp();
        i2.ReadAuth2(r2.ReadAuth1AndCreateAuth2(i2.CreateAuth1()));
        var a3 = i2.CreateAuth3(); a3[0] ^= 1;
        Assert.Throws<CryptoProtocolException>(() => r2.ReadAuth3(a3));
    }

    [Fact]
    public void Auth_ExceptionsCarryNoKeyMaterial()
    {
        var g = new Rig();
        var ex = Assert.Throws<CryptoProtocolException>(() => g.Resp(pcKnown: false).ReadAuth1AndCreateAuth2(g.Paired().CreateAuth1()));
        Assert.DoesNotContain(Convert.ToHexString(g.Pc.Public), ex.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    // ---- pairing token ----

    [Fact]
    public void Pairing_ExpiredTokenRejected()
    {
        var g = new Rig();
        var tc = g.TokenCheck();
        g.Time.Now = T0.AddMinutes(5).AddSeconds(1);
        Assert.Throws<CryptoProtocolException>(() => g.Resp(tc).ReadAuth1AndCreateAuth2(g.Pairing().CreateAuth1()));
        Assert.Equal(0, g.MarkUsedCalls);
    }

    [Fact]
    public void Pairing_TokenSingleUse()
    {
        var g = new Rig();
        var tc = g.TokenCheck();
        Complete(g.Pairing(), g.Resp(tc));
        Assert.Equal(1, g.MarkUsedCalls);
        Assert.Throws<CryptoProtocolException>(() => g.Resp(tc).ReadAuth1AndCreateAuth2(g.Pairing().CreateAuth1()));
        Assert.Equal(1, g.MarkUsedCalls);
    }

    [Fact]
    public void Pairing_Auth1AloneDoesNotBurnToken()
    {
        var g = new Rig();
        var tc = g.TokenCheck();
        using (var r = g.Resp(tc)) r.ReadAuth1AndCreateAuth2(g.Pairing().CreateAuth1()); // abandoned
        Assert.Equal(0, g.MarkUsedCalls);
        Complete(g.Pairing(), g.Resp(tc));
        Assert.Equal(1, g.MarkUsedCalls);
    }

    [Fact]
    public void Pairing_SecondAuth1WhileInFlightRejected()
    {
        var g = new Rig();
        var tc = g.TokenCheck();
        var r1 = g.Resp(tc);
        r1.ReadAuth1AndCreateAuth2(g.Pairing().CreateAuth1());
        Assert.Throws<CryptoProtocolException>(() => g.Resp(tc).ReadAuth1AndCreateAuth2(g.Pairing().CreateAuth1()));
        r1.Dispose();
        g.Resp(tc).ReadAuth1AndCreateAuth2(g.Pairing().CreateAuth1()); // now allowed
    }

    [Fact]
    public void Pairing_WrongHmacRejected()
    {
        var g = new Rig();
        var wrong = RandomNumberGenerator.GetBytes(16);
        Assert.Throws<CryptoProtocolException>(() => g.Resp(g.TokenCheck()).ReadAuth1AndCreateAuth2(g.Pairing(wrong).CreateAuth1()));
        var a1 = g.Pairing().CreateAuth1(); a1[^1] ^= 1;
        Assert.Throws<CryptoProtocolException>(() => g.Resp(g.TokenCheck()).ReadAuth1AndCreateAuth2(a1));
    }

    [Fact]
    public void Pairing_StaticKeyDifferentFromQrRejected()
    {
        var g = new Rig();
        var other = StaticKey.Generate();
        var tc = new PairingTokenCheck(g.Token, T0.AddMinutes(5), other.Public, () => true, g.Time);
        Assert.Throws<CryptoProtocolException>(() => g.Resp(tc).ReadAuth1AndCreateAuth2(g.Pairing().CreateAuth1()));
    }

    [Fact]
    public void Pairing_ModeWithoutTokenCheckRejected()
    {
        var g = new Rig();
        Assert.Throws<CryptoProtocolException>(() => g.Resp(null).ReadAuth1AndCreateAuth2(g.Pairing().CreateAuth1()));
    }

    [Fact]
    public void X25519_Rfc7748Vector()
    {
        var aPriv = Convert.FromHexString("77076d0a7318a57d3c16c17251b26645df4c2f87ebc0992ab177fba51db92c2a");
        var bPub = Convert.FromHexString("de9edb7d7b7dc1b4d35b61c2ece435373f8343c85b78674dadfc7e146f882b4f");
        Assert.Equal("8520f0098930a754748b7ddcb43ef75a0dbf3a0d26381af4eba4a98eaa9b4e6a",
            Convert.ToHexString(X25519.FromPrivate(aPriv).pub).ToLowerInvariant());
        Assert.Equal("4a5d9d5ba4ce2de1728e3bf480350f25e07e21c947d19e3376f09b3c1e161742",
            Convert.ToHexString(X25519.Agree(aPriv, bPub)).ToLowerInvariant());
        Assert.Throws<CryptoProtocolException>(() => X25519.Agree(aPriv, new byte[32])); // low-order point
    }
}
