using System.Diagnostics;
using Xunit;
using RaaiCam.Media;

namespace RaaiCam.Media.Tests;

public class FramePacerTests
{
    [Fact]
    [Trait("Category", "Timing")]
    public void WaitNext_At30Fps_YieldsAbout30TicksPerSecond()
    {
        using var pacer = new FramePacer(30);
        using var cts = new CancellationTokenSource();
        var sw = Stopwatch.StartNew();
        int ticks = 0;
        while (sw.ElapsedMilliseconds < 1000 && pacer.WaitNext(cts.Token)) ticks++;
        Assert.InRange(ticks, 28, 32);
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var pacer = new FramePacer(30);
        pacer.Dispose();
        pacer.Dispose();
    }

    [Fact]
    public void WaitNext_ReturnsFalseWhenCancelled()
    {
        using var pacer = new FramePacer(30);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.False(pacer.WaitNext(cts.Token));
    }
}
