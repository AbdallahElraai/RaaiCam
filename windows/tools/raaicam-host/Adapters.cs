using System.Diagnostics;
using RaaiCam.Core.Hosting;
using RaaiCam.Core.Pairing;
using RaaiCam.Core.Phone;
using RaaiCam.Core.Usbmux;
using RaaiCam.Media;
using RaaiCam.Protocol;

namespace RaaiCam.HostApp;

/// <summary>Consumer source + frame sink over the shared-frame mapping (the sink is thread-safe).</summary>
sealed class SinkAdapter(VirtualCameraSink sink) : IConsumerSource, IFrameSink
{
    public ConsumerRequest? Read() =>
        sink.ReadConsumerState() is { } s ? new ConsumerRequest(s.Active, s.RequestedWidth, s.RequestedHeight) : null;

    public bool WriteFrame(ReadOnlySpan<byte> nv12, int width, int height, int stride, long timestampQpc) =>
        sink.WriteFrame(nv12, width, height, stride, timestampQpc);
}

sealed class H264DecoderAdapter(H264Decoder inner) : IVideoDecoder
{
    public event Action? KeyframeNeeded
    {
        add => inner.KeyframeNeeded += value;
        remove => inner.KeyframeNeeded -= value;
    }

    public VideoDecodeResult Decode(ReadOnlySpan<byte> accessUnit, long sampleTime100ns, Span<byte> nv12Out, out int stride) =>
        inner.Decode(accessUnit, sampleTime100ns, nv12Out, out stride) switch
        {
            DecodeResult.Frame => VideoDecodeResult.Frame,
            DecodeResult.NeedMoreInput => VideoDecodeResult.NeedMoreInput,
            _ => VideoDecodeResult.Error,
        };

    public void Dispose() => inner.Dispose();
}

sealed class H264DecoderFactory : IDecoderFactory
{
    public IVideoDecoder Create(int width, int height) => new H264DecoderAdapter(new H264Decoder(width, height));
}

sealed class PhoneLink(PhoneSession session) : IPhoneLink
{
    public event Action<VideoFrame>? Video
    {
        add => session.Video += value;
        remove => session.Video -= value;
    }

    public event Action<Telemetry>? TelemetryReceived
    {
        add => session.TelemetryReceived += value;
        remove => session.TelemetryReceived -= value;
    }

    public event Action<string>? Disconnected
    {
        add => session.Disconnected += value;
        remove => session.Disconnected -= value;
    }

    public HelloInfo? Phone => session.Phone;
    public byte[]? PhoneStaticKey => session.PhoneStaticKey;
    public double? RttMs => session.Clock.RttMs;
    public long? ToPcUs(ulong phoneUs) => session.Clock.ToPcUs(phoneUs);
    public Task RunAsync(CancellationToken ct) => session.RunAsync(ct);
    public Task StartAsync(int width, int height) => session.StartAsync(width, height);
    public Task StopAsync() => session.StopAsync();
    public Task RequestKeyframeAsync() => session.RequestKeyframeAsync();
    public Task SendRouteAsync(bool video) => session.SendRouteAsync(video);
    public ValueTask DisposeAsync() => session.DisposeAsync();
}

/// <summary>
/// usbmux list -> first USB device -> tunnel to the phone app's port, then the authenticated v2 handshake in paired mode.
/// Without a paired phone there is nothing to connect to (pairing arrives with the CLI in a later task).
/// </summary>
sealed class UsbPhoneConnector(UsbmuxClient usbmux, ILogSink log, PairingStore store) : IPhoneConnector
{
    const ushort PhonePort = 47820;
    int _notPairedLogged;

    public async Task<IPhoneLink?> ConnectAsync(CancellationToken ct)
    {
        if (store.Phone is not { } paired)
        {
            if (Interlocked.Exchange(ref _notPairedLogged, 1) == 0)
                log.Write("pc", "no phone is paired; not connecting");
            return null;
        }
        var devices = await usbmux.ListDevicesAsync(ct);
        if (devices.Count == 0) return null;
        var stream = await usbmux.ConnectAsync(devices[0].DeviceId, PhonePort, ct);
        // PhoneSession timebase must match the host's qpc: microseconds of Stopwatch time.
        var session = new PhoneSession(stream, () => (ulong)(Stopwatch.GetTimestamp() * (1_000_000.0 / Stopwatch.Frequency)), log,
            new SecureLinkOptions(store.GetOrCreateIdentity(), store, paired, null));
        return new PhoneLink(session);
    }
}
