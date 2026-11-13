using RaaiCam.Core.Phone;

namespace RaaiCam.Core.Tests;

public class ClockSyncTests
{
    [Fact]
    public void ClockSync_NoSamples_IsNull()
    {
        var c = new ClockSync();
        Assert.Null(c.OffsetUs);
        Assert.Null(c.ToPcUs(5));
        Assert.Null(c.RttMs);
    }

    [Fact]
    public void ClockSync_UsesMinRttSample()
    {
        var c = new ClockSync();
        // rtt 10000, offset = 1_000_000 - (0 + 5000) = 995000
        c.AddSample(0, 1_000_000, 10_000);
        // rtt 2000, offset = 2_000_000 - (100_000 + 1000) = 1_899_000
        c.AddSample(100_000, 2_000_000, 102_000);
        // rtt 50000 (worse), offset = 9_000_000 - (200_000 + 25_000)
        c.AddSample(200_000, 9_000_000, 250_000);
        Assert.Equal(1_899_000, c.OffsetUs);
        Assert.Equal(2.0, c.RttMs);
        Assert.Equal(101_000L, c.ToPcUs(2_000_000));
    }

    [Fact]
    public void ClockSync_ForgetsSamplesOlderThan10s()
    {
        var c = new ClockSync();
        c.AddSample(0, 500_000, 1_000);                      // rtt 1000, offset 499_500
        c.AddSample(5_000_000, 6_000_000, 5_020_000);        // rtt 20000, offset 6_000_000-5_010_000 = 990_000
        Assert.Equal(499_500, c.OffsetUs);
        // newest at 11.1 s: first sample (pcRecv 1_000) is now > 10 s older
        c.AddSample(11_000_000, 12_000_000, 11_100_000);     // rtt 100000, offset 12_000_000-11_050_000 = 950_000
        Assert.Equal(990_000, c.OffsetUs);
    }

    [Fact]
    public void ClockSync_IgnoresNegativeRtt()
    {
        var c = new ClockSync();
        c.AddSample(1_000, 5, 500);
        Assert.Null(c.OffsetUs);
    }
}
