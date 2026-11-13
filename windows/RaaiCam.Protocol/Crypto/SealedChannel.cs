using System.Buffers.Binary;
using System.Security.Cryptography;

namespace RaaiCam.Protocol.Crypto;

/// <summary>
/// SEALED frame codec for one authenticated session. Payload on the wire: [u64 BE counter][ciphertext || 16-byte tag].
/// Plaintext = inner v1 frame [u8 type][u32 BE len][payload]. Nonce = [u32 BE direction][u64 BE counter]
/// (1 = initiator-to-responder, 2 = responder-to-initiator); AAD = [0x09][u64 BE counter]. The receiver requires
/// counter == expected; any failure closes the channel for good.
/// </summary>
public sealed class SealedChannel
{
    const int CounterSize = 8;
    const int InnerHeader = 5;

    readonly byte[] _sendKey, _recvKey;
    readonly uint _sendDir, _recvDir;
    readonly object _sendLock = new(), _recvLock = new();
    ulong _sendCounter, _recvCounter;
    volatile bool _closed;

    public SealedChannel(SessionKeys keys, bool isInitiator)
    {
        _sendKey = isInitiator ? keys.InitiatorToResponder : keys.ResponderToInitiator;
        _recvKey = isInitiator ? keys.ResponderToInitiator : keys.InitiatorToResponder;
        _sendDir = isInitiator ? 1u : 2u;
        _recvDir = isInitiator ? 2u : 1u;
    }

    static void Nonce(Span<byte> n, uint dir, ulong counter)
    {
        BinaryPrimitives.WriteUInt32BigEndian(n, dir);
        BinaryPrimitives.WriteUInt64BigEndian(n[4..], counter);
    }

    static void Aad(Span<byte> a, ulong counter)
    {
        a[0] = (byte)FrameType.Sealed;
        BinaryPrimitives.WriteUInt64BigEndian(a[1..], counter);
    }

    static bool InnerTypeAllowed(FrameType t) => t is not (FrameType.Hello or FrameType.Auth or FrameType.Sealed);

    /// <summary>Returns the SEALED frame payload for an inner frame.</summary>
    public byte[] Seal(FrameType innerType, ReadOnlySpan<byte> innerPayload)
    {
        if (_closed) throw new CryptoProtocolException("channel closed");
        if (!InnerTypeAllowed(innerType)) throw new CryptoProtocolException("bad inner type");
        if (innerPayload.Length > FrameCodec.MaxPayload - CounterSize - Kdf.TagSize - InnerHeader)
            throw new CryptoProtocolException("payload too large");

        var plain = new byte[InnerHeader + innerPayload.Length];
        plain[0] = (byte)innerType;
        BinaryPrimitives.WriteUInt32BigEndian(plain.AsSpan(1), (uint)innerPayload.Length);
        innerPayload.CopyTo(plain.AsSpan(InnerHeader));

        lock (_sendLock)
        {
            if (_sendCounter == ulong.MaxValue) { _closed = true; throw new CryptoProtocolException("counter exhausted"); }
            ulong c = _sendCounter++;
            var output = new byte[CounterSize + plain.Length + Kdf.TagSize];
            BinaryPrimitives.WriteUInt64BigEndian(output, c);
            Span<byte> nonce = stackalloc byte[Kdf.NonceSize];
            Span<byte> aad = stackalloc byte[9];
            Nonce(nonce, _sendDir, c);
            Aad(aad, c);
            using var gcm = new AesGcm(_sendKey, Kdf.TagSize);
            gcm.Encrypt(nonce, plain, output.AsSpan(CounterSize, plain.Length), output.AsSpan(CounterSize + plain.Length), aad);
            return output;
        }
    }

    /// <summary>Decrypts a SEALED payload. Throws CryptoProtocolException (and closes the channel) on any failure.</summary>
    public Frame Open(ReadOnlySpan<byte> sealedPayload)
    {
        if (_closed) throw new CryptoProtocolException("channel closed");
        lock (_recvLock)
        {
            try
            {
                if (sealedPayload.Length < CounterSize + InnerHeader + Kdf.TagSize) throw new CryptoProtocolException();
                ulong c = BinaryPrimitives.ReadUInt64BigEndian(sealedPayload);
                if (c != _recvCounter) throw new CryptoProtocolException();

                int n = sealedPayload.Length - CounterSize - Kdf.TagSize;
                var plain = new byte[n];
                Span<byte> nonce = stackalloc byte[Kdf.NonceSize];
                Span<byte> aad = stackalloc byte[9];
                Nonce(nonce, _recvDir, c);
                Aad(aad, c);
                try
                {
                    using var gcm = new AesGcm(_recvKey, Kdf.TagSize);
                    gcm.Decrypt(nonce, sealedPayload.Slice(CounterSize, n), sealedPayload[(CounterSize + n)..], plain, aad);
                }
                catch (CryptographicException) { throw new CryptoProtocolException(); }

                var type = (FrameType)plain[0];
                uint len = BinaryPrimitives.ReadUInt32BigEndian(plain.AsSpan(1));
                if (!InnerTypeAllowed(type) || len != (uint)(n - InnerHeader)) throw new CryptoProtocolException();
                _recvCounter++;
                return new Frame(type, plain.AsMemory(InnerHeader));
            }
            catch { _closed = true; throw; }
        }
    }
}
