using Xunit;

namespace RaaiCam.Media.Tests;

public class VirtualCameraSinkTests
{
    private static string NewName() => @"Local\RaaiCamTest." + Guid.NewGuid().ToString("N");

    private static byte[] Frame(int w, int h, byte fill)
    {
        var b = new byte[w * h * 3 / 2];
        Array.Fill(b, fill);
        return b;
    }

    [Fact] public void Layout_OffsetsMatchSpec()
    {
        Assert.Equal(256, SharedFrameLayout.HeaderBytes);
        Assert.Equal(64 + 1920 * 1080 * 3 / 2, SharedFrameLayout.SlotStride);
        Assert.Equal(256 + 3 * SharedFrameLayout.SlotStride, SharedFrameLayout.MappingBytes);
        Assert.Equal(32, SharedFrameLayout.OffConsumerGeneration);
        Assert.Equal(68, SharedFrameLayout.OffLatestSlot);
        Assert.Equal(256 + SharedFrameLayout.SlotStride, SharedFrameLayout.SlotOff(1));
    }

    [Fact] public void TryOpen_FalseWhenMappingMissing()
    {
        using var sink = new VirtualCameraSink(NewName());
        Assert.False(sink.TryOpen());
        Assert.Null(sink.ReadConsumerState());
    }

    [Fact] public void TryOpen_FalseWhenMagicWrong()
    {
        var name = NewName();
        using var region = TestRegion.Create(name, magic: 0);
        using var sink = new VirtualCameraSink(name);
        Assert.False(sink.TryOpen());
    }

    [Fact] public void Write_RejectedWhenNotRequestedSize()
    {
        var name = NewName();
        using var region = TestRegion.Create(name);
        region.SetRequested(1280, 720, true);
        using var sink = new VirtualCameraSink(name);
        Assert.True(sink.TryOpen());
        Assert.False(sink.WriteFrame(Frame(1920, 1080, 1), 1920, 1080, 1920, 1));
        Assert.Equal(0xFFFFFFFFu, region.LatestSlot);
    }

    [Fact] public void Write_RejectedWhenInactiveOrBufferTooSmall()
    {
        var name = NewName();
        using var region = TestRegion.Create(name);
        using var sink = new VirtualCameraSink(name);
        Assert.True(sink.TryOpen());
        Assert.False(sink.WriteFrame(Frame(1280, 720, 1), 1280, 720, 1280, 1));
        region.SetRequested(1280, 720, true);
        Assert.False(sink.WriteFrame(new byte[100], 1280, 720, 1280, 1));
        Assert.Equal(0xFFFFFFFFu, region.LatestSlot);
    }

    [Fact] public void Write_CompletesSlotWithEvenSeqAndPublishesLatest()
    {
        var name = NewName();
        using var region = TestRegion.Create(name);
        region.SetRequested(1280, 720, true);
        using var sink = new VirtualCameraSink(name, () => 12345);
        Assert.True(sink.TryOpen());
        Assert.True(sink.WriteFrame(Frame(1280, 720, 0x42), 1280, 720, 1280, 777));
        int slot = (int)region.LatestSlot;
        Assert.InRange(slot, 0, 2);
        Assert.True(region.Seq(slot) > 0 && region.Seq(slot) % 2 == 0);
        Assert.Equal(1280u, region.SlotU32(slot, SharedFrameLayout.SlotOffWidth));
        Assert.Equal(720u, region.SlotU32(slot, SharedFrameLayout.SlotOffHeight));
        Assert.Equal(1280u, region.SlotU32(slot, SharedFrameLayout.SlotOffStride));
        Assert.Equal((uint)SharedFrameLayout.FormatNv12, region.SlotU32(slot, SharedFrameLayout.SlotOffFormat));
        Assert.Equal(1UL, region.FrameId(slot));
        Assert.Equal(0x42, region.PixelAt(slot, 0));
        Assert.Equal(0x42, region.PixelAt(slot, 1280 * 720 * 3 / 2 - 1));
        Assert.Equal((uint)Environment.ProcessId, region.WriterPid);
        Assert.Equal(12345, region.WriterHeartbeat);
    }

    [Fact] public void Write_NeverOverwritesLatestSlot()
    {
        var name = NewName();
        using var region = TestRegion.Create(name);
        region.SetRequested(1280, 720, true);
        using var sink = new VirtualCameraSink(name);
        Assert.True(sink.TryOpen());
        var f = Frame(1280, 720, 5);
        for (int i = 1; i <= 6; i++)
        {
            uint before = region.LatestSlot;
            Assert.True(sink.WriteFrame(f, 1280, 720, 1280, i));
            uint after = region.LatestSlot;
            Assert.NotEqual(before, after);
            Assert.Equal((ulong)i, region.FrameId((int)after));
        }
    }

