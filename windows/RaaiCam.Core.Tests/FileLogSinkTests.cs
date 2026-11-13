using RaaiCam.Core.Phone;

namespace RaaiCam.Core.Tests;

public class FileLogSinkTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "raaicam-logs-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    [Fact]
    public void FileLogSink_WritesToDatedFile_AndDeletesOldFiles()
    {
        Directory.CreateDirectory(_dir);
        var old = Path.Combine(_dir, "phone-20200101.log");
        var recent = Path.Combine(_dir, "phone-20260101.log");
        File.WriteAllText(old, "x");
        File.WriteAllText(recent, "x");
        File.SetLastWriteTime(old, new DateTime(2026, 11, 1));
        File.SetLastWriteTime(recent, new DateTime(2026, 11, 10));

        var now = new DateTime(2026, 11, 13, 9, 30, 15, DateTimeKind.Local);
        var sink = new FileLogSink(_dir, () => now);
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(recent));

        sink.Write("phone", "I hello\r\nworld");
        var text = File.ReadAllText(Path.Combine(_dir, "phone-20261113.log"));
        Assert.Contains("[phone] I hello world", text);
        Assert.StartsWith("09:30:15", text);
    }
}
