using System.Security.Cryptography;
using System.Text;

namespace RaaiCam.Protocol.Crypto;

/// <summary>Key schedule and AEAD helpers shared by both handshake roles. See protocol/PROTOCOL.md "Protocol v2".</summary>
internal static class Kdf
{
    public static readonly byte[] Salt = Encoding.ASCII.GetBytes("raaicam-v2");
    public const int TagSize = 16;
    public const int NonceSize = 12;

    public static byte[] Concat(params byte[][] parts)
    {
        var r = new byte[parts.Sum(p => p.Length)];
        int o = 0;
        foreach (var p in parts) { p.CopyTo(r, o); o += p.Length; }
        return r;
    }

    public static byte[] Sha256(params byte[][] parts) => SHA256.HashData(Concat(parts));

    /// <summary>HKDF-SHA256(ikm, salt = "raaicam-v2", info = label || th, length).</summary>
    public static byte[] Derive(byte[] ikm, string label, byte[] th, int length) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, length, Salt, Concat(Encoding.ASCII.GetBytes(label), th));

    /// <summary>Handshake AEAD: key used exactly once, so the nonce is 12 zero bytes; no associated data.</summary>
    public static byte[] SealOnce(byte[] key, byte[] plaintext)
    {
        var output = new byte[plaintext.Length + TagSize];
        using var gcm = new AesGcm(key, TagSize);
        gcm.Encrypt(new byte[NonceSize], plaintext, output.AsSpan(0, plaintext.Length), output.AsSpan(plaintext.Length), default);
        return output;
    }

    public static byte[] OpenOnce(byte[] key, ReadOnlySpan<byte> ciphertextAndTag)
    {
        if (ciphertextAndTag.Length < TagSize) throw new CryptoProtocolException();
        int n = ciphertextAndTag.Length - TagSize;
        var plain = new byte[n];
        try
        {
            using var gcm = new AesGcm(key, TagSize);
            gcm.Decrypt(new byte[NonceSize], ciphertextAndTag[..n], ciphertextAndTag[n..], plain, default);
        }
        catch (CryptographicException) { throw new CryptoProtocolException(); }
        return plain;
    }
}