    [Fact] public void Writer_FollowsRequestedSize()
    {
        var name = NewName();
        using var region = TestRegion.Create(name);
        region.SetRequested(1920, 1080, true);
        using var sink = new VirtualCameraSink(name);
        Assert.True(sink.TryOpen());
        Assert.True(sink.WriteFrame(Frame(1920, 1080, 1), 1920, 1080, 1920, 1));
        region.SetRequested(1280, 720, true);
        Assert.False(sink.WriteFrame(Frame(1920, 1080, 1), 1920, 1080, 1920, 2));
        Assert.True(sink.WriteFrame(Frame(1280, 720, 1), 1280, 720, 1280, 3));
    }

    [Fact] public void Writer_ReopensWhenGenerationChanges()
    {
        var name = NewName();
        using var region = TestRegion.Create(name);
        region.SetRequested(1280, 720, true);
        using var sink = new VirtualCameraSink(name);
        Assert.True(sink.TryOpen());
        region.BumpGeneration();
        var st = sink.ReadConsumerState();
        Assert.NotNull(st);
        Assert.Equal(2UL, st!.Generation);
        Assert.Equal(1, sink.ReopenCount);
        region.BumpGeneration();
        Assert.True(sink.WriteFrame(Frame(1280, 720, 1), 1280, 720, 1280, 1));
        Assert.Equal(3UL, sink.ReadConsumerState()!.Generation);
        Assert.Equal(2, sink.ReopenCount);
    }

    [Fact] public void Writer_GenerationZero_ClosesThenRecovers()
    {
        var name = NewName();
        using var region = TestRegion.Create(name);
        region.SetRequested(1280, 720, true);
        long now = 1000;
        using var sink = new VirtualCameraSink(name, () => now);
        Assert.True(sink.TryOpen());
        region.SetGeneration(0);
        Assert.Null(sink.ReadConsumerState());
        Assert.False(sink.WriteFrame(Frame(1280, 720, 1), 1280, 720, 1280, 1));
        Assert.Equal(0, sink.ReopenCount);
        region.SetGeneration(5);
        now += System.Diagnostics.Stopwatch.Frequency; // past the 500 ms retry throttle
        Assert.True(sink.WriteFrame(Frame(1280, 720, 1), 1280, 720, 1280, 2));
        Assert.Equal(5UL, sink.ReadConsumerState()!.Generation);
        Assert.Equal(1, sink.ReopenCount);
    }

    [Fact] public void NewSinkInSameProcess_ContinuesFrameIdsPastExistingSlots()
    {
        var name = NewName();
        using var region = TestRegion.Create(name);
        region.SetRequested(1280, 720, true);
        var f = Frame(1280, 720, 1);
        using (var a = new VirtualCameraSink(name))
        {
            Assert.True(a.TryOpen());
            for (int i = 1; i <= 5; i++) Assert.True(a.WriteFrame(f, 1280, 720, 1280, i));
        }
        using var b = new VirtualCameraSink(name);
        Assert.True(b.TryOpen());
        Assert.True(b.WriteFrame(f, 1280, 720, 1280, 6));
        Assert.True(region.FrameId((int)region.LatestSlot) > 5);
    }

    [Theory]
    [InlineData(1280u, 1080u)]
    [InlineData(1920u, 720u)]
    [InlineData(0u, 0u)]
    public void TryOpen_FalseWhenMaxDimensionsWrong(uint w, uint h)
    {
        var name = NewName();
        using var region = TestRegion.Create(name);
        region.SetMax(w, h);
        using var sink = new VirtualCameraSink(name);
        Assert.False(sink.TryOpen());
    }

    [Fact] public void Reopen_IsThrottledTo500ms()
    {
        long now = 1000;
        using var sink = new VirtualCameraSink(NewName(), () => now);
        Assert.False(sink.TryOpen());
        Assert.Equal(1, sink.OpenAttempts);
        now += System.Diagnostics.Stopwatch.Frequency / 4;
        Assert.Null(sink.ReadConsumerState());
        Assert.False(sink.WriteFrame(Frame(1280, 720, 1), 1280, 720, 1280, 1));
        Assert.Equal(1, sink.OpenAttempts);
        now += System.Diagnostics.Stopwatch.Frequency / 4;
        Assert.Null(sink.ReadConsumerState());
        Assert.Equal(2, sink.OpenAttempts);
    }

    [Fact] public void ReadConsumerState_ReflectsReaderFields()
    {
        var name = NewName();
        using var region = TestRegion.Create(name);
        region.SetRequested(1280, 720, true);
        using var sink = new VirtualCameraSink(name);
        Assert.True(sink.TryOpen());
        Assert.Equal(new ConsumerState(true, 1280, 720, 1), sink.ReadConsumerState());
    }
}
