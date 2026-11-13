using System.Globalization;

namespace RaaiCam.Core.Phone;

public interface ILogSink
{
    void Write(string source, string line);
}

/// <summary>
/// Appends to <c>%LOCALAPPDATA%\RaaiCam\logs\phone-YYYYMMDD.log</c> (local date). Files not written for
/// more than 7 days are deleted on construction. Never throws from Write.
/// </summary>
public sealed class FileLogSink : ILogSink
{
    readonly string _dir;
    readonly Func<DateTime> _localNow;
    readonly object _gate = new();

    public FileLogSink()
        : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RaaiCam", "logs"))
    {
    }

    public FileLogSink(string directory, Func<DateTime>? localNow = null)
    {
        _dir = directory;
        _localNow = localNow ?? (() => DateTime.Now);
        try
        {
            Directory.CreateDirectory(_dir);
            var cutoff = _localNow().AddDays(-7);
            foreach (var f in Directory.EnumerateFiles(_dir, "phone-*.log"))
                if (File.GetLastWriteTime(f) < cutoff) File.Delete(f);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // logging must never take the app down
        }
    }

    public void Write(string source, string line)
    {
        var now = _localNow();
        var clean = line.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ');
        var text = string.Create(CultureInfo.InvariantCulture, $"{now:HH:mm:ss.fff} [{source}] {clean}{Environment.NewLine}");
        var path = Path.Combine(_dir, string.Create(CultureInfo.InvariantCulture, $"phone-{now:yyyyMMdd}.log"));
        lock (_gate)
        {
            try { File.AppendAllText(path, text); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }
}
