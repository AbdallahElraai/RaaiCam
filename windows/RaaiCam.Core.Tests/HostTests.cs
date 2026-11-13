using RaaiCam.Core.Hosting;
using RaaiCam.Protocol;

namespace RaaiCam.Core.Tests;

public class HostTests
{
    [Fact]
    public async Task Host_StartsPhoneWhenConsumerActive()
    {
        await using var rig = new HostRig();
        rig.Consumer.Set(true, 1280, 720);
        await rig.StartAsync();

        Assert.Equal(1, rig.Connector.Attempts);
        Assert.Equal(new[] { "route video", "start 1280x720" }, rig.Link.Commands);
        await rig.Obs.Until(() => rig.Decoders.Created.Count == 1);
        Assert.Equal((1280, 720), (rig.Decoders.Created[0].W, rig.Decoders.Created[0].H));

        var st = rig.Host.Status;
        Assert.Equal("usb", st.Link);
        Assert.Equal("iPhone 15", st.Phone);
        Assert.Equal((1280, 720), (st.ConsumerWidth, st.ConsumerHeight));
        Assert.Equal(3.0, st.RttMs);
    }

    [Fact]
    public async Task Host_DoesNotConnectWhileConsumerInactive()
    {
        await using var rig = new HostRig();
        rig.Consumer.Inactive();
        await rig.StartAsync();
        await rig.StepAsync(30);

        Assert.Equal(0, rig.Connector.Attempts);
        Assert.Equal("none", rig.Host.Status.Link);
    }

    [Fact]
    public async Task Host_StopsPhoneWithin1sWhenInactive()
    {
        await using var rig = new HostRig();
        rig.Consumer.Set(true, 1280, 720);
        await rig.StartAsync();
        await rig.StepAsync(5);
        Assert.Equal(0, rig.Link.Count("stop"));

        rig.Consumer.Inactive();
        await rig.StepAsync(10); // 1 s
        Assert.Equal(1, rig.Link.Count("stop"));
        Assert.False(rig.Link.Disposed); // kept warm
    }

    [Fact]
    public async Task Host_ClosesSessionAfter10sIdle()
    {
        await using var rig = new HostRig();
        rig.Consumer.Set(true, 1280, 720);
        await rig.StartAsync();

        rig.Consumer.Inactive();
        await rig.StepAsync(99); // ~9.8 s since the first inactive tick
        Assert.False(rig.Link.Disposed);
        await rig.StepAsync(3);
        Assert.True(rig.Link.Disposed);
        Assert.Equal("none", rig.Host.Status.Link);
    }

    [Fact]
    public async Task Host_RestartsWarmSessionWithoutReconnecting()
    {
        await using var rig = new HostRig();
        rig.Consumer.Set(true, 1280, 720);
        await rig.StartAsync();
        rig.Consumer.Inactive();
        await rig.StepAsync(30);

        rig.Consumer.Set(true, 1280, 720);
        await rig.StepAsync(1);
        Assert.Equal(1, rig.Connector.Attempts);
        Assert.Equal(new[] { "route video", "start 1280x720", "stop", "start 1280x720" }, rig.Link.Commands);
    }

