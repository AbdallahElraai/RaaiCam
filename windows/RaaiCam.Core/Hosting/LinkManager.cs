using System.Security.Cryptography;
using RaaiCam.Protocol;

namespace RaaiCam.Core.Hosting;

/// <summary>What <see cref="LinkManager"/> needs from the <see cref="Host"/> that owns it.</summary>
internal interface ILinkHost
{
    /// <summary>Queues work for the host loop. Returns false when the loop is gone (the work will never run).</summary>
    bool Post(Func<Task> work);
    void Log(string line);
    void LogThrottled(string key, string line);
    /// <summary>Disposes the decoder and (w &gt; 0) creates a new one that waits for a keyframe. Never blocks.</summary>
    void ResetDecoder(int w, int h, IPhoneLink? link);
    void EnqueueVideo(IPhoneLink link, VideoFrame frame);
    int DecodeBacklog { get; }
    void SetTelemetry(Telemetry? t);
}

/// <summary>Which links exist right now, as read by <see cref="Host.Status"/> from other threads.</summary>
internal sealed record LinkSnapshot(string Link, string Standby, string? Phone, IPhoneLink? Active)
{
    public static readonly LinkSnapshot None = new("none", "none", null, null);
}

/// <summary>
/// Keeps up to one USB and one Wi-Fi link to the paired phone, decides which carries video, switches between them
/// and adapts the Wi-Fi bitrate.
/// <para>
/// Ownership: every member except <see cref="Current"/>, <see cref="TargetKbps"/> and <see cref="DroppedStandbyFrames"/>
/// is called from the host loop only. Nothing here awaits a connect or a link command on the loop: connects run
/// off-loop and post their result back, commands go through a per-link chain with a 2 s bound per command, and link
/// teardown (dispose + run task) runs off-loop with the same bound.
/// </para>
/// </summary>
internal sealed class LinkManager
{
    static readonly TimeSpan[] BackoffDelays =
    {
        TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(5),
    };
    static readonly TimeSpan KeyframeMinInterval = TimeSpan.FromSeconds(1);
    static readonly TimeSpan IdleClose = TimeSpan.FromSeconds(10);
    static readonly TimeSpan CommandBound = TimeSpan.FromSeconds(2);
    static readonly TimeSpan WifiConnectBound = TimeSpan.FromSeconds(3);
    static readonly TimeSpan StartRetry = TimeSpan.FromSeconds(1);
    static readonly TimeSpan BrowseRetry = TimeSpan.FromSeconds(5);
    static readonly TimeSpan WarmKeyframeEvery = TimeSpan.FromSeconds(1);
    static readonly TimeSpan BadHold = TimeSpan.FromSeconds(2);
    static readonly TimeSpan StableHold = TimeSpan.FromSeconds(10);
    static readonly TimeSpan RttWindow = TimeSpan.FromSeconds(2);
    static readonly int[] WifiLadder = { 6000, 4000, 2500 };
    const int UsbKbps = 8000;
    const int BadBacklog = 2;
    const double BadRttMs = 150;

    sealed class Slot(LinkKind kind)
    {
        public readonly LinkKind Kind = kind;
        public string Name => Kind == LinkKind.Usb ? "usb" : "wifi";

        // Link state (reset whenever the link goes away).
        public IPhoneLink? Link;
        public Task Run = Task.CompletedTask;
        public Task Chain = Task.CompletedTask;
        public bool Confirmed;
        public bool Routed;          // told "route video=true" (and not told otherwise since)
        public bool Warming;         // USB only: started again while Wi-Fi still carries video
        public int StartW, StartH;   // size of the last start command; 0 = none (or it failed)
        public long? StartRetryAt;
        public int StartGen;
        public WifiCandidate? Candidate;
        public long? WarmKeyframeAt;

        // Connect / backoff state (survives the link).
        public int Failures;
        public long? NextAttempt;
        public bool Connecting;
        public int Gen;
        public CancellationTokenSource? ConnectCts;
    }

