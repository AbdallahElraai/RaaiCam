// RaaiCam Phase 0 spike: mirrors spikes/vcam/src/SharedFrame.h. Keep both in sync.
namespace FrameWriter;

internal static class SharedFrameLayout
{
    // The camera DLL (inside Frame Server, session 0) creates these. Global\ is tried first;
    // Local\ is the fallback the brief asks to record.
    public static readonly string[] MappingNames = { @"Global\RaaiCamSpikeFrames", @"Local\RaaiCamSpikeFrames" };
    public static readonly string[] EventNames = { @"Global\RaaiCamSpikeFrameReady", @"Local\RaaiCamSpikeFrameReady" };

    public const uint Magic = 0x52434631;      // 'RCF1'
    public const uint Version = 1;
    public const uint FormatNv12 = 0x3231564E; // 'NV12'
    public const int MaxWidth = 1920;
    public const int MaxHeight = 1080;

    // struct SharedFrameHeader (packed, little-endian, 72 bytes)
    public const int OffMagic = 0;
    public const int OffVersion = 4;
    public const int OffWidth = 8;
    public const int OffHeight = 12;
    public const int OffFormat = 16;
    public const int OffStride = 20;
    public const int OffFrameCounter = 24;   // uint64, incremented AFTER the frame is copied
    public const int OffTimestampQpc = 32;   // int64
    public const int OffConsumerActive = 40; // uint32
    public const int HeaderSize = 72;        // 28 reserved bytes follow consumerActive

    public const int MaxFrameBytes = MaxWidth * MaxHeight * 3 / 2;
    public const int MappingSize = HeaderSize + MaxFrameBytes;
}
