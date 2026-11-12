namespace RaaiCam.Media;

/// <summary>
/// Offsets and constants of the shared-frame mapping. Normative spec: protocol/SHARED_FRAME.md.
/// </summary>
public static class SharedFrameLayout
{
    public const string MappingName = @"Global\RaaiCam.VCam.Frames.v2";

    public const uint Magic = 0x32464352; // 'RCF2'
    public const int Version = 2;
    public const int HeaderBytes = 256;
    public const int SlotCount = 3;
    public const int SlotHeaderBytes = 64;
    public const int MaxFrameBytes = 1920 * 1080 * 3 / 2;
    public const int SlotStride = SlotHeaderBytes + MaxFrameBytes;
    public const int MappingBytes = HeaderBytes + SlotCount * SlotStride;

    public const int OffMaxWidth = 20;
    public const int OffMaxHeight = 24;

    // ControlHeader: written by the reader (DLL)
    public const int OffConsumerGeneration = 32;
    public const int OffConsumerActive = 40;
    public const int OffRequestedWidth = 44;
    public const int OffRequestedHeight = 48;
    public const int OffReaderHeartbeat = 56;

    // ControlHeader: written by the writer (app)
    public const int OffWriterPid = 64;
    public const int OffLatestSlot = 68;
    public const int OffWriterHeartbeat = 72;

    // SlotHeader fields (relative to the slot start)
    public const int SlotOffSeq = 0;
    public const int SlotOffWidth = 8;
    public const int SlotOffHeight = 12;
    public const int SlotOffStride = 16;
    public const int SlotOffFormat = 20;
    public const int SlotOffTimestamp = 24;
    public const int SlotOffFrameId = 32;

    public const int FormatNv12 = 0x3231564E; // 'NV12'

    public static int SlotOff(int i) => HeaderBytes + i * SlotStride;
}
