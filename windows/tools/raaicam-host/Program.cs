using System.Diagnostics;
using System.Globalization;
using RaaiCam.Core.Hosting;
using RaaiCam.Core.Phone;
using RaaiCam.Core.Usbmux;
using RaaiCam.HostApp;
using RaaiCam.Media;

// raaicam-host: phone (USB) -> H.264 decoder -> RaaiCam virtual camera.
if (args.Contains("--list-devices"))
{
    // Preflight for run-phone-checks.ps1: "devices=N", "app=reachable|unreachable|n/a". Exit 0 when a device is attached.
    using var lcts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    var usb = new UsbmuxClient();
    IReadOnlyList<UsbDevice> devs;
    try { devs = await usb.ListDevicesAsync(lcts.Token); }
    catch (Exception e) when (e is UsbmuxUnavailableException or UsbmuxException or OperationCanceledException or IOException)
    {
        Console.WriteLine("devices=0");
        Console.WriteLine("app=n/a");
        Console.WriteLine("error=" + e.Message);
        return 2;
    }
    Console.WriteLine($"devices={devs.Count}");
    if (devs.Count == 0) { Console.WriteLine("app=n/a"); return 2; }
    string app;
    try
    {
        await using var s = await usb.ConnectAsync(devs[0].DeviceId, 47820, lcts.Token);
        app = "reachable";
    }
    catch (Exception e) when (e is UsbmuxException or UsbmuxUnavailableException or IOException or OperationCanceledException)
    {
        app = "unreachable";
    }
    Console.WriteLine($"app={app}");
    return 0;
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    try { cts.Cancel(); } catch (ObjectDisposedException) { }
};
var ct = cts.Token;

var log = new FileLogSink();
using var sink = new VirtualCameraSink();
var adapter = new SinkAdapter(sink);
var host = new Host(adapter, new UsbPhoneConnector(new UsbmuxClient(), log), new H264DecoderFactory(), adapter,
    Stopwatch.GetTimestamp, log);

Console.WriteLine("raaicam-host: running, Ctrl+C to exit.");
var run = host.RunAsync(ct);
while (!run.IsCompleted)
{
    try { await Task.WhenAny(run, Task.Delay(1000, ct)); }
    catch (OperationCanceledException) { }
    if (run.IsCompleted) break;
    Console.WriteLine(FormatStatus(host.Status));
}
try { await run; }
catch (OperationCanceledException) { }
Console.WriteLine("raaicam-host: stopped.");
return 0;

static string FormatStatus(HostStatus s)
{
    var ic = CultureInfo.InvariantCulture;
    string consumer = s.ConsumerWidth > 0 ? $"{s.ConsumerWidth}x{s.ConsumerHeight}" : "inactive";
    string lat = s.LatencyP50Ms is { } p50 && s.LatencyP95Ms is { } p95
        ? string.Create(ic, $"{p50:F0}/{p95:F0}ms") : "-/-ms";
    string rtt = s.RttMs is { } r ? string.Create(ic, $"{r:F0}ms") : "-";
    string rate = s.BitrateMbps is { } b ? string.Create(ic, $"{b:F1}Mbps") : "-";
    string batt = s.BatteryPercent is { } p ? $"{p}%{(s.Charging == true ? "+" : "")}" : "-";
    return string.Create(ic,
        $"link={s.Link} phone={s.Phone ?? "-"} consumer={consumer} in={s.FpsIn:F1} out={s.FpsOut:F1} lat={lat} rtt={rtt} {rate} thermal={s.Thermal ?? "-"} batt={batt}");
}