    [Fact]
    public async Task Host_RestartsPhoneOnSizeChange()
    {
        await using var rig = new HostRig();
        rig.Consumer.Set(true, 1280, 720);
        await rig.StartAsync();
        await rig.Obs.Until(() => rig.Decoders.Created.Count == 1);

        // Hold the 1280x720 decoder inside Decode while the size changes underneath it.
        using var entered = new SemaphoreSlim(0);
        using var gate = new ManualResetEventSlim(false);
        rig.Decoders.Behavior = (d, _) =>
        {
            if (d.W == 1280) { entered.Release(); gate.Wait(TimeSpan.FromSeconds(10)); }
            return VideoDecodeResult.Frame;
        };
        rig.Link.RaiseVideo(1, true);
        Assert.True(await entered.WaitAsync(TimeSpan.FromSeconds(10)));

        rig.Consumer.Set(true, 1920, 1080);
        await rig.StepAsync(1);
        Assert.Equal(new[] { "route video", "start 1280x720", "stop", "start 1920x1080" }, rig.Link.Commands);

        gate.Set(); // the stale 1280x720 frame now finishes decoding and must be dropped
        await rig.Obs.Until(() => rig.Decoders.Created.Count == 2);
        Assert.Equal((1920, 1080), (rig.Decoders.Created[1].W, rig.Decoders.Created[1].H));

        rig.Link.RaiseVideo(1, true);
        await rig.Obs.Until(() => rig.Sink.Writes.Count >= 1);
        var writes = rig.Sink.Writes;
        Assert.Single(writes);
        Assert.All(writes, w => Assert.Equal((1920, 1080), (w.W, w.H)));
    }

    [Fact]
    public async Task Host_RequestsKeyframeWhenDecoderAsks()
    {
        await using var rig = new HostRig();
        rig.Consumer.Set(true, 1280, 720);
        await rig.StartAsync();
        await rig.Obs.Until(() => rig.Decoders.Created.Count == 1);
        rig.Decoders.Behavior = (d, _) => { d.RaiseKeyframeNeeded(); return VideoDecodeResult.NeedMoreInput; };

        rig.Link.RaiseVideo(1, false);
        await rig.Obs.Until(() => rig.Link.Count("keyframe") == 1);
    }

    [Fact]
    public async Task Host_ReconnectsAfterDetach()
    {
        await using var rig = new HostRig();
        rig.Consumer.Set(true, 1280, 720);
        await rig.StartAsync();
        var first = rig.Link;

        first.RaiseDisconnected("peer closed");
        await rig.FlushAsync();
        await rig.StepAsync(10);

        Assert.True(first.Disposed);
        Assert.Equal(2, rig.Connector.Attempts);
        var second = rig.Connector.Links[1];
        Assert.Equal(new[] { "route video", "start 1280x720" }, second.Commands);
        Assert.Equal("usb", rig.Host.Status.Link);
    }

    [Fact]
    public async Task Host_RetriesConnectWithBackoffWhileActive()
    {
        // FR-CON-6: 0.5 s, 1 s, ... after each failed attempt (replaces the old fixed 1 s retry).
        await using var rig = new HostRig();
        rig.Consumer.Set(true, 1280, 720);
        rig.Connector.Script(() => null);                                   // no phone attached
        rig.Connector.Script(() => throw new IOException("usbmux down"));   // daemon not reachable
        await rig.StartAsync();
        Assert.Equal(1, rig.Connector.Attempts);

        await rig.StepAsync(4);
        Assert.Equal(1, rig.Connector.Attempts);
        await rig.StepAsync(1);   // 0.5 s
        Assert.Equal(2, rig.Connector.Attempts);
        await rig.StepAsync(9);   // 1.4 s
        Assert.Equal(2, rig.Connector.Attempts);
        await rig.StepAsync(1);   // 1.5 s
        Assert.Equal(3, rig.Connector.Attempts);
        Assert.Equal(new[] { "route video", "start 1280x720" }, rig.Link.Commands);
    }

    [Fact]
    public async Task Host_StampsFramesWithQpcAtWrite()
    {
        await using var rig = new HostRig();
        rig.Consumer.Set(true, 1280, 720);
        await rig.StartAsync();
        await rig.Obs.Until(() => rig.Decoders.Created.Count == 1);

        rig.Decoders.Behavior = (_, _) => { Interlocked.Exchange(ref rig.Qpc, 7_000); return VideoDecodeResult.Frame; };
        Interlocked.Exchange(ref rig.Qpc, 1_000); // time of arrival differs from time of write
        rig.Link.RaiseVideo(1, true, captureUs: 123);
        await rig.Obs.Until(() => rig.Sink.Writes.Count == 1);

        var w = rig.Sink.Writes[0];
        Assert.Equal(7_000, w.Qpc);
        Assert.Equal((1280, 720, 1280), (w.W, w.H, w.Stride));
    }

