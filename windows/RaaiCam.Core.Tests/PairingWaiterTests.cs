using RaaiCam.Core.Hosting;
using RaaiCam.Core.Pairing;

namespace RaaiCam.Core.Tests;

public class PairingWaiterTests
{
    static PairingSession NewSession(TimeProvider t) =>
        PairingSession.Begin(new PcIdentity(new byte[16], new byte[32], new byte[32]), "PC", t);

    static PairedPhone Phone(string label) => new(new byte[32], label, DateTimeOffset.UnixEpoch);

    static async Task<T> Within<T>(Task<T> t)
    {
        var done = await Task.WhenAny(t, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.Same(t, done);
        return await t;
    }

    [Fact]
    public async Task FirstSuccessWins_AndCancelsTheOtherRoute()
    {
        var time = new FakeTimeProvider();
        var otherCancelled = new TaskCompletionSource();
        Func<CancellationToken, Task<PairedPhone?>> usb = _ => Task.FromResult<PairedPhone?>(Phone("usb"));
        Func<CancellationToken, Task<PairedPhone?>> wifi = async ct =>
        {
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { otherCancelled.SetResult(); throw; }
            return null;
        };
        var r = await Within(PairingWaiter.WaitAsync(NewSession(time), [usb, wifi], time, CancellationToken.None));
        Assert.Equal("usb", r!.Label);
        await Within(otherCancelled.Task.ContinueWith(_ => 0));
    }

    [Fact]
    public async Task FailedAttempt_IsRetriedAfterOneSecond()
    {
        var time = new FakeTimeProvider();
        int calls = 0;
        var failures = new List<string>();
        Func<CancellationToken, Task<PairedPhone?>> route = _ =>
        {
            if (Interlocked.Increment(ref calls) == 1) throw new IOException("not yet");
            return Task.FromResult<PairedPhone?>(Phone("wifi"));
        };
        var t = PairingWaiter.WaitAsync(NewSession(time), [route], time, CancellationToken.None, failures.Add);
        await time.WaitForTimerAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(1, calls);
        time.Advance(TimeSpan.FromSeconds(1));
        var r = await Within(t);
        Assert.Equal("wifi", r!.Label);
        Assert.Equal(2, calls);
        Assert.Equal(["IOException"], failures);
    }

    [Fact]
    public async Task NullResult_RetriesUntilTheSessionExpires_ThenReturnsNull()
    {
        var time = new FakeTimeProvider();
        int calls = 0;
        Func<CancellationToken, Task<PairedPhone?>> route = _ => { Interlocked.Increment(ref calls); return Task.FromResult<PairedPhone?>(null); };
        var t = PairingWaiter.WaitAsync(NewSession(time), [route], time, CancellationToken.None);
        await time.WaitForTimerAsync(TimeSpan.FromSeconds(1));
        time.Advance(PairingSession.Lifetime);
        Assert.Null(await Within(t));
        Assert.True(calls >= 1);
    }

    [Fact]
    public async Task CallerCancellation_Throws()
    {
        var time = new FakeTimeProvider();
        using var cts = new CancellationTokenSource();
        Func<CancellationToken, Task<PairedPhone?>> route = async ct => { await Task.Delay(Timeout.Infinite, ct); return null; };
        var t = PairingWaiter.WaitAsync(NewSession(time), [route], time, cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Within(t));
    }

    [Fact]
    public async Task ExpiredSession_ReturnsNullWithoutTrying()
    {
        var clock = new ManualClock();
        var s = NewSession(clock);
        clock.Advance(TimeSpan.FromMinutes(6));
        int calls = 0;
        Func<CancellationToken, Task<PairedPhone?>> route = _ => { calls++; return Task.FromResult<PairedPhone?>(Phone("x")); };
        Assert.Null(await PairingWaiter.WaitAsync(s, [route], clock, CancellationToken.None));
        Assert.Equal(0, calls);
    }
}

public class StatusLineTests
{
    [Fact]
    public void Format_IsStableKeyValueAndRoundTrips()
    {
        var s = new HostStatus("usb", "My iPhone", 1920, 1080, 30.0, 29.9, 48, 71.4, 3, 8.0, "nominal", 82, true, "wifi", 8000);
        var line = StatusLine.Format(s);
        Assert.Equal("link=usb standby=wifi kbps=8000 phone=My iPhone consumer=1920x1080 in=30.0 out=29.9 lat=48/71ms rtt=3ms rate=8.0Mbps thermal=nominal batt=82%+", line);
        var p = StatusLine.TryParse(line)!;
        Assert.Equal("usb", p.Link);
        Assert.Equal("wifi", p.Standby);
        Assert.Equal(8000, p.Kbps);
        Assert.Equal("My iPhone", p.Phone);
        Assert.Equal("1920x1080", p.Consumer);
        Assert.Equal(71.0, p.P95Ms);
    }

    [Fact]
    public void Format_NoLinkNoData()
    {
        var line = StatusLine.Format(new HostStatus("none", null, 0, 0, 0, 0, null, null, null, null, null, null, null));
        Assert.Equal("link=none standby=none kbps=0 phone=- consumer=inactive in=0.0 out=0.0 lat=-/-ms rtt=- rate=- thermal=- batt=-", line);
        var p = StatusLine.TryParse(line)!;
        Assert.Equal("none", p.Link);
        Assert.Null(p.P95Ms);
        Assert.Null(StatusLine.TryParse("raaicam-host: running"));
    }
}
