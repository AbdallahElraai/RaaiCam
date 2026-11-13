using RaaiCam.Core.Hosting;
using RaaiCam.Protocol;

namespace RaaiCam.Core.Tests;

public class LinkManagerTests
{
    const string WifiIp = "192.168.1.50";

    // ---- helpers ----

    /// <summary>USB streaming plus a confirmed Wi-Fi standby link.</summary>
    static async Task<(HostRig Rig, FakeLink Usb, FakeLink Wifi)> BothUpAsync()
    {
        var rig = new HostRig(wifi: true);
        rig.Consumer.Set(true, 1280, 720);
        await rig.StartAsync();
        var usb = rig.Link;
        rig.Wifi!.Offer(WifiIp);
        await rig.Obs.Until(() => rig.Wifi.Links.Count == 1);
        await rig.FlushAsync();
        return (rig, usb, rig.Wifi.Links[0]);
    }

    /// <summary>No USB phone at all; Wi-Fi connected and streaming.</summary>
    static async Task<(HostRig Rig, FakeLink Wifi)> WifiOnlyAsync()
    {
        var rig = new HostRig(wifi: true);
        rig.Connector.NoPhone = true;
        rig.Consumer.Set(true, 1280, 720);
        await rig.StartAsync();
        rig.Wifi!.Offer(WifiIp);
        await rig.Obs.Until(() => rig.Wifi.Links.Count == 1 && rig.Wifi.Links[0].CountPrefix("start") == 1);
        await rig.FlushAsync();
        return (rig, rig.Wifi.Links[0]);
    }

    static List<string> Bitrates(FakeLink l) => l.Commands.Where(c => c.StartsWith("bitrate")).ToList();

    static byte[] Key(byte fill) => Enumerable.Repeat(fill, 32).ToArray();

    // ---- failover and routing ----

    [Fact]
    public async Task Failover_UsbLossRoutesToWifiWithin1s()
    {
        var (rig, usb, wifi) = await BothUpAsync();
        await using var _ = rig;
        await rig.Obs.Until(() => rig.Decoders.Created.Count == 1);
        usb.RaiseVideo(1, true);
        await rig.Obs.Until(() => rig.Sink.Writes.Count == 1);

        var st = rig.Host.Status;
        Assert.Equal(("usb", "wifi"), (st.Link, st.Standby));
        Assert.Empty(wifi.Commands); // warm and idle: nothing but pings
        Assert.Equal(8000, st.TargetBitrateKbps);

        long t0 = rig.Time.GetTimestamp();
        usb.RaiseDisconnected("cable pulled");
        await rig.FlushAsync(); // no fake time passes at all

        Assert.Equal(new[] { "route video", "start 1280x720", "keyframe" }, wifi.Commands);
        Assert.Equal(new[] { 6000 }, wifi.StartKbps);
        Assert.True(rig.Time.GetElapsedTime(t0, rig.Time.GetTimestamp()) <= TimeSpan.FromSeconds(1));
        st = rig.Host.Status;
        Assert.Equal(("wifi", "none", 6000), (st.Link, st.Standby, st.TargetBitrateKbps));
        Assert.True(usb.Disposed);

        await rig.Obs.Until(() => rig.Decoders.Created.Count == 2);
        Assert.Equal((1280, 720), (rig.Decoders.Created[1].W, rig.Decoders.Created[1].H));
        wifi.RaiseVideo(10, true);
        await rig.Obs.Until(() => rig.Sink.Writes.Count == 2); // the first Wi-Fi keyframe reaches the sink
    }

