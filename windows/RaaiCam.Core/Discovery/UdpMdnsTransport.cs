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
/// Real mDNS over UDP. One send/unicast-receive socket per IPv4 interface that is up and not loopback (bound to the
/// interface address, multicast egress pinned to it), plus one shared socket on 0.0.0.0:5353 (SO_REUSEADDR, not
/// exclusive: Windows' own mDNS and Apple Bonjour Service bind it too) that joins 224.0.0.251 on every interface.
/// Every setup step is best effort: a failing interface or a busy port degrades discovery, it never throws.
/// </summary>
public sealed class UdpMdnsTransport : IMdnsTransport
{
    static readonly IPAddress Group = IPAddress.Parse("224.0.0.251");
    static readonly IPEndPoint GroupEndPoint = new(Group, 5353);

    readonly List<Socket> _senders = new();
    readonly List<Socket> _all = new();
    readonly List<Task> _loops = new();
    readonly Channel<MdnsDatagram> _rx = Channel.CreateBounded<MdnsDatagram>(
        new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropWrite });
    readonly CancellationTokenSource _cts = new();
    readonly ILogSink? _log;
    bool _disposed;

    public UdpMdnsTransport(ILogSink? log = null)
    {
        _log = log;
        var addrs = new List<IPAddress>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback
                    || !nic.SupportsMulticast) continue;
                foreach (var ua in nic.GetIPProperties().UnicastAddresses)
                    if (ua.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ua.Address))
                        addrs.Add(ua.Address);
            }
        }
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
            catch (SocketException e) when (e.SocketErrorCode is SocketError.ConnectionReset or SocketError.MessageSize) { }
            catch (Exception e) { Log("mdns receive stopped: " + e.GetType().Name); return; }
        }
    }

    public async ValueTask SendAsync(byte[] packet, CancellationToken ct)
    {
        foreach (var s in _senders.ToArray())
        {
            try { await s.SendToAsync(packet, SocketFlags.None, GroupEndPoint, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception e) { Log("mdns send failed: " + e.GetType().Name); }
        }
    }

    public ValueTask<MdnsDatagram> ReceiveAsync(CancellationToken ct) => _rx.Reader.ReadAsync(ct);

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        foreach (var s in _all) { try { s.Dispose(); } catch (Exception) { } }
        try { await Task.WhenAll(_loops).ConfigureAwait(false); } catch (Exception) { }
        _rx.Writer.TryComplete();
        _cts.Dispose();
    }

    void Log(string line) { try { _log?.Write("discovery", line); } catch (Exception) { } }
}
