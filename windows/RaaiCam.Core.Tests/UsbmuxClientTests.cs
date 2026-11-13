using System.Buffers.Binary;
using System.IO.Pipelines;
using System.Net.Sockets;
using System.Text;
using RaaiCam.Core.Usbmux;

namespace RaaiCam.Core.Tests;

/// <summary>One end of an in-memory duplex connection.</summary>
sealed class DuplexStream(Stream read, Stream write) : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => write.Flush();
    public override int Read(byte[] buffer, int offset, int count) => read.Read(buffer, offset, count);
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => read.ReadAsync(buffer, ct);
    public override void Write(byte[] buffer, int offset, int count) => write.Write(buffer, offset, count);
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) => write.WriteAsync(buffer, ct);
    public override Task FlushAsync(CancellationToken ct) => write.FlushAsync(ct);
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    protected override void Dispose(bool disposing)
    {
        if (disposing) { read.Dispose(); write.Dispose(); }
        base.Dispose(disposing);
    }

    public static (Stream Client, Stream Server) CreatePair()
    {
        var toServer = new Pipe();
        var toClient = new Pipe();
        return (new DuplexStream(toClient.Reader.AsStream(), toServer.Writer.AsStream()),
                new DuplexStream(toServer.Reader.AsStream(), toClient.Writer.AsStream()));
    }
}

/// <summary>Scripted fake usbmuxd: each client connection runs the next script against the server end.</summary>
sealed class FakeDaemon
{
    readonly Queue<Func<Stream, Task>> _scripts;
    public FakeDaemon(params Func<Stream, Task>[] scripts) => _scripts = new(scripts);

    public Func<CancellationToken, Task<Stream>> Connect => ct =>
    {
        var (client, server) = DuplexStream.CreatePair();
        var script = _scripts.Dequeue();
        _ = Task.Run(async () =>
        {
            try { await script(server); }
            catch (Exception) { /* client went away */ }
            finally { server.Dispose(); }
        });
        return Task.FromResult(client);
    };

    public static async Task<(uint Tag, string Xml)> ReadRequest(Stream s)
    {
        var h = new byte[16];
        await s.ReadExactlyAsync(h);
        var len = (int)BinaryPrimitives.ReadUInt32LittleEndian(h);
        var body = new byte[len - 16];
        await s.ReadExactlyAsync(body);
        return (BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(12)), Encoding.UTF8.GetString(body));
    }

    public static byte[] Packet(uint tag, string innerXml)
    {
        var body = Encoding.UTF8.GetBytes(
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n" +
            "<plist version=\"1.0\">" + innerXml + "</plist>\n");
        var p = new byte[16 + body.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(p, (uint)p.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(8), 8);
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(12), tag);
        body.CopyTo(p, 16);
        return p;
    }

    public static byte[] Result(uint tag, int n) =>
        Packet(tag, $"<dict><key>MessageType</key><string>Result</string><key>Number</key><integer>{n}</integer></dict>");

    public static string DeviceEntry(int id, string type, string serial) =>
        $"<dict><key>DeviceID</key><integer>{id}</integer><key>MessageType</key><string>Attached</string>" +
        $"<key>Properties</key><dict><key>ConnectionType</key><string>{type}</string><key>DeviceID</key><integer>{id}</integer>" +
        "<key>LocationID</key><integer>0</integer><key>ProductID</key><integer>4776</integer>" +
        $"<key>SerialNumber</key><string>{serial}</string><key>USBSerialNumber</key><string>{serial}</string></dict></dict>";
}

public class UsbmuxClientTests
{
    const string Serial = "00000000-0000000000000000";
    const string NetSerial = "ffffffff-ffffffffffffffff";
    static CancellationToken Ct => new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token;