    readonly IPhoneConnector _usbConnector;
    readonly IWifiConnector? _wifi;
    readonly Func<byte[]?> _pairedKey;
    readonly TimeProvider _time;
    readonly ILinkHost _host;

    readonly Slot _usb = new(LinkKind.Usb);
    readonly Slot _wifiSlot = new(LinkKind.Wifi);
    Slot? _active;
    readonly List<Task> _teardowns = new();
    int _linkNo;

    bool _consumerActive;
    int _wantW, _wantH;
    long? _idleSince;
    uint? _lastSeq;
    long? _lastKeyframeSent;
    bool _keyframePending;

    // Wi-Fi discovery.
    CancellationTokenSource? _browseCts;
    bool _browseRunning;
    int _browseRunId;
    int _browseGen;
    bool _wifiRebrowse;
    long? _browseRetryAt;

    // Bitrate ladder (Wi-Fi only).
    int _level;
    long? _badSince;
    long _lastBad;
    readonly Queue<(long T, double Rtt)> _rtts = new();

    volatile LinkSnapshot _snapshot = LinkSnapshot.None;
    int _targetKbps;
    long _dropped;

    public LinkManager(IPhoneConnector usb, IWifiConnector? wifi, Func<byte[]?> pairedKey, TimeProvider time, ILinkHost host)
    {
        _usbConnector = usb;
        _wifi = wifi;
        _pairedKey = pairedKey;
        _time = time;
        _host = host;
    }

    public LinkSnapshot Current => _snapshot;
    public int TargetKbps => Volatile.Read(ref _targetKbps);
    public long DroppedStandbyFrames => Interlocked.Read(ref _dropped);

    /// <summary>True while a start command is in effect on the video-carrying link.</summary>
    public bool Streaming => _active is { StartW: > 0 };
    public (int W, int H, IPhoneLink? Link) Stream => _active is { StartW: > 0 } a ? (a.StartW, a.StartH, a.Link) : (0, 0, null);

    // ---- tick ----

    public void OnTick(bool consumerActive, int w, int h)
    {
        long now = _time.GetTimestamp();
        _consumerActive = consumerActive;
        _wantW = w;
        _wantH = h;
        _teardowns.RemoveAll(t => t.IsCompleted);

        if (consumerActive)
        {
            _idleSince = null;
            EnsureConnects(now);
            ConfirmPending();
            Reconcile(now);
        }
        else
        {
            ConfirmPending();
            if (AnyStreamState()) StopStreaming();
            bool anything = _usb.Link is not null || _wifiSlot.Link is not null || _usb.Connecting || _wifiSlot.Connecting
                            || _browseRunning;
            if (!anything) _idleSince = null;
            else
            {
                _idleSince ??= now;
                if (_time.GetElapsedTime(_idleSince.Value, now) >= IdleClose)
                {
                    _host.Log("consumer idle for 10 s, closing phone sessions");
                    CloseAll();
                }
            }
        }
        Publish();
    }

    void EnsureConnects(long now)
    {
        if (_usb.Link is null && !_usb.Connecting && Due(_usb, now)) ConnectUsb(now);
        EnsureBrowse(now);
    }

    static bool Due(Slot s, long now) => s.NextAttempt is not { } n || now >= n;

    void Reconcile(long now)
    {
        if (_active is { Link: not null } a)
        {
            var want = (_wantW, _wantH);
            if ((a.StartW, a.StartH) != want && (a.StartRetryAt is not { } r || now >= r))
                StartStream(a, isSwitch: false);

            if (a.StartW > 0 && a.Kind == LinkKind.Wifi) ManageWarming(now);
            if (a.StartW > 0 && a.Kind == LinkKind.Wifi) TickLadder(now, a);
            if (_keyframePending) RequestKeyframe(false);
        }
    }

    // ---- connecting ----

    void ConnectUsb(long now)
    {
        var s = _usb;
        s.Connecting = true;
        int gen = ++s.Gen;
        var cts = ResetConnectCts(s, null);
        Task<IPhoneLink?> t;
        try { t = _usbConnector.ConnectAsync(cts.Token); }
        catch (Exception e) { t = Task.FromException<IPhoneLink?>(e); }
        Complete(s, gen, t);
    }

