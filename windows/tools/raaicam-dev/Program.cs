using System.Diagnostics;
using System.Globalization;
using RaaiCam.Media;

// raaicam-dev: feeds the RaaiCam virtual camera with a test pattern (no phone needed).
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    try { cts.Cancel(); } catch (ObjectDisposedException) { }
};
var ct = cts.Token;

using var sink = new VirtualCameraSink();
var monitor = CameraUsageMonitor.CreateDefault();
var pattern = new TestPatternSource();
using var framePacer = new FramePacer(30); // 30 fps
using var idlePacer = new FramePacer(4);               // 250 ms idle poll

long freq = Stopwatch.Frequency;

byte[] buf = Array.Empty<byte>();
ulong frameId = 0, written = 0, writtenAtLastStatus = 0;
long lastStatus = Stopwatch.GetTimestamp();
ConsumerState? cs = null;
CameraClient? client = null;
var lastErr = new Dictionary<string, long>();

void LogError(Exception e)
{
    string key = e.GetType().Name + ": " + e.Message;
    long now = Stopwatch.GetTimestamp();
    if (lastErr.TryGetValue(key, out long t) && now - t < 5 * freq) return;
    lastErr[key] = now;
    Console.Error.WriteLine("raaicam-dev: error: " + key);
}

Console.WriteLine("raaicam-dev: running, Ctrl+C to exit.");

while (!ct.IsCancellationRequested)
{
    bool active = false;
    try
    {
        // ReadConsumerState self-heals a closed sink (retries are throttled to 500 ms inside the sink).
        cs = sink.ReadConsumerState();
        active = cs is { Active: true, RequestedWidth: > 0, RequestedHeight: > 0 };

        if (active)
        {
            int w = cs!.RequestedWidth, h = cs.RequestedHeight, stride = w;
            int size = stride * h * 3 / 2;
            if (buf.Length != size) buf = new byte[size];
            if (pattern.Render(buf, w, h, stride, ++frameId) > 0 &&
                sink.WriteFrame(buf, w, h, stride, Stopwatch.GetTimestamp()))
                written++;
        }

        long now = Stopwatch.GetTimestamp();
        if (now - lastStatus >= freq)
        {
            double secs = (double)(now - lastStatus) / freq;
            double fps = (written - writtenAtLastStatus) / secs;
            writtenAtLastStatus = written;
            lastStatus = now;
            try { client = monitor.CurrentClient(); } catch (Exception e) { client = null; LogError(e); }
            string state = cs == null ? "closed" : cs.Active ? "active" : "inactive";
            string res = cs != null ? $"{cs.RequestedWidth}x{cs.RequestedHeight}" : "0x0";
            string c = client?.ExePath ?? "none";
            string f = fps.ToString("F1", CultureInfo.InvariantCulture);
            Console.WriteLine(state == "active"
                ? $"consumer=active {res} gen={cs!.Generation} client(best-effort,unreliable)={c} fps={f} written={written}"
                : $"consumer={state} client(best-effort,unreliable)={c} fps={f} written={written}");
        }
    }
    catch (Exception e) when (e is not OperationCanceledException)
    {
        LogError(e);
    }

    // Absolute-deadline pacing on a high-resolution timer (no spinning).
    if (!(active ? framePacer : idlePacer).WaitNext(ct)) break;
}

Console.WriteLine("raaicam-dev: stopped.");
return 0;
