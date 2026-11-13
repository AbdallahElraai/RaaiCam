using System.Buffers.Binary;

namespace RaaiCam.Protocol;

public enum FrameType : byte
{
    Hello = 1,
    Auth = 2,
    Cmd = 3,
    Telemetry = 4,
    Log = 5,
    Video = 6,
    Ping = 7,
    Pong = 8,
}

public readonly record struct Frame(FrameType Type, ReadOnlyMemory<byte> Payload);

public sealed class ProtocolException : Exception
{
    public ProtocolException(string message) : base(message) { }
    public ProtocolException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>Frame: [u8 type][u32 big-endian payload length][payload].</summary>
public static class FrameCodec
{
    public const int MaxPayload = 4 * 1024 * 1024;
    public const int HeaderSize = 5;

    public static void Write(Stream s, FrameType t, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > MaxPayload)
            throw new ProtocolException($"payload {payload.Length} exceeds max {MaxPayload}");
        Span<byte> header = stackalloc byte[HeaderSize];
        header[0] = (byte)t;
        BinaryPrimitives.WriteUInt32BigEndian(header[1..], (uint)payload.Length);
        s.Write(header);
        s.Write(payload);
    }

    /// <summary>Returns null on clean EOF at a frame boundary; throws ProtocolException on oversize or truncation.</summary>
    public static async ValueTask<Frame?> ReadAsync(Stream s, CancellationToken ct)
    {
        var header = new byte[HeaderSize];
        int got = await ReadFullAsync(s, header, ct).ConfigureAwait(false);
        if (got == 0) return null;
        if (got < HeaderSize) throw new ProtocolException("truncated frame header");

        uint length = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(1));
        if (length > MaxPayload) throw new ProtocolException($"frame length {length} exceeds max {MaxPayload}");

        var payload = new byte[length];
        got = await ReadFullAsync(s, payload, ct).ConfigureAwait(false);
        if (got < payload.Length) throw new ProtocolException("truncated frame payload");
        return new Frame((FrameType)header[0], payload);
    }

    static async ValueTask<int> ReadFullAsync(Stream s, Memory<byte> buffer, CancellationToken ct)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = await s.ReadAsync(buffer[total..], ct).ConfigureAwait(false);
            if (n == 0) break;
            total += n;
        }
        return total;
    }
}
