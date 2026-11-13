using RaaiCam.Protocol;

namespace RaaiCam.Core.Hosting;

// Small seams between Host and the Windows-only / hardware-facing pieces. Core must not reference
// RaaiCam.Media, so the real adapters live in windows/tools/raaicam-host.

/// <summary>What the meeting app wants from the virtual camera. Mirror of Media's ConsumerState.</summary>
public readonly record struct ConsumerRequest(bool Active, int Width, int Height);

public interface IConsumerSource
{
    /// <summary>Current request, or null when the shared mapping is not available. Must be cheap (polled every 100 ms).</summary>
    ConsumerRequest? Read();
}

/// <summary>A connected phone. Wraps <c>PhoneSession</c>; event handlers must not block.</summary>
public interface IPhoneLink : IAsyncDisposable
{
    event Action<VideoFrame>? Video;
    event Action<Telemetry>? TelemetryReceived;
    event Action<string>? Disconnected;

    /// <summary>Null until the phone's INFO frame has been received over the sealed channel (key confirmation).</summary>
    HelloInfo? Phone { get; }
    /// <summary>The phone static public key (32 bytes); null until the link is confirmed. Identifies the phone across links.</summary>
    byte[]? PhoneStaticKey { get; }
    double? RttMs { get; }
    /// <summary>Converts a phone capture timestamp to the PC clock (same timebase as the host's qpc), or null before clock sync.</summary>
    long? ToPcUs(ulong phoneUs);

    Task RunAsync(CancellationToken ct);
    Task StartAsync(int width, int height);
    Task StopAsync();
    Task RequestKeyframeAsync();
    /// <summary>Tells the phone to start or stop streaming video over this link (sealed CMD route).</summary>
    Task SendRouteAsync(bool video);
}

public interface IPhoneConnector
{
    /// <summary>Opens a not-yet-running link to the first USB phone; null when none is attached. Throws when the attempt fails.</summary>
    Task<IPhoneLink?> ConnectAsync(CancellationToken ct);
}

public enum VideoDecodeResult { Frame, NeedMoreInput, Error }

/// <summary>Mirror of Media's H264Decoder surface. Used from exactly one thread.</summary>
public interface IVideoDecoder : IDisposable
{
    event Action? KeyframeNeeded;
    VideoDecodeResult Decode(ReadOnlySpan<byte> accessUnit, long sampleTime100ns, Span<byte> nv12Out, out int stride);
}

public interface IDecoderFactory
{
    IVideoDecoder Create(int width, int height);
}

public interface IFrameSink
{
    bool WriteFrame(ReadOnlySpan<byte> nv12, int width, int height, int stride, long timestampQpc);
}

/// <param name="Link">"usb" while a phone connection exists, otherwise "none".</param>
public sealed record HostStatus(
    string Link,
    string? Phone,
    int ConsumerWidth,
    int ConsumerHeight,
    double FpsIn,
    double FpsOut,
    double? LatencyP50Ms,
    double? LatencyP95Ms,
    double? RttMs,
    double? BitrateMbps,
    string? Thermal,
    int? BatteryPercent,
    bool? Charging);
