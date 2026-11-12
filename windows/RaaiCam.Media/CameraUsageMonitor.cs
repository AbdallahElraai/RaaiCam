using Microsoft.Win32;

namespace RaaiCam.Media;

public sealed record CameraClient(string ExePath, DateTime StartedUtc);

/// <summary>
/// Detects which app currently has a camera open, from the CapabilityAccessManager consent store.
/// Best-effort and unreliable for virtual cameras: the Windows Frame Server opens the device, so the consent store
/// does not record the app that uses the virtual camera (FR-VC-6 is not met in Phase 1A). Display only; do not feed UI decisions.
/// An entry is "current" when LastUsedTimeStop == 0 (or less than LastUsedTimeStart). Values are FILETIMEs.
/// </summary>
public sealed class CameraUsageMonitor
{
    private const string WebcamKey = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\webcam";
    private static readonly string[] OwnProcessSuffixes = { "raaicam-dev.exe", "vcam-probe.exe" };

    private readonly Func<IEnumerable<(string keyName, long lastUsedStart, long lastUsedStop)>> _reader;

    // Boot time is derived from uptime (TickCount64); Windows Fast Startup may not reset it (known limitation).
    private readonly Func<DateTime> _bootUtc;

    /// <param name="reader">Entries; a missing registry value is reported as 0.</param>
    /// <param name="bootTimeUtc">Last boot time (injectable for tests); entries that started before it are stale.</param>
    public CameraUsageMonitor(Func<IEnumerable<(string keyName, long lastUsedStart, long lastUsedStop)>> reader,
                              Func<DateTime>? bootTimeUtc = null)
    {
        _reader = reader;
        _bootUtc = bootTimeUtc ?? (() => DateTime.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64));
    }

    public static CameraUsageMonitor CreateDefault() => new(ReadRegistry);

    public CameraClient? CurrentClient()
    {
        List<(string keyName, long lastUsedStart, long lastUsedStop)> entries;
        try { entries = _reader().ToList(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return null; }

        long bootFt = _bootUtc().ToFileTimeUtc();
        CameraClient? best = null;
        long bestStart = long.MinValue;
        foreach (var (keyName, start, stop) in entries)
        {
            if (start <= 0) continue;          // never used the camera (values absent)
            if (start < bootFt) continue;      // stale entry from before the last boot
            if (!(stop == 0 || stop < start)) continue;
            string path = keyName.Replace('#', '\\');
            if (OwnProcessSuffixes.Any(s => path.EndsWith(s, StringComparison.OrdinalIgnoreCase))) continue;
            if (best != null && start <= bestStart) continue;
            bestStart = start;
            best = new CameraClient(path, ToUtc(start));
        }
        return best;
    }

    private static DateTime ToUtc(long fileTime)
    {
        try { return fileTime <= 0 ? DateTime.MinValue : DateTime.FromFileTimeUtc(fileTime); }
        catch (ArgumentOutOfRangeException) { return DateTime.MinValue; }
    }

    private static IEnumerable<(string, long, long)> ReadRegistry()
    {
        var result = new List<(string, long, long)>();
        using var root = Registry.CurrentUser.OpenSubKey(WebcamKey);
        if (root == null) return result;

        foreach (var name in root.GetSubKeyNames())
        {
            using var sub = root.OpenSubKey(name);
            if (sub == null) continue;
            if (name.Equals("NonPackaged", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var app in sub.GetSubKeyNames())
                {
                    using var k = sub.OpenSubKey(app);
                    if (k != null) result.Add((app, Q(k, "LastUsedTimeStart"), Q(k, "LastUsedTimeStop")));
                }
            }
            else
            {
                // Packaged app: the key name is the package family name.
                result.Add((name, Q(sub, "LastUsedTimeStart"), Q(sub, "LastUsedTimeStop")));
            }
        }
        return result;
    }

    private static long Q(RegistryKey k, string value) => k.GetValue(value) is long l ? l : 0;
}
