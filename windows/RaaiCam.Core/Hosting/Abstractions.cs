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
    /// <summary>Starts the stream at the given size and initial bitrate (kbps).</summary>
    Task StartAsync(int width, int height, int kbps);
    Task StopAsync();
    Task RequestKeyframeAsync();
    /// <summary>Tells the phone to start or stop streaming video over this link (sealed CMD route).</summary>
    Task SendRouteAsync(bool video);
    /// <summary>Changes the encoder bitrate (kbps) while streaming.</summary>
    Task SetBitrateAsync(int kbps);
    /// <summary>Unpairs the phone of this confirmed session. Throws <see cref="InvalidOperationException"/> when not confirmed, IOException when the store fails.</summary>
    Task UnpairAsync();
}

/// <summary>
/// USB connector. Implementations must not block synchronously (the link manager calls them off the host loop but
/// relies on the call returning a Task promptly).
/// </summary>
public interface IPhoneConnector
{
    /// <summary>Opens a not-yet-running link to the first USB phone; null when none is attached. Throws when the attempt fails.</summary>
    Task<IPhoneLink?> ConnectAsync(CancellationToken ct);
}

public enum LinkKind { Usb, Wifi }

/// <summary>A phone address to try over Wi-Fi.</summary>
public sealed record WifiCandidate(System.Net.IPAddress Address, int Port);

/// <summary>Wi-Fi side: discovery plus TCP connect. Same non-blocking rule as <see cref="IPhoneConnector"/>.</summary>
public interface IWifiConnector
{
    /// <summary>Candidates for the paired phone, best first (last known address, then mDNS); runs until cancelled. Single consumer.</summary>
    IAsyncEnumerable<WifiCandidate> BrowseAsync(CancellationToken ct);
    /// <summary>Opens a not-yet-running link to the candidate (the caller bounds the TCP connect through <paramref name="ct"/>).</summary>
    Task<IPhoneLink> ConnectAsync(WifiCandidate candidate, CancellationToken ct);
    /// <summary>Remembers an address a confirmed session succeeded on.</summary>
    void RecordWorkingAddress(System.Net.IPAddress address);
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

/// <param name="Link">"usb" or "wifi" for the link carrying video, otherwise "none".</param>
/// <param name="Standby">The other authenticated link kept warm, or "none".</param>
/// <param name="TargetBitrateKbps">The bitrate the host last asked the active link for (0 when not streaming).</param>
/// <param name="DroppedStandbyFrames">Video frames dropped because they came from a link that is not carrying video.</param>
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
    bool? Charging,
    string Standby = "none",
    int TargetBitrateKbps = 0,
    long DroppedStandbyFrames = 0);
