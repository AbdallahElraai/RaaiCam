using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using RaaiCam.Core.Phone;

namespace RaaiCam.Core.Discovery;

public enum DiscoverySource { Mdns, LastKnown }

/// <summary>A phone address candidate. <see cref="IdHash"/> is empty for a <see cref="DiscoverySource.LastKnown"/> candidate with no id filter.</summary>
public sealed record DiscoveredPhone(string IdHash, IPAddress Address, int Port, DiscoverySource Source = DiscoverySource.Mdns);

/// <summary>
/// Finds the paired phone on the LAN through Bonjour (<c>_raaicam._tcp</c>, TXT <c>v=2</c>, <c>id=&lt;8 hex&gt;</c>).
/// Yields the last known address first, then mDNS results, re-querying with a 1 s, 2 s, 4 s ... 30 s backoff.
/// It only discovers: connecting (and authenticating, which is what actually protects against spoofed answers) is
/// the caller's job. <see cref="BrowseAsync"/> has a single consumer at a time.
/// </summary>
public sealed class WifiDiscovery : IAsyncDisposable
{
    public const int DefaultPort = 47820;
    const string ServiceName = "_raaicam._tcp.local";
    const string InstanceSuffix = "._raaicam._tcp.local";
    const int MaxInstances = 32, MaxHosts = 32, MaxAddressesPerHost = 8, MaxFollowUps = 8;
    const uint MaxTtlSeconds = 120;
    static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    readonly IMdnsTransport _transport;
    readonly SettingsStore _settings;
    readonly string? _expectedId;
    readonly TimeProvider _time;
    readonly ILogSink? _log;
    readonly Func<IPAddress, bool> _isOnLink;
    int _disposed;
    int _browsing;

    /// <param name="expectedId">Only yield phones advertising this id; null yields every phone with a valid id.</param>
    /// <param name="isOnLink">Test hook: whether a public-range address is inside a local interface subnet (default: cached interface prefixes).</param>
    public WifiDiscovery(IMdnsTransport transport, SettingsStore settings, string? expectedId = null,
                         TimeProvider? time = null, ILogSink? log = null, Func<IPAddress, bool>? isOnLink = null)
    {
        _transport = transport;
        _settings = settings;
        _expectedId = expectedId?.ToLowerInvariant();
        _time = time ?? TimeProvider.System;
        _log = log;
        _isOnLink = isOnLink ?? OnLinkCache.Contains;
    }

    /// <summary>The Bonjour id for a phone static public key: lowercase hex of the first 4 bytes of SHA-256 (matches iOS <c>BonjourPublisher.idHex</c>).</summary>
    public static string IdHashFor(byte[] phoneStaticPublic) =>
        Convert.ToHexString(SHA256.HashData(phoneStaticPublic).AsSpan(0, 4)).ToLowerInvariant();

    /// <summary>Remembers the address a connection actually succeeded on; it is tried first next time.</summary>
    public void RecordWorkingAddress(IPAddress address)
    {
        try { _settings.SetLastPhoneIp(address); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log("could not save last phone ip: " + e.GetType().Name);
        }
    }

