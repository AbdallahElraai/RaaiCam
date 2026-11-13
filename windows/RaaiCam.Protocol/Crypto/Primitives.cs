using System.Security.Cryptography;
using BcX25519 = Org.BouncyCastle.Math.EC.Rfc7748.X25519;

namespace RaaiCam.Protocol.Crypto;

/// <summary>Any cryptographic or handshake failure. Messages are deliberately generic: never key material.</summary>
public sealed class CryptoProtocolException : ProtocolException
{
    public CryptoProtocolException(string message = "authentication failed") : base(message) { }
}

/// <summary>
/// X25519 (RFC 7748) raw-key operations. Windows CNG's curve25519 can generate keys and agree, but .NET cannot
/// import a raw 32-byte private key into it (ImportParameters throws "operation is not supported"), so
/// deterministic keys and raw interop with CryptoKit are impossible through CNG. BouncyCastle's RFC 7748
/// implementation (MIT, pinned) is used instead.
/// </summary>
public static class X25519
{
    public const int KeySize = 32;

    public static (byte[] priv, byte[] pub) Generate() => FromPrivate(RandomNumberGenerator.GetBytes(KeySize));

    public static (byte[] priv, byte[] pub) FromPrivate(byte[] priv)
    {
        if (priv is null || priv.Length != KeySize) throw new CryptoProtocolException("bad key");
        var pub = new byte[KeySize];
        BcX25519.ScalarMultBase(priv, 0, pub, 0);
        return ((byte[])priv.Clone(), pub);
    }

    /// <summary>Raw shared secret. Rejects low-order peer points (all-zero output).</summary>
    public static byte[] Agree(byte[] priv, byte[] peerPub)
    {
        if (priv is null || priv.Length != KeySize || peerPub is null || peerPub.Length != KeySize)
            throw new CryptoProtocolException("bad key");
        var shared = new byte[KeySize];
        if (!BcX25519.CalculateAgreement(priv, 0, peerPub, 0, shared, 0) ||
            CryptographicOperations.FixedTimeEquals(shared, new byte[KeySize]))
            throw new CryptoProtocolException();
        return shared;
    }
}

/// <summary>An X25519 key pair (static or ephemeral).</summary>
public sealed class StaticKey
{
    public byte[] Private { get; }
    public byte[] Public { get; }
    StaticKey(byte[] priv, byte[] pub) { Private = priv; Public = pub; }
    public static StaticKey Generate() { var (a, b) = X25519.Generate(); return new StaticKey(a, b); }
    public static StaticKey FromPrivate(byte[] priv) { var (a, b) = X25519.FromPrivate(priv); return new StaticKey(a, b); }
}

/// <summary>Per-direction AES-256-GCM keys for one connection.</summary>
public sealed class SessionKeys
{
    public byte[] InitiatorToResponder { get; }
    public byte[] ResponderToInitiator { get; }

    public SessionKeys(byte[] i2r, byte[] r2i)
    {
        if (i2r is null || r2i is null || i2r.Length != 32 || r2i.Length != 32) throw new CryptoProtocolException("bad key");
        InitiatorToResponder = i2r;
        ResponderToInitiator = r2i;
    }
}
