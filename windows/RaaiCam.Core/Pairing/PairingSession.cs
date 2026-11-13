using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using QRCoder;

namespace RaaiCam.Core.Pairing;

/// <summary>
/// One QR pairing attempt: a random 16-byte one-time token valid for 5 minutes. <see cref="QrUrl"/> carries the token
/// and must never be logged. LAN addresses are not part of it (the phone is the listener).
/// </summary>
public sealed class PairingSession
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);
    public const int MaxNameLength = 40;

    readonly byte[] _token;
    readonly TimeProvider _time;
    int _used;

    PairingSession(byte[] token, DateTimeOffset expiresAt, string url, TimeProvider time)
    {
        _token = token; ExpiresAt = expiresAt; QrUrl = url; _time = time;
    }

    public static PairingSession Begin(PcIdentity id, string pcName, TimeProvider t)
    {
        var token = RandomNumberGenerator.GetBytes(16);
        var exp = t.GetUtcNow() + Lifetime;
        var url = string.Create(CultureInfo.InvariantCulture,
            $"raaicam://pair?v=2&pc={B64Url(id.PcId)}&name={Uri.EscapeDataString(CapName(pcName))}&pk={B64Url(id.PublicKey)}&tok={B64Url(token)}&exp={exp.ToUnixTimeSeconds()}");
        return new PairingSession(token, exp, url, t);
    }

    /// <summary>Contains the token. Never log it.</summary>
    public string QrUrl { get; }
    public DateTimeOffset ExpiresAt { get; }
    public byte[] Token => (byte[])_token.Clone();
    public bool IsExpired => _time.GetUtcNow() >= ExpiresAt;
    public bool IsUsed => Volatile.Read(ref _used) == 1;

    /// <summary>Single use: true for the first caller only.</summary>
    public bool TryMarkUsed() => Interlocked.Exchange(ref _used, 1) == 0;

    static string CapName(string name)
    {
        name ??= "";
        if (name.Length <= MaxNameLength) return name;
        int n = MaxNameLength;
        if (char.IsHighSurrogate(name[n - 1])) n--; // never split a surrogate pair
        return name[..n];
    }

    static string B64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>Renders the pairing URL as a terminal QR code.</summary>
public static class PairingQr
{
    /// <summary>
    /// Two characters per module (block characters) so the code renders square in a terminal. The console must be
    /// UTF-8 (<c>Console.OutputEncoding</c>). Includes the quiet zone.
    /// </summary>
    public static string ToConsoleString(string url)
    {
        using var gen = new QRCodeGenerator();
        using var data = gen.CreateQrCode(url, QRCodeGenerator.ECCLevel.M);
        using var ascii = new AsciiQRCode(data);
        return ascii.GetGraphic(1, "██", "  ", true, "\n");
    }
}