    [Fact]
    public async Task Host_LatencyUsesClockOffset()
    {
        await using var rig = new HostRig();
        rig.Consumer.Set(true, 1280, 720);
        await rig.StartAsync();
        await rig.Obs.Until(() => rig.Decoders.Created.Count == 1);

        // Phone clock runs 500 ms ahead of the PC clock. Captured at phone 2_000_000 us = PC 1_500_000 us.
        rig.Link.OffsetUs = 500_000;
        Interlocked.Exchange(ref rig.Qpc, 1_540_000); // qpc ticks are microseconds in this rig
        rig.Link.RaiseVideo(1, true, captureUs: 2_000_000);
        await rig.Obs.Until(() => rig.Sink.Writes.Count == 1);

        var st = rig.Host.Status;
        Assert.Equal(40.0, st.LatencyP50Ms!.Value, 3);
        Assert.Equal(40.0, st.LatencyP95Ms!.Value, 3);
        Assert.Equal(1.0, st.FpsIn, 3);
        Assert.Equal(1.0, st.FpsOut, 3);
    }

    // ---- carried findings ----

    [Fact]
    public async Task Host_EventHandlersNeverBlock()
    {
        // Finding 2: the loop is stuck awaiting StartAsync; session events (which in production run on the
        // session's read loop) must still return immediately and be handled once the loop is free.
        await using var rig = new HostRig();
        rig.Consumer.Set(true, 1280, 720);
        var link = rig.Connector.NewLink();
        link.StartGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Connector.Script(() => link);
        await rig.StartAsync(waitFirstTick: false);
        await rig.Obs.Until(() => link.Count("start 1280x720") == 1);

        var raised = Task.Run(() =>
        {
            link.RaiseVideo(1, true);
            link.RaiseTelemetry(new Telemetry("nominal", 0.8, true, true, 1280, 720, 30, 30, 8000, "wide", false));
            link.RaiseDisconnected("peer closed");
        });
        await raised.WaitAsync(TimeSpan.FromSeconds(5)); // would time out if a handler waited on the host loop

        link.StartGate.SetResult();
        await rig.Obs.Until(() => link.Disposed); // Disconnected handled on the host loop
    }

    [Fact]
    public async Task Host_WaitsForHelloBeforeSendingStart()
    {
        // Finding 3.
        await using var rig = new HostRig();
        rig.Consumer.Set(true, 1280, 720);
        var link = rig.Connector.NewLink();
        link.Phone = null;
        rig.Connector.Script(() => link);
        await rig.StartAsync();
        await rig.StepAsync(20);
        Assert.Empty(link.Commands);

        link.Phone = new HelloInfo(2, "phone", "RaaiCam", "1.0", "iPhone 15", "iOS 18");
        await rig.StepAsync(1);
        Assert.Equal(new[] { "route video", "start 1280x720" }, link.Commands);
    }