    [Fact]
    public async Task Failover_UsbReturnSwitchesBackAtKeyframe()
    {
        var (rig, usb, wifi) = await BothUpAsync();
        await using var _ = rig;
        usb.RaiseDisconnected("cable pulled");
        await rig.FlushAsync();
        await rig.Obs.Until(() => rig.Decoders.Created.Count == 2);
        wifi.RaiseVideo(10, true);
        await rig.Obs.Until(() => rig.Sink.Writes.Count == 1);

        // The cable comes back: a new USB link connects after the 0.5 s backoff.
        await rig.StepAsync(6);
        await rig.Obs.Until(() => rig.Connector.Links.Count == 2 && rig.Connector.Links[1].CountPrefix("start") == 1);
        var usb2 = rig.Connector.Links[1];
        Assert.Equal(new[] { "route video", "start 1280x720" }, usb2.Commands);
        Assert.Equal(new[] { 8000 }, usb2.StartKbps);
        var st = rig.Host.Status;
        Assert.Equal(("wifi", "usb"), (st.Link, st.Standby)); // Wi-Fi keeps carrying video meanwhile
        Assert.True(wifi.Carrying);

        // Wi-Fi frames keep being decoded (no gap) ...
        wifi.RaiseVideo(11, false);
        await rig.Obs.Until(() => rig.Sink.Writes.Count == 2);
        // ... USB frames are dropped until its first keyframe.
        usb2.RaiseVideo(200, false);
        await rig.FlushAsync();
        Assert.Equal(1, rig.Host.Status.DroppedStandbyFrames);
        Assert.DoesNotContain(200, rig.Decoders.DecodedSeqs);
        Assert.True(wifi.Carrying);
        Assert.Equal("wifi", rig.Host.Status.Link);

        usb2.RaiseVideo(201, true);
        await rig.Obs.Until(() => rig.Decoders.DecodedSeqs.Contains(201));
        await rig.FlushAsync();
        await rig.Obs.Until(() => rig.Decoders.Created.Count == 3); // decoder reset at the switch
        st = rig.Host.Status;
        Assert.Equal(("usb", "wifi", 8000), (st.Link, st.Standby, st.TargetBitrateKbps));
        Assert.False(wifi.Carrying);
        Assert.True(usb2.Carrying);
        Assert.Equal(new[] { "route video", "start 1280x720", "bitrate 8000" }, usb2.Commands);
        Assert.Equal("route idle", wifi.Commands[^1]);

        wifi.RaiseVideo(12, false); // late Wi-Fi frame is now a standby frame
        await rig.FlushAsync();
        Assert.Equal(2, rig.Host.Status.DroppedStandbyFrames);
        Assert.DoesNotContain(12, rig.Decoders.DecodedSeqs);
    }

    [Fact]
    public async Task Route_OnlyOneSessionCarriesVideo()
    {
        var (rig, usb, wifi) = await BothUpAsync();
        await using var _ = rig;
        Assert.Equal(1, new[] { usb, wifi }.Count(l => l.Carrying));
        Assert.True(usb.Carrying);

        usb.RaiseDisconnected("cable pulled");
        await rig.FlushAsync();
        Assert.True(wifi.Carrying);

        await rig.StepAsync(6);
        await rig.Obs.Until(() => rig.Connector.Links.Count == 2 && rig.Connector.Links[1].CountPrefix("start") == 1);
        var usb2 = rig.Connector.Links[1];
        await rig.Obs.Until(() => rig.Decoders.Created.Count == 2);
        usb2.RaiseVideo(5, true);
        await rig.Obs.Until(() => rig.Host.Status.Link == "usb");
        await rig.FlushAsync();
        Assert.Equal(1, new[] { usb2, wifi }.Count(l => l.Carrying));
        Assert.True(usb2.Carrying);
        Assert.False(wifi.Carrying);
    }

    [Fact]
    public async Task Wifi_StreamsWhileUsbConnectIsPending()
    {
        // The loop never awaits a connect: a USB connect that takes forever must not hold up Wi-Fi.
        await using var rig = new HostRig(wifi: true);
        var gate = new TaskCompletionSource<IPhoneLink?>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Connector.ScriptAsync(_ => gate.Task);
        rig.Consumer.Set(true, 1280, 720);
        await rig.StartAsync();
        rig.Wifi!.Offer(WifiIp);
        await rig.Obs.Until(() => rig.Wifi.Links.Count == 1 && rig.Wifi.Links[0].CountPrefix("start") == 1);
        await rig.StepAsync(5);
        Assert.Equal("wifi", rig.Host.Status.Link);

        // The USB connect finally completes: it is warmed up and takes over at its first keyframe.
        var usb = rig.Connector.NewLink();
        gate.SetResult(usb);
        await rig.Obs.Until(() => usb.CountPrefix("start") == 1);
        rig.Wifi.Links[0].RaiseVideo(1, true);
        await rig.Obs.Until(() => rig.Sink.Writes.Count == 1);
        usb.RaiseVideo(50, true);
        await rig.Obs.Until(() => rig.Host.Status.Link == "usb");
    }

