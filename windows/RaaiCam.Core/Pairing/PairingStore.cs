using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using RaaiCam.Core.Phone;
using RaaiCam.Protocol.Crypto;

namespace RaaiCam.Core.Pairing;

/// <summary>Protects the pairing file at rest. DPAPI (CurrentUser) in production, a fake in tests.</summary>
public interface IDataProtector
{
    byte[] Protect(byte[] plain);
    /// <summary>Throws <see cref="CryptographicException"/> when the data was not protected by this protector / user.</summary>
    byte[] Unprotect(byte[] protectedData);
}

/// <summary>DPAPI, scope CurrentUser, with an app-specific entropy constant.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class DpapiDataProtector : IDataProtector
{
    static readonly byte[] Entropy = Encoding.UTF8.GetBytes("RaaiCam.pairing.v1");

    public byte[] Protect(byte[] plain) => ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
    public byte[] Unprotect(byte[] protectedData) => ProtectedData.Unprotect(protectedData, Entropy, DataProtectionScope.CurrentUser);
}

/// <summary>This PC's persistent identity: a 16-byte id and a long-term X25519 key pair. ToString never shows key material.</summary>
public sealed class PcIdentity
{
    public byte[] PcId { get; }
    public byte[] PrivateKey { get; }
    public byte[] PublicKey { get; }

    public PcIdentity(byte[] pcId, byte[] privateKey, byte[] publicKey)
    {
        PcId = pcId; PrivateKey = privateKey; PublicKey = publicKey;
    }

    /// <summary>A key object for the handshake (a copy: the caller may dispose it, which zeroes the copy only).</summary>
    public StaticKey ToStaticKey() => StaticKey.FromPrivate((byte[])PrivateKey.Clone());

    public override string ToString() => "PcIdentity";
}

/// <summary>The phone this PC is paired with. ToString never shows the key.</summary>
public sealed record PairedPhone(byte[] PublicKey, string Label, DateTimeOffset PairedAt)
{
    public override string ToString() => $"PairedPhone({Label})";
}

/// <summary>
/// Persistent PC identity and the single paired phone, in <c>%APPDATA%\RaaiCam\pairing.bin</c> as versioned JSON
/// <c>{v:1, pcId, pcPrivate, pcPublic, phone:{pub,label,pairedAt}|null}</c> protected with <see cref="IDataProtector"/>.
/// Writes are atomic (temp file + replace). A corrupt or undecryptable file is renamed to
/// <c>pairing.bin.bad-&lt;timestamp&gt;</c> and a fresh identity is created. Keys are never logged. Thread-safe.
/// </summary>
public sealed class PairingStore
{
    const int Version = 1;
    const string Source = "pairing";

    readonly string _path;
    readonly IDataProtector _protector;
    readonly ILogSink? _log;
    readonly TimeProvider _time;
    readonly object _gate = new();
    State? _state;
    bool _loaded;

    public PairingStore(string path, IDataProtector protector, ILogSink? log = null, TimeProvider? time = null)
    {
        _path = path;
        _protector = protector;
        _log = log;
        _time = time ?? TimeProvider.System;
    }

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RaaiCam", "pairing.bin");

    public PairedPhone? Phone
    {
        get { lock (_gate) { Load(); return _state?.Phone; } }
    }

    public PcIdentity GetOrCreateIdentity()
    {
        lock (_gate)
        {
            Load();
            if (_state is null)
            {
                var (priv, pub) = X25519.Generate();
                var fresh = new State(RandomNumberGenerator.GetBytes(16), priv, pub, null);
                Save(fresh); // persist first; only then expose it
                _state = fresh;
                Log("created a new PC identity");
            }
            return new PcIdentity((byte[])_state.PcId.Clone(), (byte[])_state.Private.Clone(), (byte[])_state.Public.Clone());
        }
    }

    /// <summary>Stores the paired phone (replacing any previous one) and persists.</summary>
    public PairedPhone SavePhone(byte[] phoneStaticPub, string label)
    {
        if (phoneStaticPub is null || phoneStaticPub.Length != 32) throw new ArgumentException("bad phone key", nameof(phoneStaticPub));
        GetOrCreateIdentity();
        lock (_gate)
        {
            var phone = new PairedPhone((byte[])phoneStaticPub.Clone(), label ?? "", _time.GetUtcNow());
            var next = _state! with { Phone = phone };
            Save(next);
            _state = next;
            Log("phone paired");
            return phone;
        }
    }

    /// <summary>Removes the paired phone; the PC identity is kept.</summary>
    public void Unpair()
    {
        lock (_gate)
        {
            Load();
            if (_state is null) return;
            var next = _state with { Phone = null };
            Save(next);
            _state = next;
            Log("phone unpaired");
        }
    }

