using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RaaiCam.Core.Discovery;

/// <summary>
/// Tiny JSON settings file (<c>%APPDATA%\RaaiCam\settings.json</c>). Unknown keys are preserved. An unreadable or
/// corrupt file reads as empty; writes are atomic (temp file + replace). Thread-safe.
/// </summary>
public sealed class SettingsStore
{
    const string LastPhoneIpKey = "lastPhoneIp";

    readonly string _path;
    readonly object _gate = new();

    public SettingsStore(string path) { _path = path; }

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RaaiCam", "settings.json");

    /// <summary>The address the phone last answered on, or null.</summary>
    public IPAddress? LastPhoneIp
    {
        get
        {
            lock (_gate)
            {
                var s = ReadRoot()[LastPhoneIpKey] is JsonValue v && v.TryGetValue<string>(out var str) ? str : null;
                return s is not null && IPAddress.TryParse(s, out var ip) ? ip : null;
            }
        }
    }

    public void SetLastPhoneIp(IPAddress address)
    {
        lock (_gate)
        {
            var root = ReadRoot();
            root[LastPhoneIpKey] = address.ToString();
            Write(root);
        }
    }

    JsonObject ReadRoot()
    {
        try
        {
            if (!File.Exists(_path)) return new JsonObject();
            return JsonNode.Parse(File.ReadAllBytes(_path)) as JsonObject ?? new JsonObject();
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return new JsonObject();
        }
    }

    void Write(JsonObject root)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(root, new JsonSerializerOptions { WriteIndented = true });
        var dir = Path.GetDirectoryName(Path.GetFullPath(_path))!;
        Directory.CreateDirectory(dir);
        var tmp = Path.Combine(dir, Path.GetFileName(_path) + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp");
        try
        {
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                fs.Write(bytes);
                fs.Flush(true);
            }
            if (File.Exists(_path)) File.Replace(tmp, _path, null);
            else File.Move(tmp, _path);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch (IOException) { }
        }
    }
}