    [Fact]
    public async Task Wifi_LossWhileUsbActiveDropsStandbyAndRebrowses()
    {
        var (rig, usb, wifi) = await BothUpAsync();
        await using var _ = rig;
        Assert.Equal(1, rig.Wifi!.Browses);

        wifi.RaiseDisconnected("wifi gone");
        await rig.FlushAsync();
        var st = rig.Host.Status;
        Assert.Equal(("usb", "none"), (st.Link, st.Standby));
        Assert.True(wifi.Disposed);
        Assert.True(usb.Carrying);
        Assert.DoesNotContain("stop", usb.Commands);

        // The browse is restarted once the 0.5 s backoff has elapsed, and the candidates are tried again.
        await rig.StepAsync(4);
        Assert.Equal(1, rig.Wifi.Browses);
        await rig.StepAsync(3);
        await rig.Obs.Until(() => rig.Wifi.Browses == 2);
        rig.Wifi.Offer(WifiIp);
        await rig.Obs.Until(() => rig.Wifi.Links.Count == 2);
        await rig.FlushAsync();
        Assert.Equal("wifi", rig.Host.Status.Standby);
    }

    [Fact]
    public async Task Wifi_RecordsWorkingAddressOnConfirmationAndIgnoresCandidatesWhileLinked()
    {
        var (rig, _, _) = await BothUpAsync();
        await using var _r = rig;
        Assert.Equal(new[] { System.Net.IPAddress.Parse(WifiIp) }, rig.Wifi!.Recorded);

        rig.Wifi.Offer("192.168.1.51");
        rig.Wifi.Offer("192.168.1.52");
        await rig.StepAsync(3);
        await rig.FlushAsync();
        Assert.Single(rig.Wifi.Attempts);
    }

    [Fact]
    public async Task Wifi_UnconfirmedLinkIsNeverRoutedOrRecorded()
    {
        await using var rig = new HostRig(wifi: true);
        rig.Connector.NoPhone = true;
        var silent = new FakeLink(rig.Obs) { Phone = null };
        rig.Wifi!.OnConnect = _ => silent;
        rig.Consumer.Set(true, 1280, 720);
        await rig.StartAsync();
        rig.Wifi.Offer(WifiIp);
        await rig.Obs.Until(() => rig.Wifi.Links.Count == 1);
        await rig.StepAsync(30);

        Assert.Empty(silent.Commands);
        Assert.Empty(rig.Wifi.Recorded);
        Assert.Equal("none", rig.Host.Status.Link);
        Assert.Equal("none", rig.Host.Status.Standby);

        silent.Phone = new HelloInfo(2, "phone", "RaaiCam", "1.0", "iPhone 15", "iOS 18");
        await rig.StepAsync(1);
        Assert.Equal("wifi", rig.Host.Status.Link);
        Assert.Equal(new[] { "route video", "start 1280x720" }, silent.Commands);
        Assert.Single(rig.Wifi.Recorded);
    }