    // ---- persistence ----

    sealed record State(byte[] PcId, byte[] Private, byte[] Public, PairedPhone? Phone);

    sealed class PhoneDto
    {
        [JsonPropertyName("pub")] public string? Pub { get; set; }
        [JsonPropertyName("label")] public string? Label { get; set; }
        [JsonPropertyName("pairedAt")] public DateTimeOffset PairedAt { get; set; }
    }

    sealed class FileDto
    {
        [JsonPropertyName("v")] public int V { get; set; }
        [JsonPropertyName("pcId")] public string? PcId { get; set; }
        [JsonPropertyName("pcPrivate")] public string? PcPrivate { get; set; }
        [JsonPropertyName("pcPublic")] public string? PcPublic { get; set; }
        [JsonPropertyName("phone")] public PhoneDto? Phone { get; set; }
    }

    void Load()
    {
        if (_loaded) return;
        if (!File.Exists(_path)) { _loaded = true; return; }
        // An IO error (locked file, transient failure) propagates with _loaded still false: we must never create a new
        // identity over a file we merely failed to read.
        var raw = File.ReadAllBytes(_path);
        try
        {
            var plain = _protector.Unprotect(raw);
            try { _state = Parse(plain); }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }
        catch (Exception e) when (e is CryptographicException or JsonException or FormatException or InvalidDataException
                                      or ArgumentException or NotSupportedException)
        {
            Quarantine(e.GetType().Name);
        }
        _loaded = true;
    }

    static State Parse(byte[] json)
    {
        var dto = JsonSerializer.Deserialize<FileDto>(json) ?? throw new InvalidDataException("empty");
        if (dto.V != Version) throw new InvalidDataException("version");
        var id = Convert.FromBase64String(dto.PcId ?? throw new InvalidDataException());
        var priv = Convert.FromBase64String(dto.PcPrivate ?? throw new InvalidDataException());
        var pub = Convert.FromBase64String(dto.PcPublic ?? throw new InvalidDataException());
        if (id.Length != 16 || priv.Length != 32 || pub.Length != 32) throw new InvalidDataException("lengths");
        if (!CryptographicOperations.FixedTimeEquals(X25519.FromPrivate(priv).pub, pub)) throw new InvalidDataException("key mismatch");
        PairedPhone? phone = null;
        if (dto.Phone is { } p)
        {
            var ppub = Convert.FromBase64String(p.Pub ?? throw new InvalidDataException());
            if (ppub.Length != 32) throw new InvalidDataException("phone key");
            phone = new PairedPhone(ppub, p.Label ?? "", p.PairedAt);
        }
        return new State(id, priv, pub, phone);
    }

    void Quarantine(string why)
    {
        _state = null;
        var bad = _path + ".bad-" + _time.GetUtcNow().ToString("yyyyMMddTHHmmssfff");
        try
        {
            if (File.Exists(bad)) bad += "-" + Guid.NewGuid().ToString("N")[..6];
            File.Move(_path, bad);
            Log($"pairing file unreadable ({why}); quarantined as {Path.GetFileName(bad)}, starting with a new identity");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log($"pairing file unreadable ({why}) and could not be quarantined: {e.GetType().Name}");
            throw; // never overwrite a file we could not move aside
        }
    }

    void Save(State s)
    {
        var dto = new FileDto
        {
            V = Version,
            PcId = Convert.ToBase64String(s.PcId),
            PcPrivate = Convert.ToBase64String(s.Private),
            PcPublic = Convert.ToBase64String(s.Public),
            Phone = s.Phone is null ? null : new PhoneDto
            {
                Pub = Convert.ToBase64String(s.Phone.PublicKey),
                Label = s.Phone.Label,
                PairedAt = s.Phone.PairedAt,
            },
        };
        var plain = JsonSerializer.SerializeToUtf8Bytes(dto);
        byte[] blob;
        try { blob = _protector.Protect(plain); }
        finally { CryptographicOperations.ZeroMemory(plain); }

        var dir = Path.GetDirectoryName(Path.GetFullPath(_path))!;
        Directory.CreateDirectory(dir);
        var tmp = Path.Combine(dir, Path.GetFileName(_path) + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp");
        try
        {
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                fs.Write(blob);
                fs.Flush(true); // reach the disk before the replace
            }
            if (File.Exists(_path)) File.Replace(tmp, _path, null);
            else File.Move(tmp, _path);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch (IOException) { }
        }
    }

    void Log(string line)
    {
        try { _log?.Write(Source, line); } catch (Exception) { }
    }
}