    void ConnectWifi(WifiCandidate c)
    {
        var s = _wifiSlot;
        s.Connecting = true;
        int gen = ++s.Gen;
        var cts = ResetConnectCts(s, WifiConnectBound);
        s.Candidate = c;
        Task<IPhoneLink?> t;
        try { t = Widen(_wifi!.ConnectAsync(c, cts.Token)); }
        catch (Exception e) { t = Task.FromException<IPhoneLink?>(e); }
        Complete(s, gen, t);
    }

    static async Task<IPhoneLink?> Widen(Task<IPhoneLink> t) => await t.ConfigureAwait(false);

    CancellationTokenSource ResetConnectCts(Slot s, TimeSpan? bound)
    {
        CancelConnect(s);
        var cts = bound is { } b ? new CancellationTokenSource(b, _time) : new CancellationTokenSource();
        s.ConnectCts = cts;
        return cts;
    }

    static void CancelConnect(Slot s)
    {
        var old = s.ConnectCts;
        s.ConnectCts = null;
        if (old is null) return;
        try { old.Cancel(); } catch (Exception) { }
        try { old.Dispose(); } catch (Exception) { }
    }

    /// <summary>A connect that finished synchronously is handled right here (no extra loop turn); otherwise the result is posted back.</summary>
    void Complete(Slot s, int gen, Task<IPhoneLink?> t)
    {
        if (t.IsCompleted)
        {
            IPhoneLink? link = null;
            Exception? err = null;
            if (t.IsCompletedSuccessfully) link = t.Result;
            else err = t.Exception?.GetBaseException() ?? new OperationCanceledException();
            OnConnectResult(s, gen, link, err);
        }
        else _ = AwaitConnectAsync(s, gen, t);
    }

    async Task AwaitConnectAsync(Slot s, int gen, Task<IPhoneLink?> t)
    {
        IPhoneLink? link = null;
        Exception? err = null;
        try { link = await t.ConfigureAwait(false); }
        catch (Exception e) { err = e; }
        bool posted = _host.Post(() => { OnConnectResult(s, gen, link, err); return Task.CompletedTask; });
        if (!posted && link is not null)
        {
            try { await link.DisposeAsync().ConfigureAwait(false); } catch (Exception) { }
        }
    }

    void OnConnectResult(Slot s, int gen, IPhoneLink? link, Exception? err)
    {
        if (gen != s.Gen)
        {
            // Superseded (idle close, shutdown): nobody wants this link any more.
            if (link is not null) _teardowns.Add(TeardownAsync(link, Task.CompletedTask));
            return;
        }
        long now = _time.GetTimestamp();
        s.Connecting = false;
        CancelConnect(s);
        if (err is not null)
        {
            _host.LogThrottled("connect-" + s.Name, $"{s.Name} connect failed: {err.GetType().Name}: {err.Message}");
            Fail(s, now);
            return;
        }
        if (link is null) { Fail(s, now); return; } // no phone attached

        link.Video += f => _host.Post(() => { OnVideo(link, f); return Task.CompletedTask; });
        link.TelemetryReceived += t => _host.Post(() => { OnTelemetry(link, t); return Task.CompletedTask; });
        link.Disconnected += r => _host.Post(() => { OnDisconnected(link, r); return Task.CompletedTask; });
        s.Link = link;
        s.Confirmed = false;
        s.Chain = Task.CompletedTask;
        try { s.Run = link.RunAsync(CancellationToken.None); }
        catch (Exception e)
        {
            _host.LogThrottled("run-" + s.Name, $"{s.Name} link failed to start: {e.GetType().Name}: {e.Message}");
            DetachSlot(s, fail: true);
            Publish();
            return;
        }
        _host.Log($"{s.Name} link #{++_linkNo} up");
        ConfirmPending();
        if (_consumerActive) Reconcile(now);
        Publish();
    }

