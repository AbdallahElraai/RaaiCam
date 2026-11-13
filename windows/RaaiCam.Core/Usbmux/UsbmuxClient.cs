using System.Buffers.Binary;
using System.Net.Sockets;
using System.Runtime.CompilerServices;

namespace RaaiCam.Core.Usbmux;

public record UsbDevice(int DeviceId, string SerialNumber);

public abstract record UsbmuxEvent;
public sealed record Attached(UsbDevice Device) : UsbmuxEvent;
public sealed record Detached(int DeviceId) : UsbmuxEvent;

public enum UsbmuxResult { Ok = 0, BadCommand = 1, BadDevice = 2, ConnectionRefused = 3, BadVersion = 6 }

/// <summary>The daemon answered a request with a non-zero Result.</summary>
public sealed class UsbmuxException(UsbmuxResult result) : Exception($"usbmuxd returned {result} ({(int)result})")
{
    public UsbmuxResult Result { get; } = result;
}

/// <summary>usbmuxd (Apple Devices / Apple Mobile Device Service) cannot be reached.</summary>
public sealed class UsbmuxUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Client for usbmuxd's plist protocol (16-byte LE header + XML plist body).</summary>
public sealed class UsbmuxClient
{
    const int HeaderSize = 16;
    const int MaxPacket = 1_048_576;
    const string ProgName = "raaicam";
    static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(2);
    static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(5);

    readonly Func<CancellationToken, Task<Stream>> _connectDaemon;
    int _tag;

    public UsbmuxClient(Func<CancellationToken, Task<Stream>>? connectDaemon = null)
        => _connectDaemon = connectDaemon ?? ConnectTcpAsync;

    static async Task<Stream> ConnectTcpAsync(CancellationToken ct)
    {
        var tcp = new TcpClient { NoDelay = true };
        try
        {
            await tcp.ConnectAsync("127.0.0.1", 27015, ct);
            return tcp.GetStream();
        }
        catch { tcp.Dispose(); throw; }
    }

    public async Task<IReadOnlyList<UsbDevice>> ListDevicesAsync(CancellationToken ct)
    {
        await using var s = await OpenAsync(ct);
        var reply = await RequestAsync(s, "ListDevices", null, ct);
        var devices = new List<UsbDevice>();
        if (reply.TryGetValue("DeviceList", out var dl))
        {
            if (dl is not List<object?> list) throw new UsbmuxProtocolException("DeviceList is not an array.");
            foreach (var item in list)
                if (ParseDevice(item) is { } d) devices.Add(d);
        }
        return devices;
    }

    /// <summary>Opens a tunnel to a TCP port on the device. The returned stream is raw, positioned right after the Result packet.</summary>
    public async Task<Stream> ConnectAsync(int deviceId, ushort port, CancellationToken ct)
    {
        var s = await OpenAsync(ct);
        try
        {
            var reply = await RequestAsync(s, "Connect", new()
            {
                ["DeviceID"] = deviceId,
                ["PortNumber"] = (int)BinaryPrimitives.ReverseEndianness(port),
            }, ct);
            var result = ParseResult(reply);
            if (result != UsbmuxResult.Ok) throw new UsbmuxException(result);
            return s;
        }
        catch { await s.DisposeAsync(); throw; }
    }

    public async IAsyncEnumerable<UsbmuxEvent> ListenAsync([EnumeratorCancellation] CancellationToken ct)
    {
        await using var s = await OpenAsync(ct);
        var reply = await RequestAsync(s, "Listen", null, ct);
        var result = ParseResult(reply);
        if (result != UsbmuxResult.Ok) throw new UsbmuxException(result);

        while (true)
        {
            Dictionary<string, object?> msg;
            try { msg = (await ReadPacketAsync(s, ct)).Plist; }
            catch (EndOfStreamException) { yield break; } // daemon closed the listen socket
            if (msg.GetValueOrDefault("MessageType") is not string type) continue;
            switch (type)
            {
                case "Attached":
                    if (ParseDevice(msg) is { } d) yield return new Attached(d);
                    break;
                case "Detached":
                    yield return new Detached(RequireInt(msg, "DeviceID"));
                    break;
                // Paired and anything else: ignore.
            }
        }
    }

