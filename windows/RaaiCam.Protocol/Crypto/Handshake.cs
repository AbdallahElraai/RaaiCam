using System.Security.Cryptography;
using System.Text;

namespace RaaiCam.Protocol.Crypto;

static class HandshakeLayout
{
    public const byte ModePaired = 1, ModePairing = 2;
    public const int Auth1PairedLength = 1 + 32 + 32 + 16;          // 81
    public const int Auth1PairingLength = Auth1PairedLength + PairingTokenCheck.HmacSize; // 97
    public const int Auth2Length = 32 + 32 + Kdf.TagSize;           // 80
    public static readonly byte[] Ok = Encoding.ASCII.GetBytes("ok");
    public const int Auth3Length = 2 + Kdf.TagSize;                 // 18
    public static readonly byte[] Th0Label = Encoding.ASCII.GetBytes("raaicam-v2");

    /// <summary>IKM = DH outputs, plus (mode 2 only) the 16-byte QR token as a pre-shared key. The ikm is wiped after use.</summary>
    public static byte[] Hkdf(byte[]? psk, string label, byte[] th, int length, params byte[][] dh)
    {
        var ikm = psk is null ? Kdf.Concat(dh) : Kdf.Concat([.. dh, psk]);
        try { return Kdf.Derive(ikm, label, th, length); }
        finally { CryptographicOperations.ZeroMemory(ikm); }
    }

    public static void Wipe(params byte[]?[] buffers)
    {
        foreach (var b in buffers) if (b is not null) CryptographicOperations.ZeroMemory(b);
    }
}

/// <summary>PC side (initiator). Single use: CreateAuth1, ReadAuth2, CreateAuth3, then read <see cref="Keys"/>.</summary>
public sealed class InitiatorHandshake
{
    enum State { New, Sent1, Got2, Done, Failed }

    readonly StaticKey _sI;
    readonly StaticKey _eI;
    readonly byte[] _pcId;
    readonly byte[]? _pairedPhonePub;
    readonly byte[]? _token;
    State _state = State.New;
    byte[] _auth1 = [];
    byte[] _th2 = [];
    byte[] _dhEE = [], _dhSE = [], _dhES = [], _dhSS = [];
    SessionKeys? _keys;

    public InitiatorHandshake(StaticKey pc, byte[] pcId, byte[]? pairedPhonePub, byte[]? token)
        : this(pc, pcId, pairedPhonePub, token, null) { }

    internal InitiatorHandshake(StaticKey pc, byte[] pcId, byte[]? pairedPhonePub, byte[]? token, StaticKey? ephemeralForTest)
    {
        if (pcId is null || pcId.Length != 16) throw new CryptoProtocolException("bad id");
        if ((pairedPhonePub is null) == (token is null)) throw new CryptoProtocolException("bad mode");
        if (pairedPhonePub is { Length: not 32 } || token is { Length: not 16 }) throw new CryptoProtocolException("bad key");
        _sI = pc; _pcId = pcId; _pairedPhonePub = pairedPhonePub; _token = token;
        _eI = ephemeralForTest ?? StaticKey.Generate();
    }

    /// <summary>
    /// Session keys. The session is NOT confirmed by the handshake alone: only after the first successful
    /// <see cref="SealedChannel.Open"/> (the phone must send a SEALED INFO frame first) has the phone proven it derived the
    /// same keys. In pairing mode persist the phone key, and report "connected", only after that confirmation.
    /// </summary>
    public SessionKeys Keys => _state == State.Done ? _keys! : throw new CryptoProtocolException("not authenticated");

    public byte[] CreateAuth1()
    {
        Require(State.New);
        bool pairing = _token is not null;
        var a1 = Kdf.Concat([pairing ? HandshakeLayout.ModePairing : HandshakeLayout.ModePaired], _eI.Public, _sI.Public, _pcId,
            pairing ? PairingTokenCheck.ComputeHmac(_token!, _eI.Public, _sI.Public) : []);
        _auth1 = a1;
        _state = State.Sent1;
        return (byte[])a1.Clone();
    }