    void Fail(Slot s, long now)
    {
        var delay = BackoffDelays[Math.Min(s.Failures, BackoffDelays.Length - 1)];
        s.Failures++;
        s.NextAttempt = now + (long)(delay.TotalSeconds * _time.TimestampFrequency);
        if (s.Kind == LinkKind.Wifi) _wifiRebrowse = true;
    }

    // ---- confirmation ----

    void ConfirmPending()
    {
        foreach (var s in new[] { _usb, _wifiSlot })
            if (s.Link is { Phone: not null, PhoneStaticKey: not null } && !s.Confirmed) Confirm(s);
    }

    void Confirm(Slot s)
    {
        var link = s.Link!;
        long now = _time.GetTimestamp();
        byte[]? paired;
        try { paired = _pairedKey(); }
        catch (Exception e)
        {
            _host.LogThrottled("paired-read", $"cannot verify the paired phone ({e.GetType().Name}); closing {s.Name} link");
            DetachSlot(s, fail: true);
            return;
        }
        var key = link.PhoneStaticKey!;
        if (paired is not null && !CryptographicOperations.FixedTimeEquals(paired, key))
        {
            _host.LogThrottled("other-phone", "ignoring a phone that is not the paired one");
            DetachSlot(s, fail: true);
            return;
        }
        s.Confirmed = true;
        s.Failures = 0;
        _host.Log($"phone ready ({s.Name}): {link.Phone?.Device}");
        if (s.Kind == LinkKind.Wifi && s.Candidate is { } c)
        {
            try { _wifi?.RecordWorkingAddress(c.Address); }
            catch (Exception e) { _host.LogThrottled("record-addr", $"could not record the phone address: {e.GetType().Name}"); }
        }

        if (_active is null) SetActive(s, now);
        else if (s.Kind == LinkKind.Usb && _active.Kind == LinkKind.Wifi && _active.StartW == 0) SetActive(s, now);
    }

    void SetActive(Slot s, long now)
    {
        _active = s;
        _level = 0;
        _badSince = null;
        _lastBad = now;
        _rtts.Clear();
        _lastSeq = null;
        _keyframePending = false;
        _lastKeyframeSent = null;
        _host.SetTelemetry(null);
    }

    // ---- streaming control ----

    bool AnyStreamState() => _usb.StartW > 0 || _wifiSlot.StartW > 0 || _usb.Warming;

    int KbpsFor(Slot s) => s.Kind == LinkKind.Usb ? UsbKbps : WifiLadder[_level];

    void StartStream(Slot s, bool isSwitch)
    {
        var link = s.Link!;
        int w = _wantW, h = _wantH;
        bool restart = s.StartW != 0 && !s.Warming;
        s.Warming = false;
        _host.ResetDecoder(w, h, link);
        _lastSeq = null;
        _keyframePending = false;
        _lastKeyframeSent = null;
        RouteTo(s);
        if (restart)
        {
            _host.Log($"consumer size changed to {w}x{h}, restarting stream");
            Send(s, "stop", l => l.StopAsync());
        }
        IssueStart(s, w, h);
        _host.Log($"start {w}x{h} on {s.Name}");
        if (isSwitch) RequestKeyframe(bypass: true);
    }

    /// <summary>Makes <paramref name="s"/> the (only) link told to send video.</summary>
    void RouteTo(Slot s)
    {
        if (!s.Routed)
        {
            s.Routed = true;
            Send(s, "route", l => l.SendRouteAsync(true));
        }
        var o = Other(s);
        if (o.Link is not null && o.Routed)
        {
            o.Routed = false;
            o.StartW = o.StartH = 0;
            o.Warming = false;
            Send(o, "route", l => l.SendRouteAsync(false));
        }
    }

