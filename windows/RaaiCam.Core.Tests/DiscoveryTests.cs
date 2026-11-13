using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using RaaiCam.Core.Discovery;

namespace RaaiCam.Core.Tests;

/// <summary>Hand-builds DNS response packets; names are compressed against earlier names when asked.</summary>
sealed class DnsBuilder
{
    readonly List<byte> _b = new();
    readonly Dictionary<string, int> _names = new(StringComparer.OrdinalIgnoreCase);
    int _count;
    readonly bool _compress;

    public DnsBuilder(bool compress = true, ushort flags = 0x8400)
    {
        _compress = compress;
        U16(0); U16(flags); U16(0); U16(0); U16(0); U16(0);
    }

    void U16(int v) { _b.Add((byte)(v >> 8)); _b.Add((byte)v); }

    public DnsBuilder Name(string name)
    {
        var labels = name.Split('.', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < labels.Length; i++)
        {
            var suffix = string.Join('.', labels[i..]);
            if (_compress && _names.TryGetValue(suffix, out var off))
            {
                U16(0xC000 | off);
                return this;
            }
            if (_b.Count < 0x3FFF) _names[suffix] = _b.Count;
            var l = Encoding.ASCII.GetBytes(labels[i]);
            _b.Add((byte)l.Length); _b.AddRange(l);
        }
        _b.Add(0);
        return this;
    }

    DnsBuilder Rec(string name, int type, Action rdata)
    {
        Name(name); U16(type); U16(0x8001); _b.AddRange(new byte[] { 0, 0, 0x0E, 0x10 });
        var lenAt = _b.Count; U16(0);
        var start = _b.Count; rdata();
        var len = _b.Count - start; _b[lenAt] = (byte)(len >> 8); _b[lenAt + 1] = (byte)len;
        _count++;
        return this;
    }

    public DnsBuilder Ptr(string name, string target) => Rec(name, 12, () => Name(target));
    public DnsBuilder Srv(string name, int port, string target) => Rec(name, 33, () => { U16(0); U16(0); U16(port); Name(target); });
    public DnsBuilder A(string name, string ip) => Rec(name, 1, () => _b.AddRange(IPAddress.Parse(ip).GetAddressBytes()));
    public DnsBuilder Txt(string name, params string[] kv) => Rec(name, 16, () =>
    {
        foreach (var s in kv) { var x = Encoding.ASCII.GetBytes(s); _b.Add((byte)x.Length); _b.AddRange(x); }
    });

    public byte[] Build()
    {
        var a = _b.ToArray();
        a[6] = (byte)(_count >> 8); a[7] = (byte)_count;
        return a;
    }
}

sealed class FakeMdnsTransport : IMdnsTransport
{
    readonly Channel<MdnsDatagram> _in = Channel.CreateUnbounded<MdnsDatagram>();
    public List<byte[]> Sent { get; } = new();
    public SemaphoreSlim SentSignal { get; } = new(0);
    public bool Disposed { get; private set; }

    public void Deliver(byte[] packet, string from = "192.168.1.50") =>
        _in.Writer.TryWrite(new MdnsDatagram(packet, IPAddress.Parse(from)));

    public ValueTask SendAsync(byte[] packet, CancellationToken ct)
    {
        lock (Sent) Sent.Add(packet);
        SentSignal.Release();
        return ValueTask.CompletedTask;
    }

    public ValueTask<MdnsDatagram> ReceiveAsync(CancellationToken ct) => _in.Reader.ReadAsync(ct);

    public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
}

public class DiscoveryTests
{
    const string Svc = "_raaicam._tcp.local";
    const string Inst = "RaaiCam-a1b2c3d4._raaicam._tcp.local";

    static byte[] FullAnswer(string id = "a1b2c3d4", string ip = "192.168.1.50", int port = 47820, bool compress = true, string host = "phone.local") =>
        new DnsBuilder(compress)
            .Ptr(Svc, Inst)
            .Srv(Inst, port, host)
            .Txt(Inst, "v=2", "id=" + id)
            .A(host, ip)
            .Build();