    /// <summary>Verifies the phone AUTH2 and returns its static public key (to be stored when pairing, after confirmation).</summary>
    public byte[] ReadAuth2(byte[] auth2)
    {
        try
        {
            Require(State.Sent1);
            if (auth2 is null || auth2.Length != HandshakeLayout.Auth2Length) throw new CryptoProtocolException();
            var eR = auth2[..32];
            var ct = auth2[32..];
            var th0 = Kdf.Sha256(HandshakeLayout.Th0Label, _auth1);
            var th1 = Kdf.Sha256(th0, eR);
            _dhEE = X25519.Agree(_eI.Private, eR);
            _dhSE = X25519.Agree(_sI.Private, eR);
            var k0 = HandshakeLayout.Hkdf(_token, "auth2", th1, 32, _dhEE, _dhSE);
            byte[] sR;
            try { sR = Kdf.OpenOnce(k0, ct); }
            finally { HandshakeLayout.Wipe(k0); }
            if (sR.Length != 32) throw new CryptoProtocolException();
            if (_pairedPhonePub is not null && !CryptographicOperations.FixedTimeEquals(sR, _pairedPhonePub))
                throw new CryptoProtocolException();
            _th2 = Kdf.Sha256(th1, ct);
            _dhES = X25519.Agree(_eI.Private, sR);
            _dhSS = X25519.Agree(_sI.Private, sR);
            _state = State.Got2;
            return (byte[])sR.Clone();
        }
        catch { Fail(); throw; }
    }

    public byte[] CreateAuth3()
    {
        Require(State.Got2);
        try
        {
            var k1 = HandshakeLayout.Hkdf(_token, "auth3", _th2, 32, _dhEE, _dhSE, _dhES);
            byte[] a3;
            try { a3 = Kdf.SealOnce(k1, HandshakeLayout.Ok); }
            finally { HandshakeLayout.Wipe(k1); }
            var okm = HandshakeLayout.Hkdf(_token, "session", _th2, 64, _dhEE, _dhSE, _dhES, _dhSS);
            _keys = new SessionKeys(okm[..32], okm[32..]);
            HandshakeLayout.Wipe(okm);
            _state = State.Done;
            WipeSecrets();
            return a3;
        }
        catch { Fail(); throw; }
    }

    void WipeSecrets()
    {
        HandshakeLayout.Wipe(_eI.Private, _dhEE, _dhSE, _dhES, _dhSS, _th2);
    }

    void Fail()
    {
        _state = State.Failed;
        WipeSecrets();
    }

    void Require(State s)
    {
        if (_state != s) { Fail(); throw new CryptoProtocolException("unexpected handshake step"); }
    }
}

/// <summary>Phone side (responder). Dispose it when the connection ends so a pending pairing token is released.</summary>
public sealed class ResponderHandshake : IDisposable
{
    enum State { New, Sent2, Done, Failed }

    readonly StaticKey _sR;
    readonly StaticKey _eR;
    readonly Func<byte[], bool> _isPairedPc;
    readonly PairingTokenCheck? _tokenCheck;
    readonly byte[]? _pskOverrideForTest;
    State _state = State.New;
    bool _claimedToken;
    bool _wasPairing;
    byte[]? _psk;
    byte[] _sIPub = [];
    byte[] _pcId = [];
    byte[] _th2 = [];
    byte[] _dhEE = [], _dhSE = [], _dhES = [], _dhSS = [];
    SessionKeys? _keys;

    public ResponderHandshake(StaticKey phone, Func<byte[], bool> isPairedPc, PairingTokenCheck? tokenCheck)
        : this(phone, isPairedPc, tokenCheck, null) { }

    internal ResponderHandshake(StaticKey phone, Func<byte[], bool> isPairedPc, PairingTokenCheck? tokenCheck,
        StaticKey? ephemeralForTest, byte[]? pskOverrideForTest = null)
    {
        _sR = phone; _isPairedPc = isPairedPc; _tokenCheck = tokenCheck; _pskOverrideForTest = pskOverrideForTest;
        _eR = ephemeralForTest ?? StaticKey.Generate();
    }

    /// <summary>Session keys. The responder MUST send a SEALED INFO frame first: that is the initiator key confirmation.</summary>
    public SessionKeys Keys => _state == State.Done ? _keys! : throw new CryptoProtocolException("not authenticated");
    public byte[] PeerStatic => _state == State.Done ? _sIPub : throw new CryptoProtocolException("not authenticated");
    public byte[] PeerPcId => _state == State.Done ? _pcId : throw new CryptoProtocolException("not authenticated");
    public bool WasPairing => _state == State.Done ? _wasPairing : throw new CryptoProtocolException("not authenticated");