    [Fact]
    public async Task Host_CreatesUsesAndDisposesDecoderOnOneThread()
    {
        // Finding 4.
        await using var rig = new HostRig();
        rig.Consumer.Set(true, 1280, 720);
        await rig.StartAsync();
        await rig.Obs.Until(() => rig.Decoders.Created.Count == 1);
        rig.Link.RaiseVideo(1, true);
        await rig.Obs.Until(() => rig.Sink.Writes.Count == 1);

        rig.Consumer.Set(true, 1920, 1080);
        await rig.StepAsync(1);
        await rig.Obs.Until(() => rig.Decoders.Created.Count == 2);
        rig.Link.RaiseVideo(1, true);
        await rig.Obs.Until(() => rig.Sink.Writes.Count == 2);
        await rig.Obs.Until(() => rig.Decoders.Created[0].DisposedOnThread != -1);

        var d1 = rig.Decoders.Created[0];
        var d2 = rig.Decoders.Created[1];
        int t = d1.CreatedOnThread;
        Assert.NotEqual(Environment.CurrentManagedThreadId, t);
        Assert.Equal(t, d2.CreatedOnThread);
        Assert.All(d1.UsedOnThreads, x => Assert.Equal(t, x));
        Assert.All(d2.UsedOnThreads, x => Assert.Equal(t, x));
        Assert.Equal(t, d1.DisposedOnThread);

        rig.Cancel();
        await rig.Run;
        await rig.Obs.Until(() => d2.DisposedOnThread != -1);
        Assert.Equal(t, d2.DisposedOnThread);
    }

    [Fact]
    public async Task Host_RequestsKeyframeOnSequenceGapThrottledToOnePerSecond()
    {
        // Finding 5.
        await using var rig = new HostRig();
        rig.Consumer.Set(true, 1280, 720);
        await rig.StartAsync();

        rig.Link.RaiseVideo(500, true); // first frame after start: any seq is fine
        rig.Link.RaiseVideo(501, false);
        await rig.FlushAsync();
        Assert.Equal(0, rig.Link.Count("keyframe"));

        rig.Link.RaiseVideo(503, false); // 502 lost
        await rig.FlushAsync();
        Assert.Equal(1, rig.Link.Count("keyframe"));

        await rig.StepAsync(1);
        rig.Link.RaiseVideo(510, false); // another gap, but inside the 1 s throttle window
        await rig.FlushAsync();
        await rig.StepAsync(6);
        Assert.Equal(1, rig.Link.Count("keyframe"));
        await rig.StepAsync(3); // the throttled request goes out once the window has passed
        Assert.Equal(2, rig.Link.Count("keyframe"));
        await rig.StepAsync(20);
        Assert.Equal(2, rig.Link.Count("keyframe"));
    }

    [Fact]
    public async Task Host_DropsStaleFramesAndRequestsKeyframeWhenDecoderFallsBehind()
    {
        // Finding 6.
        await using var rig = new HostRig();
        rig.Consumer.Set(true, 1280, 720);
        await rig.StartAsync();
        await rig.Obs.Until(() => rig.Decoders.Created.Count == 1);

        using var entered = new SemaphoreSlim(0);
        using var gate = new ManualResetEventSlim(false);
        int calls = 0;
        rig.Decoders.Behavior = (_, _) =>
        {
            if (Interlocked.Increment(ref calls) == 1) { entered.Release(); gate.Wait(TimeSpan.FromSeconds(10)); }
            return VideoDecodeResult.Frame;
        };

        rig.Link.RaiseVideo(1, true);
        Assert.True(await entered.WaitAsync(TimeSpan.FromSeconds(10)));
        for (uint s = 2; s <= 8; s++) rig.Link.RaiseVideo(s, false);
        rig.Link.RaiseVideo(9, true);
        await rig.StepAsync(1); // events are FIFO: everything above has reached the decode queue

        gate.Set();
        await rig.Obs.Until(() => rig.Sink.Writes.Count == 2);
        Assert.Equal(new[] { 1, 9 }, rig.Decoders.DecodedSeqs); // backlog of non-key frames was never decoded
        await rig.Obs.Until(() => rig.Link.Count("keyframe") >= 1);
    }

    [Fact]
    public async Task Host_SendsStopAndDisposesLinkOnCancel()
    {
        await using var rig = new HostRig();
        rig.Consumer.Set(true, 1280, 720);
        await rig.StartAsync();
        var link = rig.Link;

        rig.Cancel();
        await rig.Run.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(new[] { "route video", "start 1280x720", "stop" }, link.Commands);
        Assert.True(link.Disposed);
    }
}
