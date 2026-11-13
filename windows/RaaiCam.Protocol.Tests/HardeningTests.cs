using System.Security.Cryptography;
using System.Text;
using RaaiCam.Protocol;
using RaaiCam.Protocol.Crypto;
using Xunit;

namespace RaaiCam.Protocol.Tests;

public class HardeningTests
{
    static readonly DateTimeOffset T0 = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);

    static (StaticKey pc, StaticKey phone, byte[] pcId, byte[] token) Ids() =>
        (StaticKey.Generate(), StaticKey.Generate(), RandomNumberGenerator.GetBytes(16), RandomNumberGenerator.GetBytes(16));

    static PairingTokenCheck Tc(byte[] token, StaticKey pc, FakeTime? t = null, Func<bool>? mark = null) =>
        new(token, T0.AddMinutes(5), pc.Public, mark ?? (() => true), t ?? new FakeTime(T0));

    // ---- I-1 ----

    [Fact]
    public void Pairing_RogueResponderWithoutTokenFails()
    {
        var (pc, _, pcId, token) = Ids();
        var rogueStatic = StaticKey.Generate();
        var init = new InitiatorHandshake(pc, pcId, null, token);
        // The rogue sees AUTH1 (the HMAC is only checked by the genuine tokenCheck here, which we let pass) but derives
        // k0 without the token PSK (it does not know it).
        using var rogue = new ResponderHandshake(rogueStatic, _ => false, Tc(token, pc), null, pskOverrideForTest: new byte[16]);
        var auth2 = rogue.ReadAuth1AndCreateAuth2(init.CreateAuth1());
        Assert.Throws<CryptoProtocolException>(() => init.ReadAuth2(auth2));
    }

    [Fact]
    public void Pairing_TokenIsMixedIntoKeys()
    {
        // Same static/ephemeral keys, different token => different AUTH2/session keys.
        var sI = StaticKey.FromPrivate(V2Vectors.Hex("sIPriv"));
        var eI = V2Vectors.Hex("eIPriv");
        var sR = StaticKey.FromPrivate(V2Vectors.Hex("sRPriv"));
        var eR = V2Vectors.Hex("eRPriv");
        var pcId = V2Vectors.Hex("pcId");
        byte[] Run(byte[] token)
        {
            var i = new InitiatorHandshake(sI, pcId, null, token, StaticKey.FromPrivate(eI));
            var r = new ResponderHandshake(sR, _ => true, Tc(token, sI), StaticKey.FromPrivate(eR));
            return r.ReadAuth1AndCreateAuth2(i.CreateAuth1());
        }
        Assert.NotEqual(Run(V2Vectors.Hex("token")), Run(RandomNumberGenerator.GetBytes(16)));
    }

    // ---- I-2 ----

    [Fact]
    public void Wipe_EphemeralPrivateKeysZeroedAfterHandshake()
    {
        var (pc, phone, pcId, _) = Ids();
        var eI = StaticKey.Generate(); var eR = StaticKey.Generate();
        var i = new InitiatorHandshake(pc, pcId, phone.Public, null, eI);
        var r = new ResponderHandshake(phone, p => p.AsSpan().SequenceEqual(pc.Public), null, eR);
        i.ReadAuth2(r.ReadAuth1AndCreateAuth2(i.CreateAuth1()));
        r.ReadAuth3(i.CreateAuth3());
        Assert.All(eI.Private, b => Assert.Equal(0, b));
        Assert.All(eR.Private, b => Assert.Equal(0, b));
        Assert.Contains(i.Keys.InitiatorToResponder, b => b != 0);
    }

    [Fact]
    public void Wipe_EphemeralZeroedOnFailure()
    {
        var (pc, phone, pcId, _) = Ids();
        var eR = StaticKey.Generate();
        var r = new ResponderHandshake(phone, _ => false, null, eR);
        var i = new InitiatorHandshake(pc, pcId, phone.Public, null);
        Assert.Throws<CryptoProtocolException>(() => r.ReadAuth1AndCreateAuth2(i.CreateAuth1()));
        Assert.All(eR.Private, b => Assert.Equal(0, b));
    }

    [Fact]
    public void Wipe_KeyContainersZeroOnDispose()
    {
        var k = StaticKey.Generate();
        k.Dispose();
        Assert.All(k.Private, b => Assert.Equal(0, b));
        var keys = new SessionKeys(RandomNumberGenerator.GetBytes(32), RandomNumberGenerator.GetBytes(32));
        var ch = new SealedChannel(keys, true);
        keys.Dispose();
        Assert.All(keys.InitiatorToResponder, b => Assert.Equal(0, b));
        Assert.Contains(ch.Seal(FrameType.Cmd, "{}"u8), b => b != 0); // channel keeps its own copy
        ch.Dispose();
        Assert.Throws<CryptoProtocolException>(() => ch.Seal(FrameType.Cmd, "{}"u8));
    }

    // ---- I-3 ----

    [Fact]
    public void Confirmation_InitiatorUnconfirmedUntilFirstOpen()
    {
        var (pc, phone, pcId, _) = Ids();
        var i = new InitiatorHandshake(pc, pcId, phone.Public, null);
        var r = new ResponderHandshake(phone, p => p.AsSpan().SequenceEqual(pc.Public), null);
        i.ReadAuth2(r.ReadAuth1AndCreateAuth2(i.CreateAuth1()));
        r.ReadAuth3(i.CreateAuth3());
        var ic = new SealedChannel(i.Keys, true);
        var rc = new SealedChannel(r.Keys, false);
        Assert.False(ic.IsConfirmed);
        rc.Open(ic.Seal(FrameType.Cmd, "{}"u8));
        Assert.False(ic.IsConfirmed); // sending does not confirm
        var info = Encoding.UTF8.GetBytes("{\"app\":\"RaaiCam\"}");
        Assert.Throws<CryptoProtocolException>(() => ic.Open(new byte[40])); // garbage does not confirm
        Assert.False(ic.IsConfirmed);
        var ic2 = new SealedChannel(i.Keys, true);
        var rc2 = new SealedChannel(r.Keys, false);
        ic2.Open(rc2.Seal(FrameType.Info, info));
        Assert.True(ic2.IsConfirmed);
    }

    // ---- I-4 ----

    [Theory]
    [InlineData("sealedHighCounterI2R", true)]
    [InlineData("sealedHighCounterR2I", false)]
    public void Vectors_SealedHighCounterMatches(string name, bool i2r)
    {
        var v = V2Vectors.Root.GetProperty(name);
        var m = V2Vectors.Root.GetProperty("pairedMode");
        var keys = new SessionKeys(V2Vectors.Hex(m, "kI2R"), V2Vectors.Hex(m, "kR2I"));
        ulong counter = 0x0102030405060708UL;
        var inner = Encoding.UTF8.GetBytes(v.GetProperty("innerPayloadUtf8").GetString()!);
        var type = (FrameType)v.GetProperty("innerType").GetInt32();
        var sender = new SealedChannel(keys, i2r, counter, 0);
        var payload = sender.Seal(type, inner);
        Assert.Equal(V2Vectors.Hex(v, "payloadHex"), payload);
        var f = new SealedChannel(keys, !i2r, 0, counter).Open(payload);
        Assert.Equal(type, f.Type);
        Assert.Equal(inner, f.Payload.ToArray());
    }

    // ---- M-1 ----

    [Theory]
    [InlineData(1)]  // E_I
    [InlineData(33)] // S_I
    public void Auth_AllZeroKeyInAuth1Rejected(int offset)
    {
        var (pc, phone, pcId, _) = Ids();
        var a1 = new InitiatorHandshake(pc, pcId, phone.Public, null).CreateAuth1();
        Array.Clear(a1, offset, 32);
        using var r = new ResponderHandshake(phone, _ => true, null);
        Assert.Throws<CryptoProtocolException>(() => r.ReadAuth1AndCreateAuth2(a1));
    }

    // ---- M-3 ----

    [Fact]
    public void Pairing_MarkUsedRunsOutsideLock_AndExpiryRecheckedAtConsume()
    {
        var (pc, phone, pcId, token) = Ids();
        var time = new FakeTime(T0);
        PairingTokenCheck? tc = null;
        bool otherThreadProgressed = false;
        tc = Tc(token, pc, time, () =>
        {
            // If the gate were held here, this would block (another thread) and time out.
            otherThreadProgressed = Task.Run(() => tc!.TryBegin(pc.Public)).Wait(2000);
            return true;
        });
        var i = new InitiatorHandshake(pc, pcId, null, token);
        using var r = new ResponderHandshake(phone, _ => false, tc);
        var a2 = r.ReadAuth1AndCreateAuth2(i.CreateAuth1());
        i.ReadAuth2(a2);
        var a3 = i.CreateAuth3();
        r.ReadAuth3(a3);
        Assert.True(otherThreadProgressed);

        // Expiry between AUTH1 and AUTH3 is re-checked at consume time.
        var i2 = new InitiatorHandshake(pc, pcId, null, token);
        var tc2 = Tc(token, pc, time, () => true);
        using var r2 = new ResponderHandshake(phone, _ => false, tc2);
        i2.ReadAuth2(r2.ReadAuth1AndCreateAuth2(i2.CreateAuth1()));
        time.Now = T0.AddMinutes(6);
        Assert.Throws<CryptoProtocolException>(() => r2.ReadAuth3(i2.CreateAuth3()));
    }

    // ---- M-6 ----

    [Fact]
    public void Cipher_InnerTypeAllowListEnforcedOnOpen()
    {
        var keys = new SessionKeys(RandomNumberGenerator.GetBytes(32), RandomNumberGenerator.GetBytes(32));
        foreach (var t in new[] { FrameType.Cmd, FrameType.Telemetry, FrameType.Log, FrameType.Video, FrameType.Ping, FrameType.Pong, FrameType.Info })
            new SealedChannel(keys, false).Open(new SealedChannel(keys, true).Seal(t, new byte[] { 1 }));
        Assert.Throws<CryptoProtocolException>(() => new SealedChannel(keys, true).Seal((FrameType)0x42, []));
        Assert.Throws<CryptoProtocolException>(() => new SealedChannel(keys, true).Seal((FrameType)0, []));
    }
}
