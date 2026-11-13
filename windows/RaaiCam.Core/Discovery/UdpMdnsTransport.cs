using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading.Channels;
using RaaiCam.Core.Phone;

namespace RaaiCam.Core.Discovery;

public sealed record MdnsDatagram(byte[] Data, IPAddress? From);

/// <summary>Sends mDNS queries and receives whatever datagrams come back. Faked in tests.</summary>
public interface IMdnsTransport : IAsyncDisposable
{
    ValueTask SendAsync(byte[] packet, CancellationToken ct);
    /// <summary>Waits for the next datagram from any interface; throws <see cref="OperationCanceledException"/> on cancel.</summary>
    ValueTask<MdnsDatagram> ReceiveAsync(CancellationToken ct);
}

/// <summary>
/// Real mDNS over UDP. One send/receive socket per IPv4 interface that is up and not loopback (bound to the
/// interface address on an ephemeral port, multicast egress pinned to it), plus one shared socket on 0.0.0.0:5353
/// (SO_REUSEADDR, not exclusive: Windows' own mDNS and Apple Bonjour Service bind it too) that joins 224.0.0.251 on
/// every interface. Because queries leave from an ephemeral port, responders answer them as legacy unicast queries
/// straight to that port; the 5353 listener additionally sees multicast announcements. Sockets are recreated when the
/// machine's network addresses change. Every setup step is best effort: a failing interface or a busy port degrades
/// discovery, it never throws.
/// </summary>
public sealed class UdpMdnsTransport : IMdnsTransport
{
    static readonly IPAddress Group = IPAddress.Parse("224.0.0.251");
    static readonly IPEndPoint GroupEndPoint = new(Group, 5353);

    readonly object _gate = new();
    readonly List<Socket> _senders = new();
    readonly List<Socket> _all = new();
    readonly List<Task> _loops = new();
    readonly Channel<MdnsDatagram> _rx = Channel.CreateBounded<MdnsDatagram>(
        new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropWrite });
    readonly CancellationTokenSource _cts = new();
    readonly ILogSink? _log;
    readonly NetworkAddressChangedEventHandler _onNetworkChange;
    int _disposed;

    public UdpMdnsTransport(ILogSink? log = null)
    {
        _log = log;
        lock (_gate) Setup();
        _onNetworkChange = (_, _) => Rebuild();
        try { NetworkChange.NetworkAddressChanged += _onNetworkChange; }
        catch (Exception e) { Log("network change watch failed: " + e.GetType().Name); }
    }

    /// <summary>Local IPv4 addresses of interfaces that are up and not loopback. 169.254/16 only when nothing else exists.</summary>
    static List<IPAddress> LocalAddresses()
    {
        var addrs = new List<IPAddress>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback
                || !nic.SupportsMulticast) continue;
            foreach (var ua in nic.GetIPProperties().UnicastAddresses)
                if (ua.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ua.Address))
                    addrs.Add(ua.Address);
        }
        bool LinkLocal(IPAddress a) { var b = a.GetAddressBytes(); return b[0] == 169 && b[1] == 254; }
        var routable = addrs.Where(a => !LinkLocal(a)).ToList();
        return routable.Count > 0 ? routable : addrs;
    }

    void Setup()
    {
        var addrs = new List<IPAddress>();
        try { addrs = LocalAddresses(); }
        catch (Exception e) { Log("interface enumeration failed: " + e.GetType().Name); }

        foreach (var a in addrs)
        {
            try
            {
                var s = NewSocket();
                s.Bind(new IPEndPoint(a, 0));
                s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, a.GetAddressBytes());
                s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
                _senders.Add(s);
                Start(s);
            }
            catch (Exception e) { Log($"mdns sender on {a} failed: {e.GetType().Name}"); }
        }

        try
        {
            var m = NewSocket();
            m.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            m.Bind(new IPEndPoint(IPAddress.Any, 5353));
            foreach (var a in addrs)
            {
                try { m.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(Group, a)); }
                catch (Exception e) { Log($"mdns join on {a} failed: {e.GetType().Name}"); }
            }
            Start(m);
        }
        catch (Exception e) { Log("mdns listener on 5353 failed: " + e.GetType().Name); }
    }

    void Rebuild()
    {
        try
        {
            lock (_gate)
            {
                if (Volatile.Read(ref _disposed) != 0) return;
                foreach (var s in _all) { try { s.Dispose(); } catch (Exception) { } }
                _all.Clear();
                _senders.Clear();
                _loops.RemoveAll(t => t.IsCompleted);
                Setup();
            }
            Log("network changed; mdns sockets recreated");
        }
        catch (Exception e) { Log("mdns rebuild failed: " + e.GetType().Name); }
    }

    Socket NewSocket()
    {
        var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        _all.Add(s);
        s.ExclusiveAddressUse = false;
        try { s.IOControl(unchecked((int)0x9800000C), new byte[] { 0 }, null); } catch (Exception) { } // SIO_UDP_CONNRESET off
        return s;
    }

    void Start(Socket s) => _loops.Add(Task.Run(() => ReceiveLoop(s)));

    async Task ReceiveLoop(Socket s)
    {
        var buf = new byte[9000];
        var any = new IPEndPoint(IPAddress.Any, 0);
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var r = await s.ReceiveFromAsync(buf, SocketFlags.None, any, _cts.Token).ConfigureAwait(false);
                _rx.Writer.TryWrite(new MdnsDatagram(buf.AsSpan(0, r.ReceivedBytes).ToArray(), (r.RemoteEndPoint as IPEndPoint)?.Address));
            }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException e) when (e.SocketErrorCode is SocketError.OperationAborted or SocketError.Interrupted
                                                or SocketError.NotSocket or SocketError.Shutdown) { return; } // socket closed under us
            catch (SocketException e) when (e.SocketErrorCode is SocketError.ConnectionReset or SocketError.MessageSize) { }
            catch (Exception e)
            {
                Log("mdns receive failed, retrying in 1 s: " + e.GetType().Name);
                try { await Task.Delay(1000, _cts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    public async ValueTask SendAsync(byte[] packet, CancellationToken ct)
    {
        Socket[] senders;
        lock (_gate) senders = _senders.ToArray();
        foreach (var s in senders)
        {
            try { await s.SendToAsync(packet, SocketFlags.None, GroupEndPoint, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception e) { Log("mdns send failed: " + e.GetType().Name); }
        }
    }

    public ValueTask<MdnsDatagram> ReceiveAsync(CancellationToken ct) => _rx.Reader.ReadAsync(ct);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { NetworkChange.NetworkAddressChanged -= _onNetworkChange; } catch (Exception) { }
        _cts.Cancel();
        Task[] loops;
        lock (_gate)
        {
            foreach (var s in _all) { try { s.Dispose(); } catch (Exception) { } }
            loops = _loops.ToArray();
        }
        try { await Task.WhenAll(loops).ConfigureAwait(false); } catch (Exception) { }
        _rx.Writer.TryComplete();
        _cts.Dispose();
    }

    void Log(string line) { try { _log?.Write("discovery", line); } catch (Exception) { } }
}