    [Fact]
    public async Task ListDevices_ParsesRealisticReply()
    {
        var fake = new FakeDaemon(async s =>
        {
            var (tag, xml) = await FakeDaemon.ReadRequest(s);
            Assert.Contains("<string>ListDevices</string>", xml);
            Assert.Contains("<string>raaicam</string>", xml);
            var list = "<dict><key>DeviceList</key><array>" + FakeDaemon.DeviceEntry(2, "USB", Serial) +
                       FakeDaemon.DeviceEntry(3, "Network", NetSerial) + "</array></dict>";
            await s.WriteAsync(FakeDaemon.Packet(tag, list));
        });
        var devices = await new UsbmuxClient(fake.Connect).ListDevicesAsync(Ct);
        Assert.Equal([new UsbDevice(2, Serial)], devices);
    }

    [Fact]
    public async Task ListDevices_MalformedXml_ThrowsProtocolException()
    {
        var fake = new FakeDaemon(async s =>
        {
            var (tag, _) = await FakeDaemon.ReadRequest(s);
            await s.WriteAsync(FakeDaemon.Packet(tag, "<dict><key>DeviceList</key><array>"));
        });
        await Assert.ThrowsAsync<UsbmuxProtocolException>(() => new UsbmuxClient(fake.Connect).ListDevicesAsync(Ct));
    }

    [Fact]
    public async Task ListDevices_MalformedDeviceList_ThrowsProtocolException()
    {
        var fake = new FakeDaemon(async s =>
        {
            var (tag, _) = await FakeDaemon.ReadRequest(s);
            await s.WriteAsync(FakeDaemon.Packet(tag, "<dict><key>DeviceList</key><string>nope</string></dict>"));
        });
        await Assert.ThrowsAsync<UsbmuxProtocolException>(() => new UsbmuxClient(fake.Connect).ListDevicesAsync(Ct));
    }

    [Fact]
    public async Task ListDevices_BadInteger_ThrowsProtocolException()
    {
        var fake = new FakeDaemon(async s =>
        {
            var (tag, _) = await FakeDaemon.ReadRequest(s);
            await s.WriteAsync(FakeDaemon.Packet(tag, "<dict><key>DeviceList</key><array><dict><key>DeviceID</key><integer>abc</integer></dict></array></dict>"));
        });
        await Assert.ThrowsAsync<UsbmuxProtocolException>(() => new UsbmuxClient(fake.Connect).ListDevicesAsync(Ct));
    }

    [Fact]
    public async Task Reply_WithWrongTag_ThrowsProtocolException()
    {
        var fake = new FakeDaemon(async s =>
        {
            var (tag, _) = await FakeDaemon.ReadRequest(s);
            await s.WriteAsync(FakeDaemon.Packet(tag + 99, "<dict><key>DeviceList</key><array/></dict>"));
        });
        await Assert.ThrowsAsync<UsbmuxProtocolException>(() => new UsbmuxClient(fake.Connect).ListDevicesAsync(Ct));
    }

