using Xunit;
using RaaiCam.Media;

namespace RaaiCam.Media.Tests;

public class CameraUsageMonitorTests
{
    private static CameraUsageMonitor Make(params (string, long, long)[] e) => new(() => e, () => DateTime.FromFileTimeUtc(0));

    [Fact]
    public void Usage_ActiveWhenStopIsZero()
    {
        var c = Make(("chrome.exe", 100, 0), ("zoom.exe", 50, 60)).CurrentClient();
        Assert.Equal("chrome.exe", c!.ExePath);
    }

    [Fact]
    public void Usage_MostRecentActiveWins()
    {
        var c = Make(("a.exe", 100, 0), ("b.exe", 200, 0)).CurrentClient();
        Assert.Equal("b.exe", c!.ExePath);
    }

    [Fact]
    public void Usage_DecodesHashPath()
    {
        var c = Make(("C:#Program Files#Zoom#bin#Zoom.exe", 1, 0)).CurrentClient();
        Assert.Equal(@"C:\Program Files\Zoom\bin\Zoom.exe", c!.ExePath);
    }

    [Fact]
    public void Usage_NullWhenNoneActive()
    {
        Assert.Null(Make(("a.exe", 10, 20)).CurrentClient());
        Assert.Null(Make().CurrentClient());
    }

    [Fact]
    public void Usage_NeverUsedEntryIsNotActive()
    {
        Assert.Null(Make(("Claude_x", 0, 0)).CurrentClient());
    }

    [Fact]
    public void Usage_StopBeforeStartIsActive()
    {
        Assert.Equal("a.exe", Make(("a.exe", 100, 50)).CurrentClient()!.ExePath);
    }

    [Fact]
    public void Usage_StartBeforeBootIsStale()
    {
        var boot = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc);
        var m = new CameraUsageMonitor(
            () => new[] { ("old.exe", boot.AddHours(-1).ToFileTimeUtc(), 0L), ("new.exe", boot.AddHours(1).ToFileTimeUtc(), 0L) },
            () => boot);
        Assert.Equal("new.exe", m.CurrentClient()!.ExePath);
        var m2 = new CameraUsageMonitor(() => new[] { ("old.exe", boot.AddHours(-1).ToFileTimeUtc(), 0L) }, () => boot);
        Assert.Null(m2.CurrentClient());
    }

    [Fact]
    public void Usage_ExcludesOwnProcesses()
    {
        var c = Make(("C:#x#raaicam-dev.exe", 300, 0), ("C:#x#vcam-probe.exe", 400, 0), ("a.exe", 100, 0)).CurrentClient();
        Assert.Equal("a.exe", c!.ExePath);
    }
}