    /// <summary>
    /// Last known address first, then mDNS results until cancelled. Single consumer: a second concurrent enumeration
    /// throws <see cref="InvalidOperationException"/> (the transport has one receive stream).
    /// </summary>
    public async IAsyncEnumerable<DiscoveredPhone> BrowseAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        if (Interlocked.CompareExchange(ref _browsing, 1, 0) != 0)
            throw new InvalidOperationException("BrowseAsync is already running; it supports a single consumer.");
        try
        {
            var seen = new SeenSet();

            IPAddress? last = _settings.LastPhoneIp;
            if (last is not null && IsUsable(last) && seen.Add((_expectedId ?? "", last)))
                yield return new DiscoveredPhone(_expectedId ?? "", last, DefaultPort, DiscoverySource.LastKnown);
            else last = null;

            var state = new BrowseState(_time, _expectedId, IsUsable);
            var channel = Channel.CreateUnbounded<DiscoveredPhone>();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var tasks = new[]
            {
                Task.Run(() => QueryLoop(state, cts.Token)),
                Task.Run(() => ReceiveLoop(state, seen, last, channel.Writer, cts.Token)),
            };
            try
            {
                await foreach (var p in channel.Reader.ReadAllAsync(cts.Token).ConfigureAwait(false))
                    yield return p;
            }
            finally
            {
                cts.Cancel();
                try { await Task.WhenAll(tasks).ConfigureAwait(false); } catch (Exception) { }
            }
        }
        finally { Volatile.Write(ref _browsing, 0); }
    }

    async Task QueryLoop(BrowseState state, CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(1);
        var baseQuery = new[] { new MdnsQuestion(ServiceName, MdnsRecordType.Ptr) };
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    byte[] q;
                    try { q = MdnsPacket.BuildQuery(state.Questions()); }
                    catch (ArgumentException) { q = MdnsPacket.BuildQuery(baseQuery); } // never let state block the base query
                    await _transport.SendAsync(q, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception e) { Log("query send failed: " + e.GetType().Name); }
                await Task.Delay(delay, _time, ct).ConfigureAwait(false);
                delay = delay * 2 > MaxBackoff ? MaxBackoff : delay * 2;
            }
        }
        catch (OperationCanceledException) { }
    }

    async Task ReceiveLoop(BrowseState state, SeenSet seen, IPAddress? lastKnown,
                           ChannelWriter<DiscoveredPhone> output, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                MdnsDatagram d;
                try { d = await _transport.ReceiveAsync(ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                catch (ChannelClosedException) { return; }
                catch (Exception e)
                {
                    Log("receive failed: " + e.GetType().Name);
                    await Task.Delay(TimeSpan.FromSeconds(1), _time, ct).ConfigureAwait(false);
                    continue;
                }

                try
                {
                    state.Apply(MdnsPacket.Parse(d.Data));
                    foreach (var p in state.Resolved())
                    {
                        // With no id filter the LastKnown candidate has no id, so de-duplicate it by address alone.
                        if (_expectedId is null && lastKnown is not null && p.Address.Equals(lastKnown)) continue;
                        if (seen.Add((p.IdHash, p.Address))) output.TryWrite(p);
                    }
                }
                catch (Exception e) { Log("ignored bad packet: " + e.GetType().Name); }
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Only addresses a phone on our LAN can have: RFC 1918, link-local, or inside a local interface's subnet.</summary>
    bool IsUsable(IPAddress a)
    {
        if (a.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = a.GetAddressBytes();
        if (b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254))
            return true;
        if (b[0] == 0 || b[0] == 127 || b[0] >= 224) return false;
        return _isOnLink(a);
    }

    static bool IsIdHex(string s)
    {
        if (s.Length != 8) return false;
        foreach (var c in s)
            if (!(c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F')) return false;
        return true;
    }

    /// <summary>True when every label survives a UTF-8 round trip and is at most 63 bytes, so the name can be put in a query.</summary>
    internal static bool IsEncodableName(string name)
    {
        if (name.Length == 0 || name.Length > MdnsPacket.MaxNameBytes) return false;
        foreach (var label in name.Split('.'))
        {
            if (label.Length == 0 || label.Contains('�')) return false;
            if (Encoding.UTF8.GetByteCount(label) > 63) return false;
        }
        return true;
    }

    void Log(string line) { try { _log?.Write("discovery", line); } catch (Exception) { } }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _transport.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Accumulates PTR/SRV/TXT/A records across packets. Bounded and self-cleaning: at most 32 instances / 32 hosts
    /// (the oldest is evicted when full), entries expire after their TTL (capped at 120 s), TTL 0 removes them, and
    /// with an id filter only the expected instance name is admitted.
    /// </summary>
    sealed class BrowseState
    {
        sealed class Instance
        {
            public string? Host;
            public int Port;
            public string? Id;
            public long Expires, Seq;
        }

        sealed class HostEntry
        {
            public readonly Dictionary<IPAddress, long> Addrs = new();
            public long Seq;
        }

        readonly object _gate = new();
        readonly Dictionary<string, Instance> _instances = new(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, HostEntry> _hosts = new(StringComparer.OrdinalIgnoreCase);
        readonly TimeProvider _time;
        readonly string? _expectedId;
        readonly string? _expectedInstance;
        long _seq;

        // With an id filter, A records seen before the expected instance's SRV name its host are held here (first four
        // names win, so junk arriving later cannot push the real phone's address out) and promoted once the SRV arrives.
        readonly Dictionary<string, HostEntry> _pending = new(StringComparer.OrdinalIgnoreCase);
        const int MaxPending = 4;
        readonly Func<IPAddress, bool> _isUsable;

        public BrowseState(TimeProvider time, string? expectedId, Func<IPAddress, bool> isUsable)
        {
            _isUsable = isUsable;
            _time = time;
            _expectedId = expectedId;
            _expectedInstance = expectedId is null ? null : "RaaiCam-" + expectedId + InstanceSuffix;
        }

        long Deadline(long now, uint ttl) => now + (long)Math.Min(ttl, MaxTtlSeconds) * _time.TimestampFrequency;

        bool Admit(string name) =>
            name.EndsWith(InstanceSuffix, StringComparison.OrdinalIgnoreCase) && IsEncodableName(name)
            && (_expectedInstance is null || name.Equals(_expectedInstance, StringComparison.OrdinalIgnoreCase));

        public void Apply(IReadOnlyList<MdnsRecord> records)
        {
            lock (_gate)
            {
                long now = _time.GetTimestamp();
                Purge(now);

                foreach (var r in records)
                {
                    switch (r.Type)
                    {
                        case MdnsRecordType.Ptr when r.Name.Equals(ServiceName, StringComparison.OrdinalIgnoreCase)
                                                   && r.Target is { } t && Admit(t):
                            Touch(t, r.Ttl, now);
                            break;
                        case MdnsRecordType.Srv when Admit(r.Name) && r.Port is > 0 and <= 65535
                                                   && r.Target is { } host && IsEncodableName(host):
                            if (Touch(r.Name, r.Ttl, now) is { } s)
                            {
                                s.Host = host; s.Port = r.Port;
                                PromotePending(host);
                            }
                            break;
                        case MdnsRecordType.Txt when Admit(r.Name) && r.Txt is { } txt:
                            if (Touch(r.Name, r.Ttl, now) is { } x)
                                x.Id = txt.TryGetValue("v", out var v) && v == "2" && txt.TryGetValue("id", out var id) && IsIdHex(id)
                                    ? id.ToLowerInvariant() : null;
                            break;
                    }
                }

                // Address records last, so the SRV in the same packet has already named the host we care about.
                foreach (var r in records)
                {
                    if (r.Type != MdnsRecordType.A || r.Address is not { } a || !_isUsable(a) || !IsEncodableName(r.Name)) continue;

                    if (_expectedInstance is not null)
                    {
                        string? known = _instances.TryGetValue(_expectedInstance, out var inst) ? inst.Host : null;
                        if (known is not null)
                        {
                            if (!known.Equals(r.Name, StringComparison.OrdinalIgnoreCase)) continue; // not the phone's host
                        }
                        else
                        {
                            // Host not named yet: park it in the small first-wins buffer instead of the shared table.
                            if (r.Ttl == 0) continue;
                            if (!_pending.TryGetValue(r.Name, out var p))
                            {
                                if (_pending.Count >= MaxPending) continue;
                                _pending[r.Name] = p = new HostEntry();
                            }
                            if (p.Addrs.Count < MaxAddressesPerHost || p.Addrs.ContainsKey(a)) p.Addrs[a] = Deadline(now, r.Ttl);
                            continue;
                        }
                    }

                    if (r.Ttl == 0)
                    {
                        if (_hosts.TryGetValue(r.Name, out var gone)) { gone.Addrs.Remove(a); if (gone.Addrs.Count == 0) _hosts.Remove(r.Name); }
                        continue;
                    }
                    if (!_hosts.TryGetValue(r.Name, out var h))
                    {
                        if (_hosts.Count >= MaxHosts) EvictOldest(_hosts, e => e.Seq);
                        _hosts[r.Name] = h = new HostEntry();
                    }
                    h.Seq = ++_seq; // last seen: every re-announcement refreshes it
                    if (h.Addrs.Count < MaxAddressesPerHost || h.Addrs.ContainsKey(a)) h.Addrs[a] = Deadline(now, r.Ttl);
                }
            }
        }

        /// <summary>The expected instance just named its host: move its buffered addresses into the table, drop the rest.</summary>
        void PromotePending(string host)
        {
            if (_expectedInstance is null) return;
            if (_pending.TryGetValue(host, out var p) && p.Addrs.Count > 0)
            {
                if (!_hosts.TryGetValue(host, out var h))
                {
                    if (_hosts.Count >= MaxHosts) EvictOldest(_hosts, e => e.Seq);
                    _hosts[host] = h = new HostEntry();
                }
                h.Seq = ++_seq;
                foreach (var (addr, exp) in p.Addrs)
                    if (h.Addrs.Count < MaxAddressesPerHost || h.Addrs.ContainsKey(addr)) h.Addrs[addr] = exp;
            }
            _pending.Clear();
        }

        /// <summary>Creates or refreshes an instance; TTL 0 (goodbye) removes it and returns null.</summary>
        Instance? Touch(string name, uint ttl, long now)
        {
            if (ttl == 0) { _instances.Remove(name); return null; }
            if (!_instances.TryGetValue(name, out var i))
            {
                if (_instances.Count >= MaxInstances) EvictOldest(_instances, e => e.Seq);
                _instances[name] = i = new Instance();
            }
            i.Seq = ++_seq; // last seen: eviction removes the least recently seen entry
            i.Expires = Deadline(now, ttl);
            return i;
        }

        static void EvictOldest<T>(Dictionary<string, T> d, Func<T, long> seq)
        {
            string? oldest = null; long best = long.MaxValue;
            foreach (var (k, v) in d) if (seq(v) < best) { best = seq(v); oldest = k; }
            if (oldest is not null) d.Remove(oldest);
        }

        void Purge(long now)
        {
            foreach (var k in _instances.Where(kv => kv.Value.Expires <= now).Select(kv => kv.Key).ToList()) _instances.Remove(k);
            foreach (var (k, h) in _pending.ToList())
            {
                foreach (var a in h.Addrs.Where(kv => kv.Value <= now).Select(kv => kv.Key).ToList()) h.Addrs.Remove(a);
                if (h.Addrs.Count == 0) _pending.Remove(k);
            }
            foreach (var (k, h) in _hosts.ToList())
            {
                foreach (var a in h.Addrs.Where(kv => kv.Value <= now).Select(kv => kv.Key).ToList()) h.Addrs.Remove(a);
                if (h.Addrs.Count == 0) _hosts.Remove(k);
            }
        }

        public IEnumerable<DiscoveredPhone> Resolved()
        {
            var found = new List<DiscoveredPhone>();
            lock (_gate)
            {
                Purge(_time.GetTimestamp());
                foreach (var i in _instances.Values)
                {
                    if (i.Id is null || i.Host is null || i.Port == 0) continue;
                    if (_expectedId is not null && i.Id != _expectedId) continue;
                    if (!_hosts.TryGetValue(i.Host, out var h)) continue;
                    foreach (var a in h.Addrs.Keys) found.Add(new DiscoveredPhone(i.Id, a, i.Port));
                }
            }
            return found;
        }

        /// <summary>The service PTR question plus follow-ups for partly resolved instances/hosts (only names that encode).</summary>
        public List<MdnsQuestion> Questions()
        {
            var qs = new List<MdnsQuestion> { new(ServiceName, MdnsRecordType.Ptr) };
            lock (_gate)
            {
                Purge(_time.GetTimestamp());
                foreach (var (name, i) in _instances)
                {
                    if (qs.Count > MaxFollowUps) break;
                    if (i.Host is null || i.Id is null) { if (IsEncodableName(name)) qs.Add(new MdnsQuestion(name, MdnsRecordType.Any)); }
                    else if (!_hosts.ContainsKey(i.Host) && IsEncodableName(i.Host)) qs.Add(new MdnsQuestion(i.Host, MdnsRecordType.A));
                }
            }
            return qs;
        }
    }
}

/// <summary>Local IPv4 subnets, enumerated once and refreshed when the machine's addresses change (not per record).</summary>
static class OnLinkCache
{
    static readonly object Gate = new();
    static (byte[] Addr, int Prefix)[]? _subnets;
    static bool _subscribed;

    public static bool Contains(IPAddress a)
    {
        if (a.AddressFamily != AddressFamily.InterNetwork) return false;
        var subnets = Get();
        var target = a.GetAddressBytes();
        foreach (var (local, prefix) in subnets)
        {
            bool same = true;
            for (int bit = 0; bit < prefix && same; bit++)
                same = ((local[bit / 8] ^ target[bit / 8]) & (0x80 >> (bit % 8))) == 0;
            if (same) return true;
        }
        return false;
    }

    static (byte[], int)[] Get()
    {
        lock (Gate)
        {
            if (!_subscribed)
            {
                _subscribed = true;
                try { NetworkChange.NetworkAddressChanged += (_, _) => { lock (Gate) _subnets = null; }; } catch (Exception) { }
            }
            return _subnets ??= Enumerate();
        }
    }

    static (byte[], int)[] Enumerate()
    {
        var list = new List<(byte[], int)>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var ua in nic.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    int prefix = ua.PrefixLength;
                    if (prefix < 8 || prefix > 32) continue; // a /0../7 "subnet" would make everything on-link
                    list.Add((ua.Address.GetAddressBytes(), prefix));
                }
            }
        }
        catch (Exception) { }
        return list.ToArray();
    }
}

/// <summary>Already-yielded (id, address) pairs, capped at 256 with FIFO eviction.</summary>
sealed class SeenSet
{
    const int Max = 256;
    readonly HashSet<(string, IPAddress)> _set = new();
    readonly Queue<(string, IPAddress)> _order = new();

    public bool Add((string, IPAddress) key)
    {
        if (!_set.Add(key)) return false;
        _order.Enqueue(key);
        while (_order.Count > Max) _set.Remove(_order.Dequeue());
        return true;
    }
}