    [Fact]
    public async Task Connect_SendsNetworkOrderPort_AndReturnsRawTunnel()
    {
        string? request = null;
        var fake = new FakeDaemon(async s =>
        {
            var (tag, xml) = await FakeDaemon.ReadRequest(s);
            request = xml;
            // Result immediately followed by tunnel bytes in the same write: the client must not over-read.
            await s.WriteAsync(FakeDaemon.Result(tag, 0).Concat(new byte[] { 1, 2, 3, 4 }).ToArray());
        });
        await using var tunnel = await new UsbmuxClient(fake.Connect).ConnectAsync(2, 47820, Ct);
        var buf = new byte[4];
        await tunnel.ReadExactlyAsync(buf);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, buf);
        Assert.Contains("<string>Connect</string>", request);
        Assert.Contains("<key>DeviceID</key><integer>2</integer>", request);
        Assert.Contains("<key>PortNumber</key><integer>52410</integer>", request);
    }

    [Fact]
    public async Task Connect_RefusedThrows()
    {
        var fake = new FakeDaemon(async s =>
        {
            var (tag, _) = await FakeDaemon.ReadRequest(s);
            await s.WriteAsync(FakeDaemon.Result(tag, 3));
        });
        var ex = await Assert.ThrowsAsync<UsbmuxException>(() => new UsbmuxClient(fake.Connect).ConnectAsync(2, 47820, Ct));
        Assert.Equal(UsbmuxResult.ConnectionRefused, ex.Result);
    }

    [Fact]
    public async Task Listen_ParsesAttachedAndDetached_IgnoringOthers()
    {
        var fake = new FakeDaemon(async s =>
        {
            var (tag, xml) = await FakeDaemon.ReadRequest(s);
            Assert.Contains("<string>Listen</string>", xml);
            await s.WriteAsync(FakeDaemon.Result(tag, 0));
            await s.WriteAsync(FakeDaemon.Packet(0, FakeDaemon.DeviceEntry(2, "USB", Serial)));
            await s.WriteAsync(FakeDaemon.Packet(0, FakeDaemon.DeviceEntry(5, "Network", NetSerial)));
            await s.WriteAsync(FakeDaemon.Packet(0, "<dict><key>MessageType</key><string>Paired</string><key>DeviceID</key><integer>2</integer></dict>"));
            await s.WriteAsync(FakeDaemon.Packet(0, "<dict><key>MessageType</key><string>Detached</string><key>DeviceID</key><integer>2</integer></dict>"));
        });
        var events = new List<UsbmuxEvent>();
        await foreach (var e in new UsbmuxClient(fake.Connect).ListenAsync(Ct)) events.Add(e);
        Assert.Equal(new UsbmuxEvent[] { new Attached(new UsbDevice(2, Serial)), new Detached(2) }, events);
    }

    [Theory]
    [InlineData(15u)]
    [InlineData(1_048_577u)]
    public async Task Packet_RejectsBadLength(uint length)
    {
        var fake = new FakeDaemon(async s =>
        {
            await FakeDaemon.ReadRequest(s);
            var h = new byte[16];
            BinaryPrimitives.WriteUInt32LittleEndian(h, length);
            await s.WriteAsync(h);
        });
        await Assert.ThrowsAsync<UsbmuxProtocolException>(() => new UsbmuxClient(fake.Connect).ListDevicesAsync(Ct));
    }

    [Fact]
    public async Task Daemon_UnavailableThrowsTypedException()
    {
        var client = new UsbmuxClient(_ => throw new SocketException((int)SocketError.ConnectionRefused));
        await Assert.ThrowsAsync<UsbmuxUnavailableException>(() => client.ListDevicesAsync(Ct));
        await Assert.ThrowsAsync<UsbmuxUnavailableException>(() => client.ConnectAsync(1, 47820, Ct));
    }

    [Fact]
    public void Plist_RoundTripsAndRejectsGarbage()
    {
        var bytes = Plist.Write(new Dictionary<string, object?>
        {
            ["s"] = "a<b", ["i"] = 5, ["b"] = true, ["d"] = new byte[] { 1, 2 }, ["r"] = 1.5, ["a"] = new object[] { "x", 2L },
        });
        var d = Assert.IsType<Dictionary<string, object?>>(Plist.Parse(bytes));
        Assert.Equal("a<b", d["s"]);
        Assert.Equal(5L, d["i"]);
        Assert.Equal(true, d["b"]);
        Assert.Equal(new byte[] { 1, 2 }, d["d"]);
        Assert.Equal(1.5, d["r"]);
        Assert.Equal(2, Assert.IsType<List<object?>>(d["a"]).Count);
        Assert.Throws<UsbmuxProtocolException>(() => Plist.Parse(Encoding.UTF8.GetBytes("<plist><dict><key>a</key><data>!!!</data></dict></plist>")));
        Assert.Throws<UsbmuxProtocolException>(() => Plist.Parse(Encoding.UTF8.GetBytes("<plist><dict><key>a</key></dict></plist>")));
        Assert.Throws<UsbmuxProtocolException>(() => Plist.Parse(Encoding.UTF8.GetBytes("not xml")));
    }
}