    public byte[] ReadAuth1AndCreateAuth2(byte[] auth1)
    {
        try
        {
            if (_state != State.New) throw new CryptoProtocolException();
            if (auth1 is null || auth1.Length < 1) throw new CryptoProtocolException();
            byte mode = auth1[0];
            bool pairing = mode == HandshakeLayout.ModePairing;
            if (!(mode == HandshakeLayout.ModePaired && auth1.Length == HandshakeLayout.Auth1PairedLength) &&
                !(pairing && auth1.Length == HandshakeLayout.Auth1PairingLength))
                throw new CryptoProtocolException();

            var eI = auth1[1..33];
            var sI = auth1[33..65];
            var pcId = auth1[65..81];

            if (pairing)
            {
                var tc = _tokenCheck ?? throw new CryptoProtocolException();
                if (!tc.VerifyHmac(eI, sI, auth1.AsSpan(81, PairingTokenCheck.HmacSize))) throw new CryptoProtocolException();
                if (!tc.TryBegin(sI)) throw new CryptoProtocolException();
                _claimedToken = true;
                _psk = (byte[])(_pskOverrideForTest ?? tc.Token).Clone();
            }
            else if (!_isPairedPc(sI)) throw new CryptoProtocolException();

            var th0 = Kdf.Sha256(HandshakeLayout.Th0Label, auth1);
            var th1 = Kdf.Sha256(th0, _eR.Public);
            _dhEE = X25519.Agree(_eR.Private, eI);
            _dhSE = X25519.Agree(_eR.Private, sI);
            var k0 = HandshakeLayout.Hkdf(_psk, "auth2", th1, 32, _dhEE, _dhSE);
            byte[] ct;
            try { ct = Kdf.SealOnce(k0, _sR.Public); }
            finally { HandshakeLayout.Wipe(k0); }
            _th2 = Kdf.Sha256(th1, ct);
            _dhES = X25519.Agree(_sR.Private, eI);
            _dhSS = X25519.Agree(_sR.Private, sI);
            _sIPub = sI; _pcId = pcId; _wasPairing = pairing;
            _state = State.Sent2;
            return Kdf.Concat(_eR.Public, ct);
        }
        catch { Fail(); throw; }
    }

    public void ReadAuth3(byte[] auth3)
    {
        try
        {
            if (_state != State.Sent2) throw new CryptoProtocolException();
            if (auth3 is null || auth3.Length != HandshakeLayout.Auth3Length) throw new CryptoProtocolException();
            var k1 = HandshakeLayout.Hkdf(_psk, "auth3", _th2, 32, _dhEE, _dhSE, _dhES);
            byte[] ok;
            try { ok = Kdf.OpenOnce(k1, auth3); }
            finally { HandshakeLayout.Wipe(k1); }
            if (!CryptographicOperations.FixedTimeEquals(ok, HandshakeLayout.Ok)) throw new CryptoProtocolException();
            if (_wasPairing)
            {
                if (!_tokenCheck!.Consume()) throw new CryptoProtocolException();
                _claimedToken = false;
            }
            var okm = HandshakeLayout.Hkdf(_psk, "session", _th2, 64, _dhEE, _dhSE, _dhES, _dhSS);
            _keys = new SessionKeys(okm[..32], okm[32..]);
            HandshakeLayout.Wipe(okm);
            _state = State.Done;
            WipeSecrets();
        }
        catch { Fail(); throw; }
    }

    void WipeSecrets() => HandshakeLayout.Wipe(_eR.Private, _dhEE, _dhSE, _dhES, _dhSS, _th2, _psk);

    void Fail()
    {
        _state = State.Failed;
        WipeSecrets();
        ReleaseToken();
    }

    void ReleaseToken()
    {
        if (_claimedToken) { _tokenCheck?.Release(); _claimedToken = false; }
    }

    public void Dispose()
    {
        ReleaseToken();
        if (_state != State.Done) WipeSecrets();
    }
}
