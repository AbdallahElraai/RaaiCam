using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
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
/// the caller's job.
/// </summary>
public sealed class WifiDiscovery : IAsyncDisposable
{
    public const int DefaultPort = 47820;
    const string ServiceName = "_raaicam._tcp.local";
    const string InstanceSuffix = "._raaicam._tcp.local";
    const int MaxInstances = 32, MaxHosts = 32, MaxAddressesPerHost = 8, MaxFollowUps = 8;
    static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    readonly IMdnsTransport _transport;
    readonly SettingsStore _settings;
    readonly string? _expectedId;
    readonly TimeProvider _time;
    readonly ILogSink? _log;
    int _disposed;

    /// <param name="expectedId">Only yield phones advertising this id; null yields every phone with a valid id.</param>
    public WifiDiscovery(IMdnsTransport transport, SettingsStore settings, string? expectedId = null,
                         TimeProvider? time = null, ILogSink? log = null)
    {
        _transport = transport;
        _settings = settings;
        _expectedId = expectedId?.ToLowerInvariant();
        _time = time ?? TimeProvider.System;
        _log = log;
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

    public async IAsyncEnumerable<DiscoveredPhone> BrowseAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        var seen = new HashSet<(string, IPAddress)>();

        if (_settings.LastPhoneIp is { } last && IsUsable(last) && seen.Add((_expectedId ?? "", last)))
            yield return new DiscoveredPhone(_expectedId ?? "", last, DefaultPort, DiscoverySource.LastKnown);

        var state = new BrowseState();
        var channel = Channel.CreateUnbounded<DiscoveredPhone>();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var tasks = new[]
        {
            Task.Run(() => QueryLoop(state, cts.Token)),
            Task.Run(() => ReceiveLoop(state, seen, channel.Writer, cts.Token)),
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

    async Task QueryLoop(BrowseState state, CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(1);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try { await _transport.SendAsync(MdnsPacket.BuildQuery(state.Questions()), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; }
                catch (Exception e) { Log("query send failed: " + e.GetType().Name); }
                await Task.Delay(delay, _time, ct).ConfigureAwait(false);
                delay = delay * 2 > MaxBackoff ? MaxBackoff : delay * 2;
            }
        }
        catch (OperationCanceledException) { }
    }

    async Task ReceiveLoop(BrowseState state, HashSet<(string, IPAddress)> seen, ChannelWriter<DiscoveredPhone> output, CancellationToken ct)
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
                    foreach (var p in state.Resolved(_expectedId))
                        if (seen.Add((p.IdHash, p.Address))) output.TryWrite(p);
                }
                catch (Exception e) { Log("ignored bad packet: " + e.GetType().Name); }
            }
        }
        catch (OperationCanceledException) { }
    }

    static bool IsUsable(IPAddress a)
    {
        if (a.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = a.GetAddressBytes();
        return !(b[0] == 0 || b[0] == 127 || b[0] >= 224); // not any/loopback/multicast/broadcast
    }

    static bool IsIdHex(string s)
    {
        if (s.Length != 8) return false;
        foreach (var c in s)
            if (!(c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F')) return false;
        return true;
    }

    void Log(string line) { try { _log?.Write("discovery", line); } catch (Exception) { } }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _transport.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>Accumulates PTR/SRV/TXT/A records across packets. Bounded: a flood of records cannot grow it without limit.</summary>
    sealed class BrowseState
    {
        sealed class Instance
        {
            public string? Host;
            public int Port;
            public string? Id;
        }

        readonly object _gate = new();
        readonly Dictionary<string, Instance> _instances = new(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, List<IPAddress>> _hosts = new(StringComparer.OrdinalIgnoreCase);

        public void Apply(IReadOnlyList<MdnsRecord> records)
        {
            lock (_gate)
            {
                foreach (var r in records)
                {
                    switch (r.Type)
                    {
                        case MdnsRecordType.Ptr when r.Name.Equals(ServiceName, StringComparison.OrdinalIgnoreCase)
                                                   && r.Target is { } t && t.EndsWith(InstanceSuffix, StringComparison.OrdinalIgnoreCase):
                            Inst(t);
                            break;
                        case MdnsRecordType.Srv when r.Name.EndsWith(InstanceSuffix, StringComparison.OrdinalIgnoreCase)
                                                   && r.Port is > 0 and <= 65535 && !string.IsNullOrEmpty(r.Target):
                            if (Inst(r.Name) is { } s) { s.Host = r.Target; s.Port = r.Port; }
                            break;
                        case MdnsRecordType.Txt when r.Name.EndsWith(InstanceSuffix, StringComparison.OrdinalIgnoreCase) && r.Txt is { } txt:
                            if (Inst(r.Name) is { } x)
                                x.Id = txt.TryGetValue("v", out var v) && v == "2" && txt.TryGetValue("id", out var id) && IsIdHex(id)
                                    ? id.ToLowerInvariant() : null;
                            break;
                        case MdnsRecordType.A when r.Address is { } a && IsUsable(a):
                            if (!_hosts.TryGetValue(r.Name, out var list))
                            {
                                if (_hosts.Count >= MaxHosts) break;
                                _hosts[r.Name] = list = new List<IPAddress>();
                            }
                            if (list.Count < MaxAddressesPerHost && !list.Contains(a)) list.Add(a);
                            break;
                    }
                }
            }
        }

        Instance? Inst(string name)
        {
            if (_instances.TryGetValue(name, out var i)) return i;
            if (_instances.Count >= MaxInstances) return null;
            return _instances[name] = new Instance();
        }

        public IEnumerable<DiscoveredPhone> Resolved(string? expectedId)
        {
            var found = new List<DiscoveredPhone>();
            lock (_gate)
            {
                foreach (var i in _instances.Values)
                {
                    if (i.Id is null || i.Host is null || i.Port == 0) continue;
                    if (expectedId is not null && i.Id != expectedId) continue;
                    if (!_hosts.TryGetValue(i.Host, out var addrs)) continue;
                    foreach (var a in addrs) found.Add(new DiscoveredPhone(i.Id, a, i.Port));
                }
            }
            return found;
        }

        /// <summary>The service PTR question plus follow-ups for instances/hosts we have only partly resolved.</summary>
        public List<MdnsQuestion> Questions()
        {
            var qs = new List<MdnsQuestion> { new(ServiceName, MdnsRecordType.Ptr) };
            lock (_gate)
            {
                foreach (var (name, i) in _instances)
                {
                    if (qs.Count > MaxFollowUps) break;
                    if (i.Host is null || i.Id is null) qs.Add(new MdnsQuestion(name, MdnsRecordType.Any));
                    else if (!_hosts.ContainsKey(i.Host)) qs.Add(new MdnsQuestion(i.Host, MdnsRecordType.A));
                }
            }
            return qs;
        }
    }
}