    static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "raaicam-disc-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        return d;
    }

    static async Task<List<DiscoveredPhone>> Take(IAsyncEnumerable<DiscoveredPhone> src, int n, int timeoutMs = 10_000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        var list = new List<DiscoveredPhone>();
        await foreach (var p in src.WithCancellation(cts.Token))
        {
            list.Add(p);
            if (list.Count == n) break;
        }
        return list;
    }

    // ---- parser ----

    [Fact]
    public void Discovery_ParsesServiceAnswer()
    {
        foreach (var compress in new[] { true, false })
        {
            var recs = MdnsPacket.Parse(FullAnswer(compress: compress));
            Assert.Equal(4, recs.Count);
            Assert.Contains(recs, r => r.Type == MdnsRecordType.Ptr && r.Name == Svc && r.Target == Inst);
            Assert.Contains(recs, r => r.Type == MdnsRecordType.Srv && r.Name == Inst && r.Port == 47820 && r.Target == "phone.local");
            Assert.Contains(recs, r => r.Type == MdnsRecordType.Txt && r.Txt!["id"] == "a1b2c3d4" && r.Txt["v"] == "2");
            Assert.Contains(recs, r => r.Type == MdnsRecordType.A && r.Name == "phone.local" && r.Address!.ToString() == "192.168.1.50");
        }
    }

    [Fact]
    public void Discovery_ParserRejectsPointerLoop()
    {
        // Header (1 answer) then a name that points at itself (offset 12).
        var p = new byte[] { 0, 0, 0x84, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0xC0, 12, 0, 1, 0, 1, 0, 0, 0, 0, 0, 0 };
        Assert.Empty(MdnsPacket.Parse(p));

        // Two pointers pointing at each other.
        var q = new byte[] { 0, 0, 0x84, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0xC0, 14, 0xC0, 12, 0, 1, 0, 1, 0, 0, 0, 0, 0, 0 };
        Assert.Empty(MdnsPacket.Parse(q));
    }

    [Fact]
    public void Discovery_ParserIgnoresTruncatedAndGarbage()
    {
        var full = FullAnswer();
        for (int len = 0; len < full.Length; len++)
            _ = MdnsPacket.Parse(full.AsSpan(0, len)); // must never throw
        var rnd = new Random(7);
        for (int i = 0; i < 2000; i++)
        {
            var buf = new byte[rnd.Next(0, 200)];
            rnd.NextBytes(buf);
            if (buf.Length > 7) { buf[2] |= 0x80; }
            _ = MdnsPacket.Parse(buf);
        }
        // a record whose rdlength runs past the end yields nothing from that record
        var cut = full[..^2];
        Assert.DoesNotContain(MdnsPacket.Parse(cut), r => r.Type == MdnsRecordType.A);
    }

    [Fact]
    public void Discovery_ParserCapsRecordsAndIgnoresQueries()
    {
        var b = new DnsBuilder();
        for (int i = 0; i < 120; i++) b.A("h" + i + ".local", "10.0.0.1");
        Assert.Equal(50, MdnsPacket.Parse(b.Build()).Count);

        var query = new DnsBuilder(flags: 0x0000).A("x.local", "10.0.0.1").Build();
        Assert.Empty(MdnsPacket.Parse(query));
    }

    [Fact]
    public void Discovery_ParserRejectsOverlongName()
    {
        var b = new DnsBuilder(compress: false);
        var label = new string('a', 60);
        b.Ptr(Svc, string.Join('.', Enumerable.Repeat(label, 6))); // 6*61 > 255
        Assert.DoesNotContain(MdnsPacket.Parse(b.Build()), r => r.Type == MdnsRecordType.Ptr);
    }

    [Fact]
    public void Discovery_BuildQuery_SetsQuBitAndName()
    {
        var q = MdnsPacket.BuildQuery(new[] { new MdnsQuestion(Svc, MdnsRecordType.Ptr) });
        Assert.Equal(1, q[5]);                       // QDCOUNT
        Assert.Equal(0, q[2] & 0x80);                // QR = query
        Assert.Equal(0x80, q[^2] & 0x80);            // QU bit in the class
        Assert.Equal(1, q[^1]);                      // class IN
        Assert.Equal(12, q[^3]);                     // type PTR
        Assert.Equal((byte)8, q[12]);                // "_raaicam" label length
    }

    // ---- id hash ----

    [Fact]
    public void Discovery_IdHashMatchesIosScheme()
    {
        var key = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        var expected = Convert.ToHexString(SHA256.HashData(key)[..4]).ToLowerInvariant();
        Assert.Equal(expected, WifiDiscovery.IdHashFor(key));
        Assert.Equal(8, WifiDiscovery.IdHashFor(key).Length);
    }

    // ---- settings ----

    [Fact]
    public void Settings_RoundTripsLastPhoneIpAndKeepsOtherKeys()
    {
        var dir = TempDir();
        try
        {
            var path = Path.Combine(dir, "sub", "settings.json");
            var s = new SettingsStore(path);
            Assert.Null(s.LastPhoneIp);
            s.SetLastPhoneIp(IPAddress.Parse("192.168.1.77"));
            Assert.Equal(IPAddress.Parse("192.168.1.77"), new SettingsStore(path).LastPhoneIp);
            Assert.Contains("\"lastPhoneIp\"", File.ReadAllText(path));

            File.WriteAllText(path, "{\"other\":5,\"lastPhoneIp\":\"10.0.0.2\"}");
            var s2 = new SettingsStore(path);
            s2.SetLastPhoneIp(IPAddress.Parse("10.0.0.3"));
            var text = File.ReadAllText(path);
            Assert.Contains("\"other\"", text);
            Assert.Equal(IPAddress.Parse("10.0.0.3"), new SettingsStore(path).LastPhoneIp);
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));

            File.WriteAllText(path, "{ not json");
            Assert.Null(new SettingsStore(path).LastPhoneIp);
            File.WriteAllText(path, "{\"lastPhoneIp\":\"nonsense\"}");
            Assert.Null(new SettingsStore(path).LastPhoneIp);
        }
        finally { Directory.Delete(dir, true); }
    }

    // ---- browse ----

    [Fact]
    public async Task Discovery_YieldsPhoneFromAnswer_AndDedupes()
    {
        var t = new FakeMdnsTransport();
        var dir = TempDir();
        try
        {
            await using var d = new WifiDiscovery(t, new SettingsStore(Path.Combine(dir, "s.json")), null, new FakeTimeProvider());
            t.Deliver(FullAnswer());
            t.Deliver(FullAnswer()); // duplicate
            t.Deliver(FullAnswer(id: "00000000", ip: "192.168.1.51", host: "other.local"));
            var got = await Take(d.BrowseAsync(), 2);
            Assert.Equal(new DiscoveredPhone("a1b2c3d4", IPAddress.Parse("192.168.1.50"), 47820), got[0]);
            Assert.Equal("00000000", got[1].IdHash);
            Assert.All(got, p => Assert.Equal(DiscoverySource.Mdns, p.Source));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Discovery_FiltersByIdHash()
    {
        var t = new FakeMdnsTransport();
        var dir = TempDir();
        try
        {
            await using var d = new WifiDiscovery(t, new SettingsStore(Path.Combine(dir, "s.json")), "a1b2c3d4", new FakeTimeProvider());
            t.Deliver(FullAnswer(id: "deadbeef", ip: "192.168.1.60", host: "h60.local"));      // wrong id
            t.Deliver(FullAnswer(id: "A1B2C3D4", ip: "192.168.1.61", host: "h61.local"));      // right id (case-insensitive)
            var got = await Take(d.BrowseAsync(), 1);
            Assert.Equal("192.168.1.61", got[0].Address.ToString());
            Assert.Equal("a1b2c3d4", got[0].IdHash);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Discovery_RejectsMalformedIdAndWrongVersion()
    {
        var t = new FakeMdnsTransport();
        var dir = TempDir();
        try
        {
            await using var d = new WifiDiscovery(t, new SettingsStore(Path.Combine(dir, "s.json")), null, new FakeTimeProvider());
            t.Deliver(FullAnswer(id: "zzzzzzzz", host: "bad1.local"));
            t.Deliver(new DnsBuilder().Ptr(Svc, Inst).Srv(Inst, 47820, "bad2.local")
                .Txt(Inst, "v=1", "id=a1b2c3d4").A("bad2.local", "192.168.1.70").Build());
            t.Deliver(new byte[] { 1, 2, 3 });
            t.Deliver(FullAnswer(ip: "192.168.1.71", host: "good.local"));
            var got = await Take(d.BrowseAsync(), 1);
            Assert.Equal("192.168.1.71", got[0].Address.ToString());
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Discovery_AssemblesAnswerAcrossPackets()
    {
        var t = new FakeMdnsTransport();
        var dir = TempDir();
        try
        {
            await using var d = new WifiDiscovery(t, new SettingsStore(Path.Combine(dir, "s.json")), "a1b2c3d4", new FakeTimeProvider());
            t.Deliver(new DnsBuilder().Ptr(Svc, Inst).Build());
            t.Deliver(new DnsBuilder().Srv(Inst, 47820, "phone.local").Txt(Inst, "v=2", "id=a1b2c3d4").Build());
            t.Deliver(new DnsBuilder().A("phone.local", "192.168.1.80").Build());
            var got = await Take(d.BrowseAsync(), 1);
            Assert.Equal("192.168.1.80", got[0].Address.ToString());
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Discovery_UsesLastKnownFirst()
    {
        var t = new FakeMdnsTransport();
        var dir = TempDir();
        try
        {
            var settings = new SettingsStore(Path.Combine(dir, "s.json"));
            settings.SetLastPhoneIp(IPAddress.Parse("192.168.1.99"));
            await using var d = new WifiDiscovery(t, settings, "a1b2c3d4", new FakeTimeProvider());
            t.Deliver(FullAnswer(ip: "192.168.1.50"));
            var got = await Take(d.BrowseAsync(), 2);
            Assert.Equal(DiscoverySource.LastKnown, got[0].Source);
            Assert.Equal("192.168.1.99", got[0].Address.ToString());
            Assert.Equal(47820, got[0].Port);
            Assert.Equal(DiscoverySource.Mdns, got[1].Source);
            Assert.Equal("192.168.1.50", got[1].Address.ToString());
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Discovery_RecordWorkingAddress_Persists()
    {
        var dir = TempDir();
        try
        {
            var path = Path.Combine(dir, "s.json");
            await using var d = new WifiDiscovery(new FakeMdnsTransport(), new SettingsStore(path), null, new FakeTimeProvider());
            d.RecordWorkingAddress(IPAddress.Parse("172.16.0.9"));
            Assert.Equal(IPAddress.Parse("172.16.0.9"), new SettingsStore(path).LastPhoneIp);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Discovery_RequeriesWithBackoffCappedAt30s()
    {
        var t = new FakeMdnsTransport();
        var time = new FakeTimeProvider();
        var dir = TempDir();
        try
        {
            await using var d = new WifiDiscovery(t, new SettingsStore(Path.Combine(dir, "s.json")), null, time);
            using var cts = new CancellationTokenSource();
            var e = d.BrowseAsync(cts.Token).GetAsyncEnumerator(cts.Token);
            var pending = e.MoveNextAsync().AsTask();

            await t.SentSignal.WaitAsync(TimeSpan.FromSeconds(10));          // query 1 at t=0
            foreach (var step in new[] { 1, 2, 4, 8, 16, 30, 30 })
            {
                await time.WaitForTimerAsync(TimeSpan.FromSeconds(step));
                time.Advance(TimeSpan.FromSeconds(step));
                await t.SentSignal.WaitAsync(TimeSpan.FromSeconds(10));
            }
            lock (t.Sent) Assert.Equal(8, t.Sent.Count);

            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Discovery_DisposeDisposesTransport()
    {
        var t = new FakeMdnsTransport();
        var d = new WifiDiscovery(t, new SettingsStore(Path.Combine(Path.GetTempPath(), "x-" + Guid.NewGuid().ToString("N") + ".json")), null, new FakeTimeProvider());
        await d.DisposeAsync();
        Assert.True(t.Disposed);
    }

    [Fact]
    public async Task UdpTransport_CreatesAndDisposesWithoutThrowing()
    {
        // Binds real sockets (best effort per interface); must tolerate any network state.
        await using var u = new UdpMdnsTransport();
        using var cts = new CancellationTokenSource(200);
        await u.SendAsync(MdnsPacket.BuildQuery(new[] { new MdnsQuestion(Svc, MdnsRecordType.Ptr) }), CancellationToken.None);
        try { await u.ReceiveAsync(cts.Token); } catch (OperationCanceledException) { }
    }
}