    [Fact]
    public async Task Wifi_ConnectIsBoundedTo3s()
    {
        await using var rig = new HostRig(wifi: true);
        rig.Wifi!.OnConnectAsync = async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct); // a black-holed address
            throw new InvalidOperationException("unreachable");
        };
        rig.Consumer.Set(true, 1280, 720);
        await rig.StartAsync();
        var usb = rig.Link;
        rig.Wifi.Offer(WifiIp);
        await rig.Obs.Until(() => rig.Wifi.Attempts.Count == 1);

        await rig.StepAsync(25); // the loop keeps ticking while the connect hangs
        Assert.Equal("usb", rig.Host.Status.Link);
        Assert.Single(rig.Wifi.Attempts);
        await rig.StepAsync(10); // 3 s: the token fires, the attempt fails, backoff starts
        await rig.WaitLogAsync("wifi connect failed");
        rig.Wifi.OnConnectAsync = null;
        await rig.StepAsync(8);
        rig.Wifi.Offer(WifiIp);
        await rig.Obs.Until(() => rig.Wifi.Links.Count == 1);
        await rig.FlushAsync();
        Assert.Equal("wifi", rig.Host.Status.Standby);
        Assert.True(usb.Carrying);
    }

    [Fact]
    public async Task Idle_ClosesBothLinksAndStopsBrowsing()
    {
        var (rig, usb, wifi) = await BothUpAsync();
        await using var _ = rig;
        Assert.Equal(1, rig.Wifi!.BrowseActive);

        rig.Consumer.Inactive();
        await rig.StepAsync(50);
        Assert.False(usb.Disposed);
        Assert.False(wifi.Disposed);
        Assert.Equal(1, usb.Count("stop"));
        await rig.StepAsync(55);
        Assert.True(usb.Disposed);
        Assert.True(wifi.Disposed);
        await rig.Obs.Until(() => rig.Wifi.BrowseActive == 0);
        Assert.Equal(("none", "none"), (rig.Host.Status.Link, rig.Host.Status.Standby));

        // Back in use: both links are rebuilt without waiting for any backoff.
        rig.Consumer.Set(true, 1280, 720);
        await rig.StepAsync(5);
        await rig.Obs.Until(() => rig.Connector.Links.Count == 2 && rig.Wifi.Browses == 2);
        rig.Wifi.Offer(WifiIp);
        await rig.Obs.Until(() => rig.Wifi.Links.Count == 2);
    }

    // ---- bitrate ladder ----

    [Fact]
    public async Task Bitrate_StepsDownOnBacklog()
    {
        var (rig, wifi) = await WifiOnlyAsync();
        await using var _ = rig;
        Assert.Equal(new[] { 6000 }, wifi.StartKbps);
        Assert.Equal(6000, rig.Host.Status.TargetBitrateKbps);
        await rig.Obs.Until(() => rig.Decoders.Created.Count == 1);

        using var entered = new SemaphoreSlim(0);
        using var gate = new ManualResetEventSlim(false);
        rig.Decoders.Behavior = (_, seq) =>
        {
            if (seq == 1) { entered.Release(); gate.Wait(TimeSpan.FromSeconds(20)); }
            return VideoDecodeResult.Frame;
        };
        try
        {
            wifi.RaiseVideo(1, true);
            Assert.True(await entered.WaitAsync(TimeSpan.FromSeconds(10)));
            for (uint s = 2; s <= 6; s++) wifi.RaiseVideo(s, false); // 5 frames waiting behind the stuck decode
            await rig.FlushAsync();

            await rig.StepAsync(18);
            Assert.Empty(Bitrates(wifi));
            await rig.StepAsync(4); // bad for 2 s
            Assert.Equal(new[] { "bitrate 4000" }, Bitrates(wifi));
            Assert.Equal(4000, rig.Host.Status.TargetBitrateKbps);

            await rig.StepAsync(15); // a fresh 2 s is required before the next step
            Assert.Single(Bitrates(wifi));
            await rig.StepAsync(8);
            Assert.Equal(new[] { "bitrate 4000", "bitrate 2500" }, Bitrates(wifi));

            await rig.StepAsync(60); // floor
            Assert.Equal(2, Bitrates(wifi).Count);
            Assert.Equal(2500, rig.Host.Status.TargetBitrateKbps);
        }
        finally { gate.Set(); }
    }

    [Fact]
    public async Task Bitrate_StepsDownOnHighRtt_AndUpAfter10sStable()
    {
        var (rig, wifi) = await WifiOnlyAsync();
        await using var _ = rig;

        wifi.RttMs = 200;
        await rig.StepAsync(45);
        Assert.Equal(new[] { "bitrate 4000", "bitrate 2500" }, Bitrates(wifi));

        wifi.RttMs = 3;
        await rig.StepAsync(60); // 6 s of good RTT is not enough
        Assert.Equal(2, Bitrates(wifi).Count);
        await rig.StepAsync(80); // ~14 s: one step up
        Assert.Equal(new[] { "bitrate 4000", "bitrate 2500", "bitrate 4000" }, Bitrates(wifi));
        Assert.Equal(4000, rig.Host.Status.TargetBitrateKbps);
        await rig.StepAsync(90); // another 10 s: back at the top
        Assert.Equal(new[] { "bitrate 4000", "bitrate 2500", "bitrate 4000", "bitrate 6000" }, Bitrates(wifi));
        await rig.StepAsync(120);
        Assert.Equal(4, Bitrates(wifi).Count);
        Assert.Equal(6000, rig.Host.Status.TargetBitrateKbps);
    }

    [Fact]
    public async Task Bitrate_UsbIsNeverAdapted()
    {
        await using var rig = new HostRig();
        rig.Consumer.Set(true, 1280, 720);
        await rig.StartStreamingAsync();
        rig.Link.RttMs = 500;
        await rig.StepAsync(60);
        Assert.Empty(Bitrates(rig.Link));
        Assert.Equal(new[] { 8000 }, rig.Link.StartKbps);
        Assert.Equal(8000, rig.Host.Status.TargetBitrateKbps);
    }

    // ---- reconnect backoff ----

    [Fact]
    public async Task Reconnect_BackoffSequence()
    {
        await using var rig = new HostRig();
        rig.Connector.NoPhone = true;
        rig.Consumer.Set(true, 1280, 720);
        await rig.StartAsync();
        await rig.StepAsync(180);

        var secs = rig.Connector.AttemptTimes.Select(t => TimeSpan.FromTicks(t).TotalSeconds).ToArray();
        Assert.Equal(new[] { 0, 0.5, 1.5, 3.5, 7.5, 12.5, 17.5 }, secs.Take(7));
    }

    [Fact]
    public async Task Reconnect_BackoffResetsOnceALinkIsConfirmed()
    {
        await using var rig = new HostRig();
        rig.Connector.Script(() => null);
        rig.Connector.Script(() => null);
        rig.Connector.Script(() => null); // third failure: next delay would be 2 s
        rig.Consumer.Set(true, 1280, 720);
        await rig.StartAsync();
        await rig.StepAsync(36); // 0, 0.5, 1.5 fail; the attempt at 3.5 s succeeds
        Assert.Equal(4, rig.Connector.Attempts);
        await rig.Obs.Until(() => rig.Link.CountPrefix("start") == 1);

        rig.Link.RaiseDisconnected("peer closed");
        await rig.FlushAsync();
        await rig.StepAsync(4);
        Assert.Equal(4, rig.Connector.Attempts);
        await rig.StepAsync(1); // 0.5 s after the loss, not 2 s
        Assert.Equal(5, rig.Connector.Attempts);
    }

    [Fact]
    public async Task Reconnect_WifiBackoffAfterFailedConnect()
    {
        await using var rig = new HostRig(wifi: true);
        rig.Connector.NoPhone = true;
        rig.Wifi!.OnConnect = _ => null; // refused
        rig.Consumer.Set(true, 1280, 720);
        await rig.StartAsync();
        rig.Wifi.Offer(WifiIp);
        await rig.Obs.Until(() => rig.Wifi.Attempts.Count == 1);
        rig.Wifi.Offer(WifiIp); // inside the 0.5 s backoff: ignored
        await rig.StepAsync(3);
        await rig.FlushAsync();
        Assert.Single(rig.Wifi.Attempts);

        // After the backoff the browse restarts and the candidate is offered again.
        await rig.StepAsync(4);
        await rig.Obs.Until(() => rig.Wifi.Browses == 2);
        rig.Wifi.OnConnect = null;
        rig.Wifi.Offer(WifiIp);
        await rig.Obs.Until(() => rig.Wifi.Links.Count == 1);
        await rig.FlushAsync();
        Assert.Equal("wifi", rig.Host.Status.Link);
    }

    // ---- other phones ----

    [Fact]
    public async Task Host_IgnoresOtherPhones()
    {
        await using var rig = new HostRig();
        rig.PairedKey = Key(0xA1);
        var stranger = rig.Connector.NewLink();
        stranger.PhoneStaticKey = Key(0xB2);
        rig.Connector.Script(() => stranger);
        rig.Consumer.Set(true, 1280, 720);
        await rig.StartAsync();

        Assert.True(stranger.Disposed);
        Assert.Empty(stranger.Commands);
        Assert.Equal("none", rig.Host.Status.Link);
        Assert.Contains(rig.Log.Lines, l => l.Contains("ignoring a phone that is not the paired one"));
        string all = string.Join("\n", rig.Log.Lines);
        Assert.DoesNotContain("B2B2B2", all, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("A1A1A1", all, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Convert.ToBase64String(Key(0xB2)), all);

        // The slot backs off like any failed attempt, then accepts the paired phone.
        await rig.StepAsync(4);
        Assert.Equal(1, rig.Connector.Attempts);
        var paired = rig.Connector.NewLink();
        paired.PhoneStaticKey = Key(0xA1);
        rig.Connector.Script(() => paired);
        await rig.StepAsync(1);
        await rig.Obs.Until(() => paired.CountPrefix("start") == 1);
        Assert.Equal("usb", rig.Host.Status.Link);
    }

    [Fact]
    public async Task Host_IgnoresOtherPhonesOverWifi()
    {
        await using var rig = new HostRig(wifi: true);
        rig.Connector.NoPhone = true;
        rig.PairedKey = Key(0xA1);
        var stranger = new FakeLink(rig.Obs) { PhoneStaticKey = Key(0xB2) };
        rig.Wifi!.OnConnect = _ => stranger;
        rig.Consumer.Set(true, 1280, 720);
        await rig.StartAsync();
        rig.Wifi.Offer(WifiIp);
        await rig.Obs.Until(() => stranger.Disposed);

        Assert.Empty(stranger.Commands);
        Assert.Empty(rig.Wifi.Recorded); // the address of a stranger is never remembered
        Assert.Equal("none", rig.Host.Status.Link);
        Assert.Equal("none", rig.Host.Status.Standby);
    }

    [Fact]
    public async Task Host_AcceptsAnyConfirmedPhoneWhenNoPairingKnown()
    {
        await using var rig = new HostRig();
        rig.PairedKeyFn = () => null;
        rig.Consumer.Set(true, 1280, 720);
        await rig.StartStreamingAsync();
        Assert.Equal("usb", rig.Host.Status.Link);
    }

    [Fact]
    public async Task Host_UnreadablePairingDropsThePhoneAndBacksOff()
    {
        await using var rig = new HostRig();
        rig.PairedKeyFn = () => throw new IOException("pairing store unreadable");
        rig.Consumer.Set(true, 1280, 720);
        await rig.StartAsync();

        Assert.True(rig.Link.Disposed);
        Assert.Empty(rig.Link.Commands);
        Assert.Equal("none", rig.Host.Status.Link);
        Assert.Contains(rig.Log.Lines, l => l.Contains("cannot verify the paired phone"));
        await rig.StepAsync(5);
        Assert.Equal(2, rig.Connector.Attempts); // keeps trying, loop alive
    }

    // ---- carried fixes ----

    [Fact]
    public async Task Decoder_EveryResetWaitsForAKeyframe()
    {
        await using var rig = new HostRig();
        rig.Consumer.Set(true, 1280, 720);
        await rig.StartAsync();
        await rig.Obs.Until(() => rig.Decoders.Created.Count == 1);

        rig.Link.RaiseVideo(1, false); // old P-frame into a fresh decoder
        await rig.Obs.Until(() => rig.Link.Count("keyframe") == 1);
        Assert.Empty(rig.Decoders.DecodedSeqs);

        rig.Link.RaiseVideo(2, true);
        rig.Link.RaiseVideo(3, false);
        await rig.Obs.Until(() => rig.Decoders.DecodedSeqs.Count == 2);
        Assert.Equal(new[] { 2, 3 }, rig.Decoders.DecodedSeqs);

        // Same after a restart at a new size.
        rig.Consumer.Set(true, 1920, 1080);
        await rig.StepAsync(1);
        await rig.Obs.Until(() => rig.Decoders.Created.Count == 2);
        rig.Link.RaiseVideo(4, false);
        rig.Link.RaiseVideo(5, true);
        await rig.Obs.Until(() => rig.Decoders.DecodedSeqs.Count == 3);
        Assert.Equal(new[] { 2, 3, 5 }, rig.Decoders.DecodedSeqs);
    }

    [Fact]
    public async Task Status_LinkIsReportedOnlyOnceConfirmed()
    {
        await using var rig = new HostRig();
        rig.Consumer.Set(true, 1280, 720);
        var link = rig.Connector.NewLink();
        link.Phone = null;
        link.PhoneStaticKey = null;
        rig.Connector.Script(() => link);
        await rig.StartAsync();
        await rig.StepAsync(5);
        Assert.Equal(("none", "none", null), (rig.Host.Status.Link, rig.Host.Status.Standby, rig.Host.Status.Phone));

        link.Phone = new HelloInfo(2, "phone", "RaaiCam", "1.0", "iPhone 15", "iOS 18");
        await rig.StepAsync(2);
        Assert.Equal("none", rig.Host.Status.Link); // INFO seen but the static key is still unknown
        Assert.Empty(link.Commands);

        link.PhoneStaticKey = new byte[32];
        await rig.StepAsync(1);
        Assert.Equal(("usb", "iPhone 15"), (rig.Host.Status.Link, rig.Host.Status.Phone));
    }

    [Fact]
    public async Task Teardown_HungDisposeIsAbandonedAfter2sWithoutBlockingTheLoop()
    {
        await using var rig = new HostRig();
        rig.Consumer.Set(true, 1280, 720);
        var link = rig.Connector.NewLink();
        link.DisposeGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Connector.Script(() => link);
        await rig.StartAsync();

        link.RaiseDisconnected("peer closed");
        await rig.FlushAsync();
        await rig.Obs.Until(() => link.Disposed);

        await rig.StepAsync(19); // loop is alive; a new link is already up after the 0.5 s backoff
        Assert.Equal(2, rig.Connector.Attempts);
        Assert.DoesNotContain(rig.Log.Lines, l => l.Contains("abandoning"));
        await rig.StepAsync(2);
        await rig.WaitLogAsync("link dispose did not finish within 2 s");
        link.DisposeGate.SetResult();
    }

    [Fact]
    public async Task Teardown_HungRunTaskIsAbandonedAfter2s()
    {
        await using var rig = new HostRig();
        rig.Consumer.Set(true, 1280, 720);
        var link = rig.Connector.NewLink();
        link.HangRun = true;
        rig.Connector.Script(() => link);
        await rig.StartAsync();

        link.RaiseDisconnected("peer closed");
        await rig.FlushAsync();
        await rig.StepAsync(19);
        Assert.DoesNotContain(rig.Log.Lines, l => l.Contains("abandoning"));
        await rig.StepAsync(2);
        await rig.WaitLogAsync("link run did not finish within 2 s");
    }

    [Fact]
    public async Task Start_HungStartNeverBlocksTheLoopAndIsRetried()
    {
        await using var rig = new HostRig();
        rig.Consumer.Set(true, 1280, 720);
        var link = rig.Connector.NewLink();
        link.StartGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Connector.Script(() => link);
        await rig.StartAsync();
        await rig.Obs.Until(() => link.Count("start 1280x720") == 1);

        await rig.StepAsync(25); // the loop keeps ticking
        Assert.Equal(1, link.Count("start 1280x720"));
        link.RaiseDisconnected("peer closed"); // and still handles events
        await rig.FlushAsync();
        Assert.True(link.Disposed);
        link.StartGate.SetResult();
    }

    [Fact]
    public async Task Start_HungStartIsAbandonedAfter2sAndRetriedLater()
    {
        await using var rig = new HostRig();
        rig.Consumer.Set(true, 1280, 720);
        var link = rig.Connector.NewLink();
        link.StartGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Connector.Script(() => link);
        await rig.StartAsync();
        await rig.Obs.Until(() => link.Count("start 1280x720") == 1);

        await rig.StepAsync(28);
        Assert.Equal(1, link.Count("start 1280x720")); // abandoned at 2 s, retry not before 3 s
        await rig.StepAsync(12);
        await rig.Obs.Until(() => link.Count("start 1280x720") == 2);
        Assert.Contains(rig.Log.Lines, l => l.Contains("start command did not finish within 2 s"));
        link.StartGate.SetResult();
    }

    [Fact]
    public async Task Start_FailedStartIsRetriedNoSoonerThan1sLater()
    {
        await using var rig = new HostRig();
        rig.Consumer.Set(true, 1280, 720);
        var link = rig.Connector.NewLink();
        link.StartFailures = 1;
        rig.Connector.Script(() => link);
        await rig.StartAsync();
        await rig.Obs.Until(() => link.Count("start 1280x720") == 1);

        await rig.StepAsync(9);
        Assert.Equal(1, link.Count("start 1280x720"));
        await rig.StepAsync(4);
        await rig.Obs.Until(() => link.Count("start 1280x720") == 2);
        Assert.Equal(new[] { "route video", "start 1280x720", "start 1280x720" }, link.Commands);
        await rig.StepAsync(30);
        Assert.Equal(2, link.Count("start 1280x720")); // retried once, now streaming
    }

    [Fact]
    public async Task Decoder_CreateFailureIsRetriedOnALaterTick()
    {
        await using var rig = new HostRig();
        rig.Decoders.CreateFailures = 1;
        rig.Consumer.Set(true, 1280, 720);
        await rig.StartAsync();
        await rig.Obs.Until(() => rig.Decoders.CreateAttempts == 1);

        await rig.StepAsync(8);
        Assert.Equal(1, rig.Decoders.CreateAttempts); // not before 1 s
        await rig.StepAsync(15);
        await rig.Obs.Until(() => rig.Decoders.Created.Count == 1);
        Assert.Equal(2, rig.Decoders.CreateAttempts);

        rig.Link.RaiseVideo(1, true);
        await rig.Obs.Until(() => rig.Sink.Writes.Count == 1);
        await rig.StepAsync(30);
        Assert.Equal(2, rig.Decoders.CreateAttempts); // no further retries once it works
    }

    // ---- unpair ----

    [Fact]
    public async Task Unpair_SucceedsAndClosesBothLinks()
    {
        var (rig, usb, wifi) = await BothUpAsync();
        await using var _ = rig;
        Assert.True(await rig.Host.UnpairAsync().WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(1, usb.Count("unpair"));
        Assert.True(usb.Disposed);
        Assert.True(wifi.Disposed);
        Assert.Equal("none", rig.Host.Status.Link);
    }

    [Fact]
    public async Task Unpair_StoreFailureReturnsFalseAndKeepsLinks()
    {
        await using var rig = new HostRig();
        rig.Consumer.Set(true, 1280, 720);
        await rig.StartStreamingAsync();
        rig.Link.UnpairThrows = new IOException("disk full");
        Assert.False(await rig.Host.UnpairAsync().WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Contains(rig.Log.Lines, l => l.Contains("unpair") && l.Contains("pairing store"));
        Assert.False(rig.Link.Disposed);
    }

    [Fact]
    public async Task Unpair_NotConfirmedReturnsFalse()
    {
        await using var rig = new HostRig();
        rig.Consumer.Set(true, 1280, 720);
        await rig.StartStreamingAsync();
        rig.Link.UnpairThrows = new InvalidOperationException("session is not confirmed");
        Assert.False(await rig.Host.UnpairAsync().WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.False(rig.Link.Disposed);

        await using var rig2 = new HostRig(); // no link at all
        rig2.Consumer.Inactive();
        await rig2.StartAsync();
        Assert.False(await rig2.Host.UnpairAsync().WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task Unpair_WhenHostIsNotRunningCompletesFalse()
    {
        var rig = new HostRig();
        await rig.StartAsync(waitFirstTick: false);
        rig.Cancel();
        await rig.Run.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(await rig.Host.UnpairAsync().WaitAsync(TimeSpan.FromSeconds(10)));
    }

    // ---- shutdown ----

    [Fact]
    public async Task Shutdown_CancelsAPendingConnectAndDisposesLateLinks()
    {
        var rig = new HostRig();
        var gate = new TaskCompletionSource<IPhoneLink?>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken seen = default;
        rig.Connector.ScriptAsync(ct => { seen = ct; return gate.Task; });
        rig.Consumer.Set(true, 1280, 720);
        await rig.StartAsync();
        await rig.Obs.Until(() => rig.Connector.Attempts == 1);

        rig.Cancel();
        await rig.Run.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(seen.IsCancellationRequested);

        var late = rig.Connector.NewLink(); // the connect finishes after the host is gone
        gate.SetResult(late);
        await rig.Obs.Until(() => late.Disposed);
    }
}
