using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RaaiCam.Protocol;

public sealed record HelloInfo(int Proto, string Role, string App = "", string Version = "", string Device = "", string Os = "");

public sealed record StartCmd(int Width, int Height, int Fps, string Lens, int BitrateKbps)
{
    [JsonPropertyOrder(-1)] public string Cmd => "start";
}

public sealed record StopCmd
{
    [JsonPropertyOrder(-1)] public string Cmd => "stop";
}

public sealed record KeyframeCmd
{
    [JsonPropertyOrder(-1)] public string Cmd => "keyframe";
}

public sealed record BitrateCmd(int Kbps)
{
    [JsonPropertyOrder(-1)] public string Cmd => "bitrate";
}

public sealed record LensCmd(string Lens)
{
    [JsonPropertyOrder(-1)] public string Cmd => "lens";
}

public sealed record RouteCmd(bool Video)
{
    [JsonPropertyOrder(-1)] public string Cmd => "route";
}

public sealed record UnpairCmd
{
    [JsonPropertyOrder(-1)] public string Cmd => "unpair";
}

public sealed record Telemetry(
    string Thermal, double Battery, bool Charging, bool Streaming, int Width, int Height,
    double FpsCapture, double FpsEncode, int BitrateKbps, string Lens, bool Paused);

public sealed record VideoFrame(ulong CaptureUs, uint Seq, bool Keyframe, ReadOnlyMemory<byte> AccessUnit);

public static class Messages
{
    public const int ProtoVersion = 2;
    const int VideoHeaderSize = 13;

    static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    sealed record HelloWire(int Proto, string Role);

    public static byte[] Hello(string role) =>
        JsonSerializer.SerializeToUtf8Bytes(new HelloWire(ProtoVersion, role), Json);

    public static HelloInfo ParseHello(ReadOnlySpan<byte> payload)
    {
        HelloWire? h;
        try { h = JsonSerializer.Deserialize<HelloWire>(payload, Json); }
        catch (JsonException e) { throw new ProtocolException("invalid HELLO json", e); }
        if (h is null) throw new ProtocolException("empty HELLO");
        if (h.Proto != ProtoVersion) throw new ProtocolException($"unsupported proto {h.Proto}");
        return new HelloInfo(h.Proto, h.Role ?? "");
    }

    sealed record InfoWire(string? App, string? Version, string? Device, string? Os);

    /// <summary>Parses the encrypted INFO frame (sent by the phone as its first sealed frame) into a HelloInfo.</summary>
    public static HelloInfo ParseInfo(ReadOnlySpan<byte> payload)
    {
        InfoWire? i;
        try { i = JsonSerializer.Deserialize<InfoWire>(payload, Json); }
        catch (JsonException e) { throw new ProtocolException("invalid INFO json", e); }
        if (i is null) throw new ProtocolException("empty INFO");
        return new HelloInfo(ProtoVersion, "phone", i.App ?? "", i.Version ?? "", i.Device ?? "", i.Os ?? "");
    }

    public static byte[] Cmd(object cmd) => JsonSerializer.SerializeToUtf8Bytes(cmd, cmd.GetType(), Json);

    public static Telemetry ParseTelemetry(ReadOnlySpan<byte> payload)
    {
        try { return JsonSerializer.Deserialize<Telemetry>(payload, Json) ?? throw new ProtocolException("empty TELEMETRY"); }
        catch (JsonException e) { throw new ProtocolException("invalid TELEMETRY json", e); }
    }

    public static byte[] Ping(ulong pcSendUs)
    {
        var b = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(b, pcSendUs);
        return b;
    }

    public static (ulong pcSendUs, ulong phoneRecvUs) ParsePong(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != 16) throw new ProtocolException($"PONG payload must be 16 bytes, got {payload.Length}");
        return (BinaryPrimitives.ReadUInt64BigEndian(payload), BinaryPrimitives.ReadUInt64BigEndian(payload[8..]));
    }

    public static VideoFrame ParseVideo(ReadOnlyMemory<byte> payload)
    {
        if (payload.Length < VideoHeaderSize) throw new ProtocolException("VIDEO payload shorter than header");
        var s = payload.Span;
        return new VideoFrame(
            BinaryPrimitives.ReadUInt64BigEndian(s),
            BinaryPrimitives.ReadUInt32BigEndian(s[8..]),
            (s[12] & 1) != 0,
            payload[VideoHeaderSize..]);
    }
}
