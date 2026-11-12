using System.IO.MemoryMappedFiles;
using RaaiCam.Media;

namespace RaaiCam.Media.Tests;

/// <summary>Plays the DLL's role: creates the mapping and initializes the ControlHeader.</summary>
internal sealed class TestRegion : IDisposable
{
    private readonly MemoryMappedFile _mmf;
    private readonly MemoryMappedViewAccessor _view;
    public string Name { get; }

    private TestRegion(string name, MemoryMappedFile mmf)
    {
        Name = name;
        _mmf = mmf;
        _view = mmf.CreateViewAccessor(0, SharedFrameLayout.MappingBytes, MemoryMappedFileAccess.ReadWrite);
    }

    public static TestRegion Create(string localName, uint magic = SharedFrameLayout.Magic)
    {
        var mmf = MemoryMappedFile.CreateNew(localName, SharedFrameLayout.MappingBytes,
            MemoryMappedFileAccess.ReadWrite, MemoryMappedFileOptions.None, HandleInheritability.None);
        var r = new TestRegion(localName, mmf);
        r._view.Write(0, magic);
        r._view.Write(4, (uint)SharedFrameLayout.Version);
        r._view.Write(8, (uint)SharedFrameLayout.HeaderBytes);
        r._view.Write(12, (uint)SharedFrameLayout.SlotCount);
        r._view.Write(16, (uint)SharedFrameLayout.SlotStride);
        r._view.Write(20, 1920u);
        r._view.Write(24, 1080u);
        r._view.Write(SharedFrameLayout.OffConsumerGeneration, 1UL);
        r._view.Write(SharedFrameLayout.OffLatestSlot, 0xFFFFFFFFu);
        return r;
    }

    public void SetMax(uint w, uint h)
    {
        _view.Write(SharedFrameLayout.OffMaxWidth, w);
        _view.Write(SharedFrameLayout.OffMaxHeight, h);
    }

    public void SetRequested(int w, int h, bool active)
    {
        _view.Write(SharedFrameLayout.OffRequestedWidth, (uint)w);
        _view.Write(SharedFrameLayout.OffRequestedHeight, (uint)h);
        _view.Write(SharedFrameLayout.OffConsumerActive, active ? 1u : 0u);
    }

    public void BumpGeneration() =>
        _view.Write(SharedFrameLayout.OffConsumerGeneration, _view.ReadUInt64(SharedFrameLayout.OffConsumerGeneration) + 1);

    public void SetGeneration(ulong g) => _view.Write(SharedFrameLayout.OffConsumerGeneration, g);

    public uint LatestSlot => _view.ReadUInt32(SharedFrameLayout.OffLatestSlot);
    public uint WriterPid => _view.ReadUInt32(SharedFrameLayout.OffWriterPid);
    public long WriterHeartbeat => _view.ReadInt64(SharedFrameLayout.OffWriterHeartbeat);
    public ulong Seq(int slot) => _view.ReadUInt64(SharedFrameLayout.SlotOff(slot) + SharedFrameLayout.SlotOffSeq);
    public uint SlotU32(int slot, int off) => _view.ReadUInt32(SharedFrameLayout.SlotOff(slot) + off);
    public ulong FrameId(int slot) => _view.ReadUInt64(SharedFrameLayout.SlotOff(slot) + SharedFrameLayout.SlotOffFrameId);
    public byte PixelAt(int slot, int index) => _view.ReadByte(SharedFrameLayout.SlotOff(slot) + SharedFrameLayout.SlotHeaderBytes + index);

    public void Dispose() { _view.Dispose(); _mmf.Dispose(); }
}