    async Task<Stream> OpenAsync(CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(ConnectTimeout);
        try { return await _connectDaemon(cts.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new UsbmuxUnavailableException("Timed out connecting to usbmuxd.");
        }
        catch (Exception e) when (e is SocketException or IOException)
        {
            throw new UsbmuxUnavailableException("Cannot reach usbmuxd. Install the Apple Devices app and make sure it is running.", e);
        }
    }

    async Task<Dictionary<string, object?>> RequestAsync(Stream s, string messageType, Dictionary<string, object?>? extra, CancellationToken ct)
    {
        var request = new Dictionary<string, object?>
        {
            ["MessageType"] = messageType,
            ["ClientVersionString"] = ProgName,
            ["ProgName"] = ProgName,
        };
        if (extra != null) foreach (var kv in extra) request[kv.Key] = kv.Value;

        var tag = unchecked((uint)Interlocked.Increment(ref _tag));
        var body = Plist.Write(request);
        var pkt = new byte[HeaderSize + body.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(pkt, (uint)pkt.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(pkt.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(pkt.AsSpan(8), 8);
        BinaryPrimitives.WriteUInt32LittleEndian(pkt.AsSpan(12), tag);
        body.CopyTo(pkt, HeaderSize);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(ReplyTimeout);
        try
        {
            await s.WriteAsync(pkt, cts.Token);
            await s.FlushAsync(cts.Token);
            var (replyTag, plist) = await ReadPacketAsync(s, cts.Token);
            if (replyTag != tag) throw new UsbmuxProtocolException($"Reply tag {replyTag} does not match request tag {tag}.");
            return plist;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new UsbmuxProtocolException("Timed out waiting for usbmuxd reply.");
        }
        catch (EndOfStreamException e) { throw new UsbmuxProtocolException("usbmuxd closed the connection.", e); }
        catch (IOException e) { throw new UsbmuxUnavailableException("Lost connection to usbmuxd.", e); }
    }

    /// <summary>Exact-length reads only, so a raw tunnel following the Result packet is never over-read.</summary>
    static async Task<(uint Tag, Dictionary<string, object?> Plist)> ReadPacketAsync(Stream s, CancellationToken ct)
    {
        var header = new byte[HeaderSize];
        await s.ReadExactlyAsync(header, ct);
        var total = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (total < HeaderSize || total > MaxPacket) throw new UsbmuxProtocolException($"Bad usbmux packet length {total}.");
        var tag = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12));
        var body = new byte[total - HeaderSize];
        await s.ReadExactlyAsync(body, ct);
        if (Plist.Parse(body) is not Dictionary<string, object?> dict)
            throw new UsbmuxProtocolException("usbmuxd reply is not a dictionary.");
        return (tag, dict);
    }

    static UsbmuxResult ParseResult(Dictionary<string, object?> reply)
    {
        if (reply.GetValueOrDefault("MessageType") is string t && t != "Result")
            throw new UsbmuxProtocolException($"Expected Result, got {t}.");
        return (UsbmuxResult)RequireInt(reply, "Number");
    }

    /// <summary>Returns the device if it is a USB device, null for network/other connection types.</summary>
    static UsbDevice? ParseDevice(object? entry)
    {
        if (entry is not Dictionary<string, object?> d) throw new UsbmuxProtocolException("Device entry is not a dictionary.");
        var props = d.GetValueOrDefault("Properties") as Dictionary<string, object?>
            ?? throw new UsbmuxProtocolException("Device entry has no Properties.");
        if (props.GetValueOrDefault("ConnectionType") as string != "USB") return null;
        var id = props.ContainsKey("DeviceID") ? RequireInt(props, "DeviceID") : RequireInt(d, "DeviceID");
        var serial = props.GetValueOrDefault("SerialNumber") as string
            ?? props.GetValueOrDefault("USBSerialNumber") as string ?? "";
        return new UsbDevice(id, serial);
    }

    static int RequireInt(Dictionary<string, object?> d, string key)
    {
        if (d.GetValueOrDefault(key) is long l && l is >= int.MinValue and <= int.MaxValue) return (int)l;
        throw new UsbmuxProtocolException($"Missing or invalid integer '{key}'.");
    }
}