    void IssueStart(Slot s, int w, int h)
    {
        int kbps = KbpsFor(s);
        if (s == _active) Volatile.Write(ref _targetKbps, kbps);
        s.StartW = w;
        s.StartH = h;
        s.StartRetryAt = null;
        int gen = ++s.StartGen;
        var link = s.Link;
        Send(s, "start", l => l.StartAsync(w, h, kbps), ok =>
        {
            if (ok || s.StartGen != gen || s.Link != link) return;
            s.StartW = s.StartH = 0;
            s.Warming = false;
            s.StartRetryAt = _time.GetTimestamp() + (long)(StartRetry.TotalSeconds * _time.TimestampFrequency);
            _host.LogThrottled("start-failed", $"start on {s.Name} failed, will retry");
        });
    }

    void StopStreaming()
    {
        var a = _active;
        if (a is { Link: not null, StartW: > 0 }) Send(a, "stop", l => l.StopAsync());
        foreach (var s in new[] { _usb, _wifiSlot })
        {
            if (s != a && s.Link is not null && s.Routed)
            {
                s.Routed = false;
                Send(s, "route", l => l.SendRouteAsync(false));
            }
            s.StartW = s.StartH = 0;
            s.Warming = false;
        }
        _host.ResetDecoder(0, 0, null);
        _keyframePending = false;
        _lastSeq = null;
        Volatile.Write(ref _targetKbps, 0);
        _host.Log("consumer inactive, stop");
    }

    /// <summary>USB came back while Wi-Fi carries video: bring it up quietly and switch at its first keyframe.</summary>
    void ManageWarming(long now)
    {
        var u = _usb;
        if (u.Link is null || !u.Confirmed) return;
        var want = (_wantW, _wantH);
        if (!u.Warming)
        {
            if (u.StartRetryAt is { } r && now < r) return;
            u.Warming = true;
            u.WarmKeyframeAt = now;
            _host.Log("usb is back, warming it up while wifi keeps carrying video");
            RouteWarm(u);
            IssueStart(u, _wantW, _wantH);
        }
        else if ((u.StartW, u.StartH) != want)
        {
            Send(u, "stop", l => l.StopAsync());
            IssueStart(u, _wantW, _wantH);
        }
        else if (u.WarmKeyframeAt is { } k && _time.GetElapsedTime(k, now) >= WarmKeyframeEvery)
        {
            u.WarmKeyframeAt = now;
            Send(u, "keyframe", l => l.RequestKeyframeAsync());
        }
    }

    void RouteWarm(Slot u)
    {
        if (!u.Routed)
        {
            u.Routed = true;
            Send(u, "route", l => l.SendRouteAsync(true));
        }
    }

    void SwitchToUsb(Slot u, VideoFrame first)
    {
        long now = _time.GetTimestamp();
        var wifi = _wifiSlot;
        _host.Log("usb keyframe received, switching video back to usb");
        u.Warming = false;
        _active = u;
        _level = 0;
        _badSince = null;
        _lastBad = now;
        _rtts.Clear();
        _host.SetTelemetry(null);
        _host.ResetDecoder(u.StartW, u.StartH, u.Link);
        _lastSeq = first.Seq;
        _keyframePending = false;
        _host.EnqueueVideo(u.Link!, first);
        if (wifi.Link is not null)
        {
            wifi.Routed = false;
            wifi.StartW = wifi.StartH = 0;
            Send(wifi, "route", l => l.SendRouteAsync(false));
        }
        Volatile.Write(ref _targetKbps, UsbKbps);
        Send(u, "bitrate", l => l.SetBitrateAsync(UsbKbps));
        Publish();
    }

    // ---- bitrate ladder ----

    void TickLadder(long now, Slot a)
    {
        double? rtt = a.Link!.RttMs;
        if (rtt is { } r) _rtts.Enqueue((now, r));
        long window = (long)(RttWindow.TotalSeconds * _time.TimestampFrequency);
        while (_rtts.Count > 0 && now - _rtts.Peek().T > window) _rtts.Dequeue();

        double? p95 = null;
        if (_rtts.Count > 0)
        {
            var sorted = _rtts.Select(x => x.Rtt).OrderBy(x => x).ToArray();
            p95 = sorted[Math.Max(0, (int)Math.Ceiling(0.95 * sorted.Length) - 1)];
        }
        bool bad = _host.DecodeBacklog > BadBacklog || p95 > BadRttMs;
        if (bad)
        {
            _badSince ??= now;
            _lastBad = now;
            if (_time.GetElapsedTime(_badSince.Value, now) >= BadHold && _level < WifiLadder.Length - 1)
            {
                _level++;
                _badSince = now;
                ApplyLevel(a, "down");
            }
        }
        else
        {
            _badSince = null;
            if (_level > 0 && _time.GetElapsedTime(_lastBad, now) >= StableHold)
            {
                _level--;
                _lastBad = now;
                ApplyLevel(a, "up");
            }
        }
    }

