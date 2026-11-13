using System.Security.Cryptography;
using System.Text;

namespace RaaiCam.Protocol.Crypto;

/// <summary>
/// Phone-side state of one pending pairing token (from the QR). Thread-safe. The token is consumed only when a
/// handshake completes (AUTH3 verified); an AUTH1 alone never burns it, but while one handshake is in flight a second
/// AUTH1 with the same token is refused. Whoever creates a <see cref="ResponderHandshake"/> with this check must
/// dispose the handshake when the connection ends or the 5 s timeout fires, which releases the in-flight claim.
/// </summary>
public sealed class PairingTokenCheck
{
    public const int HmacSize = 16;
    static readonly byte[] Domain = Encoding.ASCII.GetBytes("raaicam-pair-v2");

    readonly byte[] _token;
    readonly DateTimeOffset _expiresAt;
    readonly byte[] _expectedPcPub;
    readonly Func<bool> _markUsed;
    readonly TimeProvider _time;
    readonly object _gate = new();
    bool _used;
    bool _inFlight;
    bool _consuming;

    public PairingTokenCheck(byte[] token, DateTimeOffset expiresAt, byte[] expectedPcPub, Func<bool> markUsed,
        TimeProvider? time = null)
    {
        _token = (byte[])token.Clone();
        _expiresAt = expiresAt;
        _expectedPcPub = (byte[])expectedPcPub.Clone();
        _markUsed = markUsed;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>HMAC-SHA256(token, "raaicam-pair-v2" || E_I.pub || S_I.pub) truncated to 16 bytes.</summary>
    public static byte[] ComputeHmac(byte[] token, byte[] ePub, byte[] sPub)
    {
        var mac = HMACSHA256.HashData(token, Kdf.Concat(Domain, ePub, sPub));
        return mac.AsSpan(0, HmacSize).ToArray();
    }

    internal bool VerifyHmac(byte[] ePub, byte[] sPub, ReadOnlySpan<byte> presented) =>
        CryptographicOperations.FixedTimeEquals(ComputeHmac(_token, ePub, sPub), presented);

    /// <summary>Validate and claim the token for one in-flight handshake.</summary>
    internal bool TryBegin(byte[] sIPub)
    {
        lock (_gate)
        {
            if (_used || _inFlight) return false;
            if (_time.GetUtcNow() >= _expiresAt) return false;
            if (!CryptographicOperations.FixedTimeEquals(_expectedPcPub, sIPub)) return false;
            _inFlight = true;
            return true;
        }
    }

    internal void Release()
    {
        lock (_gate) _inFlight = false;
    }

    /// <summary>AUTH3 verified: burn the token. False if it was already consumed or the owner refused.</summary>
    internal bool Consume()
    {
        lock (_gate)
        {
            if (_used || !_inFlight || _consuming) return false;
            if (_time.GetUtcNow() >= _expiresAt) return false; // re-check expiry at consume time
            _consuming = true;
        }
        bool ok = false;
        try { ok = _markUsed(); } // owner callback runs outside the lock (it may persist or call back)
        finally
        {
            lock (_gate)
            {
                _consuming = false;
                if (ok) { _used = true; _inFlight = false; }
            }
        }
        return ok;
    }

    /// <summary>The token, used as a PSK in mode 2 key derivation.</summary>
    internal byte[] Token => _token;
}
