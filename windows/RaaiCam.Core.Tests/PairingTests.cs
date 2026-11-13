using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using RaaiCam.Core.Pairing;

namespace RaaiCam.Core.Tests;

/// <summary>Reversible stand-in for DPAPI: "FAKE" prefix + XOR. Unprotect rejects anything without the prefix.</summary>
sealed class FakeProtector : IDataProtector
{
    static readonly byte[] Magic = "FAKE"u8.ToArray();

    public byte[] Protect(byte[] plain)
    {
        var o = new byte[Magic.Length + plain.Length];
        Magic.CopyTo(o, 0);
        for (int i = 0; i < plain.Length; i++) o[Magic.Length + i] = (byte)(plain[i] ^ 0x5A);
        return o;
    }

    public byte[] Unprotect(byte[] protectedData)
    {
        if (protectedData.Length < Magic.Length || !protectedData.AsSpan(0, Magic.Length).SequenceEqual(Magic))
            throw new CryptographicException("not protected by FakeProtector");
        var o = new byte[protectedData.Length - Magic.Length];
        for (int i = 0; i < o.Length; i++) o[i] = (byte)(protectedData[Magic.Length + i] ^ 0x5A);
        return o;
    }
}

/// <summary>TimeProvider with a settable wall clock (FakeTimeProvider only controls timers).</summary>
sealed class ManualClock : TimeProvider
{
    DateTimeOffset _now = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan by) => _now += by;
}

sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "raaicam-test-" + Guid.NewGuid().ToString("N"));
    public TempDir() => Directory.CreateDirectory(Path);
    public string File(string name) => System.IO.Path.Combine(Path, name);
    public void Dispose() { try { Directory.Delete(Path, true); } catch (IOException) { } }
}

public class PairingTests
{
    static readonly byte[] PhonePub = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();

    [Fact]
    public void Store_PersistsIdentityAcrossInstances()
    {
        using var dir = new TempDir();
        var path = dir.File("sub/pairing.bin");
        var a = new PairingStore(path, new FakeProtector());
        var id1 = a.GetOrCreateIdentity();
        a.SavePhone(PhonePub, "Test iPhone");

        var b = new PairingStore(path, new FakeProtector());
        var id2 = b.GetOrCreateIdentity();
        Assert.Equal(id1.PcId, id2.PcId);
        Assert.Equal(id1.PrivateKey, id2.PrivateKey);
        Assert.Equal(id1.PublicKey, id2.PublicKey);
        Assert.Equal(16, id1.PcId.Length);
        Assert.NotNull(b.Phone);
        Assert.Equal(PhonePub, b.Phone!.PublicKey);
        Assert.Equal("Test iPhone", b.Phone.Label);
    }

    [Fact]
    public void Store_DataIsProtected()
    {
        using var dir = new TempDir();
        var path = dir.File("pairing.bin");
        var s = new PairingStore(path, new FakeProtector());
        var id = s.GetOrCreateIdentity();
        var bytes = File.ReadAllBytes(path);
        Assert.False(bytes.AsSpan().IndexOf(id.PrivateKey) >= 0);
        var text = Encoding.UTF8.GetString(bytes);
        Assert.DoesNotContain(Convert.ToBase64String(id.PrivateKey), text);
        Assert.DoesNotContain("pcPrivate", text);
        Assert.Empty(Directory.GetFiles(dir.Path, "*.tmp"));
    }

    [Fact]
    public void Store_UnpairDeletes()
    {
        using var dir = new TempDir();
        var path = dir.File("pairing.bin");
        var s = new PairingStore(path, new FakeProtector());
        var id = s.GetOrCreateIdentity();
        s.SavePhone(PhonePub, "p");
        s.Unpair();
        Assert.Null(s.Phone);
        var again = new PairingStore(path, new FakeProtector());
        Assert.Null(again.Phone);
        Assert.Equal(id.PcId, again.GetOrCreateIdentity().PcId); // identity survives unpair
    }