    void ApplyLevel(Slot a, string dir)
    {
        int kbps = WifiLadder[_level];
        Volatile.Write(ref _targetKbps, kbps);
        _host.Log($"wifi bitrate {dir} to {kbps} kbps");
        Send(a, "bitrate", l => l.SetBitrateAsync(kbps));
    }

    // ---- keyframes ----

    /// <summary>Keyframe request to the video link, at most one per second unless <paramref name="bypass"/>; a throttled request is sent on a later tick.</summary>
    public void RequestKeyframe(bool bypass)
    {
        var a = _active;
        if (a is not { Link: not null, StartW: > 0 }) { _keyframePending = false; return; }
        long now = _time.GetTimestamp();
        if (!bypass && _lastKeyframeSent is { } t && _time.GetElapsedTime(t, now) < KeyframeMinInterval)
        {
            _keyframePending = true;
            return;
        }
        _keyframePending = false;
        _lastKeyframeSent = now;
        Send(a, "keyframe", l => l.RequestKeyframeAsync());
    }

    /// <summary>Re-creates the decoder for the current stream (the previous create failed).</summary>
    public void RetryDecoder()
    {
        if (_active is { StartW: > 0, Link: not null } a)
        {
            _host.ResetDecoder(a.StartW, a.StartH, a.Link);
            _lastSeq = null;
            RequestKeyframe(false);
        }
    }

    // ---- events from links ----

    Slot? SlotOf(IPhoneLink link) => ReferenceEquals(_usb.Link, link) ? _usb : ReferenceEquals(_wifiSlot.Link, link) ? _wifiSlot : null;

    Slot Other(Slot s) => s == _usb ? _wifiSlot : _usb;

    public void OnVideo(IPhoneLink link, VideoFrame f)
    {
        var s = SlotOf(link);
        if (s is null || !s.Confirmed) return;
        if (s.Warming)
        {
            if (f.Keyframe && _active is not null && _wantW > 0) SwitchToUsb(s, f);
            else Interlocked.Increment(ref _dropped);
            return;
        }
        if (s != _active)
        {
            Interlocked.Increment(ref _dropped);
            return;
        }
        if (s.StartW == 0) return;
        if (_lastSeq is { } last && f.Seq != unchecked(last + 1)) RequestKeyframe(false); // gap: refs may be broken
        _lastSeq = f.Seq;
        _host.EnqueueVideo(link, f);
    }

    public void OnTelemetry(IPhoneLink link, Telemetry t)
    {
        if (ReferenceEquals(_active?.Link, link)) _host.SetTelemetry(t);
    }

    public void OnDisconnected(IPhoneLink link, string reason)
    {
        var s = SlotOf(link);
        if (s is null) return;
        _host.Log($"phone link closed ({s.Name}): {reason}");
        long now = _time.GetTimestamp();
        bool wasActive = s == _active;
        DetachSlot(s, fail: true);
        if (wasActive)
        {
            _active = null;
            _host.SetTelemetry(null);
            var alt = Other(s);
            if (alt.Link is not null && alt.Confirmed)
            {
                SetActive(alt, now);
                if (_consumerActive && _wantW > 0)
                {
                    alt.StartW = 0; // a warm-up in progress is simply redone as a normal start
                    alt.Warming = false;
                    StartStream(alt, isSwitch: true);
                }
            }
            else
            {
                _host.ResetDecoder(0, 0, null);
                Volatile.Write(ref _targetKbps, 0);
                _keyframePending = false;
                _lastSeq = null;
            }
        }
        Publish();
    }