    [Fact]
    public void Store_CorruptFileQuarantined()
    {
        using var dir = new TempDir();
        var path = dir.File("pairing.bin");
        File.WriteAllBytes(path, [1, 2, 3, 4, 5, 6, 7, 8]);
        var sink = new ListLogSink();
        var s = new PairingStore(path, new FakeProtector(), sink);
        var id = s.GetOrCreateIdentity();
        Assert.Equal(16, id.PcId.Length);
        Assert.Null(s.Phone);
        var bad = Directory.GetFiles(dir.Path, "pairing.bin.bad-*");
        Assert.Single(bad);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, File.ReadAllBytes(bad[0]));
        Assert.Contains(sink.Snapshot(), l => l.Line.Contains("quarantine"));
        // the fresh identity is persisted
        Assert.Equal(id.PcId, new PairingStore(path, new FakeProtector()).GetOrCreateIdentity().PcId);
    }

    [Fact]
    public void Store_ValidlyProtectedGarbageJsonQuarantined()
    {
        using var dir = new TempDir();
        var path = dir.File("pairing.bin");
        File.WriteAllBytes(path, new FakeProtector().Protect("{\"v\":7}"u8.ToArray()));
        var s = new PairingStore(path, new FakeProtector());
        s.GetOrCreateIdentity();
        Assert.Single(Directory.GetFiles(dir.Path, "pairing.bin.bad-*"));
    }

    [Fact]
    public void Store_LogsNeverContainKeys()
    {
        using var dir = new TempDir();
        var sink = new ListLogSink();
        var s = new PairingStore(dir.File("pairing.bin"), new FakeProtector(), sink);
        var id = s.GetOrCreateIdentity();
        s.SavePhone(PhonePub, "p");
        s.Unpair();
        foreach (var (_, line) in sink.Snapshot())
        {
            Assert.DoesNotContain(Convert.ToBase64String(id.PrivateKey), line);
            Assert.DoesNotContain(Convert.ToHexString(id.PrivateKey), line, StringComparison.OrdinalIgnoreCase);
        }
    }

    static Dictionary<string, string> Query(string url)
    {
        Assert.StartsWith("raaicam://pair?", url);
        return url["raaicam://pair?".Length..].Split('&').Select(p => p.Split('=', 2)).ToDictionary(p => p[0], p => p[1]);
    }

    static byte[] B64Url(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s + new string('=', (4 - s.Length % 4) % 4));
    }

    [Fact]
    public void Qr_UrlHasRequiredFieldsAndNoIps()
    {
        using var dir = new TempDir();
        var store = new PairingStore(dir.File("pairing.bin"), new FakeProtector());
        var id = store.GetOrCreateIdentity();
        var clock = new ManualClock();
        var s = PairingSession.Begin(id, "Test PC", clock);
        var q = Query(s.QrUrl);
        Assert.Equal(new[] { "v", "pc", "name", "pk", "tok", "exp" }, q.Keys.ToArray());
        Assert.Equal("2", q["v"]);
        Assert.Equal(id.PcId, B64Url(q["pc"]));
        Assert.Equal(id.PublicKey, B64Url(q["pk"]));
        Assert.Equal(s.Token, B64Url(q["tok"]));
        Assert.Equal(16, s.Token.Length);
        Assert.Equal("Test%20PC", q["name"]);
        Assert.Equal(clock.GetUtcNow().ToUnixTimeSeconds() + 300, long.Parse(q["exp"]));
        Assert.DoesNotMatch(new Regex(@"\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}"), s.QrUrl);
        foreach (var k in new[] { "pc", "pk", "tok" })
        {
            Assert.DoesNotContain('=', q[k]);
            Assert.DoesNotContain('+', q[k]);
            Assert.DoesNotContain('/', q[k]);
        }
        Assert.DoesNotContain(Convert.ToBase64String(id.PrivateKey), s.QrUrl);
    }

    [Fact]
    public void Qr_NameIsCappedAndEncoded()
    {
        using var dir = new TempDir();
        var id = new PairingStore(dir.File("pairing.bin"), new FakeProtector()).GetOrCreateIdentity();
        var s = PairingSession.Begin(id, new string('a', 30) + " & " + new string('b', 30), new ManualClock());
        var name = Uri.UnescapeDataString(Query(s.QrUrl)["name"]);
        Assert.Equal(40, name.Length);
        Assert.DoesNotContain("&", Query(s.QrUrl)["name"]);
        Assert.StartsWith(new string('a', 30) + " & ", name);
    }

    [Fact]
    public void Qr_TokenRandomAndExpiry()
    {
        using var dir = new TempDir();
        var id = new PairingStore(dir.File("pairing.bin"), new FakeProtector()).GetOrCreateIdentity();
        var clock = new ManualClock();
        var a = PairingSession.Begin(id, "PC", clock);
        var b = PairingSession.Begin(id, "PC", clock);
        Assert.NotEqual(a.Token, b.Token);
        Assert.NotEqual(new byte[16], a.Token);
        Assert.False(a.IsExpired);
        var expected = clock.GetUtcNow() + TimeSpan.FromMinutes(5);
        clock.Advance(TimeSpan.FromSeconds(299));
        Assert.False(a.IsExpired);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(a.IsExpired);
        Assert.Equal(expected, a.ExpiresAt);
    }

    [Fact]
    public void Qr_ConsoleStringIsSquareAndNonEmpty()
    {
        var text = PairingQr.ToConsoleString("raaicam://pair?v=2&x=1");
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToArray();
        Assert.True(lines.Length >= 21);
        Assert.All(lines, l => Assert.Equal(lines[0].Length, l.Length));
        // two characters per module: the character width is twice the module count (rows == modules)
        Assert.Equal(lines.Length * 2, lines[0].Length);
    }
}