    // ---- Wi-Fi discovery ----

    void EnsureBrowse(long now)
    {
        if (_wifi is null) return;
        if (!_browseRunning)
        {
            if (_browseRetryAt is { } r && now < r) return;
            if (_wifiRebrowse && !Due(_wifiSlot, now)) return;
            _wifiRebrowse = false;
            _browseRetryAt = null;
            StartBrowse();
        }
        else if (_wifiRebrowse && Due(_wifiSlot, now) && _wifiSlot.Link is null)
        {
            try { _browseCts?.Cancel(); } catch (Exception) { }
        }
    }

    void StartBrowse()
    {
        try { _browseCts?.Dispose(); } catch (Exception) { }
        var cts = new CancellationTokenSource();
        _browseCts = cts;
        int gen = ++_browseGen;
        int run = ++_browseRunId;
        _browseRunning = true;
        _ = BrowseLoopAsync(gen, run, cts.Token);
    }

    async Task BrowseLoopAsync(int gen, int run, CancellationToken ct)
    {
        try
        {
            await foreach (var c in _wifi!.BrowseAsync(ct).WithCancellation(ct).ConfigureAwait(false))
            {
                var cand = c;
                if (!_host.Post(() => { OnCandidate(gen, cand); return Task.CompletedTask; })) return;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception e)
        {
            _host.LogThrottled("wifi-browse", $"wifi discovery failed: {e.GetType().Name}");
        }
        bool cancelled = ct.IsCancellationRequested;
        // Back on the loop: the single-consumer enumeration is over, so a new one may start (at once after a cancel).
        _host.Post(() =>
        {
            if (run != _browseRunId) return Task.CompletedTask;
            _browseRunning = false;
            long now = _time.GetTimestamp();
            if (!cancelled) _browseRetryAt = now + (long)(BrowseRetry.TotalSeconds * _time.TimestampFrequency);
            if (_consumerActive) { EnsureBrowse(now); Publish(); }
            return Task.CompletedTask;
        });
    }

    void OnCandidate(int gen, WifiCandidate c)
    {
        if (gen != _browseGen || _wifi is null || !_consumerActive) return;
        var s = _wifiSlot;
        if (s.Link is not null || s.Connecting || !Due(s, _time.GetTimestamp())) return;
        ConnectWifi(c);
        Publish();
    }

    void StopBrowse()
    {
        _browseGen++;
        try { _browseCts?.Cancel(); } catch (Exception) { }
        _wifiRebrowse = false;
        _browseRetryAt = null;
    }

    // ---- teardown ----

    /// <summary>Forgets the link (state reset, backoff or not) and disposes it off-loop.</summary>
    void DetachSlot(Slot s, bool fail)
    {
        var link = s.Link;
        var run = s.Run;
        s.Link = null;
        s.Run = Task.CompletedTask;
        s.Chain = Task.CompletedTask;
        s.Confirmed = s.Routed = s.Warming = false;
        s.StartW = s.StartH = 0;
        s.StartRetryAt = null;
        s.Candidate = null;
        if (fail) Fail(s, _time.GetTimestamp());
        else { s.NextAttempt = null; s.Failures = 0; }
        if (link is not null) _teardowns.Add(TeardownAsync(link, run));
        if (s == _active && !fail) _active = null;
    }

    void CloseAll()
    {
        StopBrowse();
        foreach (var s in new[] { _usb, _wifiSlot })
        {
            s.Gen++;
            s.Connecting = false;
            CancelConnect(s);
            DetachSlot(s, fail: false);
        }
        _active = null;
        _idleSince = null;
        _lastSeq = null;
        _keyframePending = false;
        _host.ResetDecoder(0, 0, null);
        _host.SetTelemetry(null);
        Volatile.Write(ref _targetKbps, 0);
        Publish();
    }

    async Task TeardownAsync(IPhoneLink link, Task run)
    {
        try { await WaitBoundedAsync(link.DisposeAsync().AsTask(), "link dispose").ConfigureAwait(false); } catch (Exception) { }
        try { await WaitBoundedAsync(run, "link run").ConfigureAwait(false); } catch (Exception) { }
    }

    /// <summary>Waits for <paramref name="t"/> for at most 2 s of <c>_time</c>; false (with a log line) when abandoned. Faults propagate.</summary>
    async Task<bool> WaitBoundedAsync(Task t, string what)
    {
        if (t.IsCompleted)
        {
            await t.ConfigureAwait(false);
            return true;
        }
        using var cts = new CancellationTokenSource();
        var delay = Task.Delay(CommandBound, _time, cts.Token);
        var first = await Task.WhenAny(t, delay).ConfigureAwait(false);
        if (first == t)
        {
            cts.Cancel();
            await t.ConfigureAwait(false);
            return true;
        }
        _ = t.ContinueWith(x => _ = x.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        _host.Log($"{what} did not finish within 2 s, abandoning it");
        return false;
    }

    // ---- link commands ----

    /// <summary>
    /// Queues a command for the link. Commands of one link run in order; each is bounded to 2 s and its failure is logged.
    /// A command that completes synchronously has run before this method returns. <paramref name="done"/> (when given) is
    /// called on the host loop with the outcome, only if the command failed or timed out.
    /// </summary>
    void Send(Slot s, string name, Func<IPhoneLink, Task> cmd, Action<bool>? done = null)
    {
        var link = s.Link;
        if (link is null) return;
        Task Run() => RunCommandAsync(link, name, cmd, done);
        var prev = s.Chain;
        s.Chain = prev.IsCompleted
            ? Run()
            : prev.ContinueWith(_ => Run(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
    }

    async Task RunCommandAsync(IPhoneLink link, string name, Func<IPhoneLink, Task> cmd, Action<bool>? done)
    {
        bool ok;
        try
        {
            Task t;
            try { t = cmd(link); }
            catch (Exception e) { t = Task.FromException(e); }
            ok = await WaitBoundedAsync(t, name + " command").ConfigureAwait(false);
        }
        catch (Exception e)
        {
            ok = false;
            _host.LogThrottled("cmd-" + name, $"{name} command failed: {e.GetType().Name}: {e.Message}");
        }
        if (!ok && done is not null) _host.Post(() => { done(false); return Task.CompletedTask; });
    }

    // ---- unpair / shutdown ----

    public async Task<bool> UnpairAsync()
    {
        var a = _active;
        if (a is not { Link: not null, Confirmed: true })
        {
            _host.Log("unpair: no confirmed phone link");
            return false;
        }
        try
        {
            if (!await WaitBoundedAsync(a.Link.UnpairAsync(), "unpair").ConfigureAwait(false)) return false;
        }
        catch (InvalidOperationException)
        {
            _host.Log("unpair: the phone link is not confirmed");
            return false;
        }
        catch (IOException e)
        {
            _host.Log($"unpair: could not update the pairing store ({e.GetType().Name})");
            return false;
        }
        catch (Exception e)
        {
            _host.Log($"unpair failed: {e.GetType().Name}");
            return false;
        }
        _host.Log("unpaired, closing phone sessions");
        CloseAll();
        return true;
    }

    public async Task ShutdownAsync()
    {
        StopBrowse();
        var a = _active;
        if (a is { Link: not null, StartW: > 0 })
        {
            Send(a, "stop", l => l.StopAsync());
            try { await a.Chain.ConfigureAwait(false); } catch (Exception) { }
        }
        CloseAll();
        try { await Task.WhenAll(_teardowns.ToArray()).ConfigureAwait(false); } catch (Exception) { }
    }

    void Publish()
    {
        var a = _active;
        if (a is not { Link: not null, Confirmed: true })
        {
            _snapshot = LinkSnapshot.None;
            return;
        }
        var o = Other(a);
        _snapshot = new LinkSnapshot(a.Name, o.Link is not null && o.Confirmed ? o.Name : "none", a.Link.Phone?.Device, a.Link);
    }
}
