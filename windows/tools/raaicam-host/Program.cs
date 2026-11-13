using System.Diagnostics;
using RaaiCam.Core.Discovery;
using RaaiCam.Core.Hosting;
using RaaiCam.Core.Pairing;
using RaaiCam.Core.Phone;
using RaaiCam.Core.Usbmux;
using RaaiCam.HostApp;
using RaaiCam.Media;

// raaicam-host: phone (USB or Wi-Fi) -> H.264 decoder -> RaaiCam virtual camera.
// raaicam-host [--pair | --unpair | --list-devices]; exit codes: 0 ok, 1 pairing failed, 2 no device, 3 not paired, 4 pairing file error.
if (args.Contains("--list-devices"))
{
    // Preflight for run-phone-checks.ps1: "devices=N", "app=reachable|unreachable|n/a". Exit 0 when a device is attached.
    using var lcts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    var usb = new UsbmuxClient();
    IReadOnlyList<UsbDevice> devs;
    try { devs = await usb.ListDevicesAsync(lcts.Token); }
    catch (Exception e)
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
    catch (Exception)
    {
        app = "unreachable";
    }
    Console.WriteLine($"app={app}");
    return 0;
}

if (args.Contains("--check-paired"))
{
    // Preflight for run-phone-checks.ps1: "paired=yes|no". Exit 0 paired, 3 not paired, 4 pairing file unreadable. Touches no phone.
    try
    {
        var checkStore = new PairingStore(PairingStore.DefaultPath, new DpapiDataProtector(), new FileLogSink());
        bool paired = checkStore.Phone is not null;
        Console.WriteLine(paired ? "paired=yes" : "paired=no");
        return paired ? ExitCodes.Ok : ExitCodes.NotPaired;
    }
    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
    {
        Console.WriteLine("paired=error");
        return ExitCodes.StoreError;
    }
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    try { cts.Cancel(); } catch (ObjectDisposedException) { }
};
var ct = cts.Token;

var log = new FileLogSink();
var store = new PairingStore(PairingStore.DefaultPath, new DpapiDataProtector(), log);
if (args.Contains("--pair")) return await HostCli.PairAsync(store, log, ct);
if (args.Contains("--unpair")) return await HostCli.UnpairAsync(store, log, ct);

// Normal run needs a paired phone; say how to pair instead of polling forever.
try
{
    if (store.Phone is null)
    {
        Console.WriteLine("raaicam-host: no phone is paired with this PC. Run: raaicam-host --pair");
        return ExitCodes.NotPaired;
    }
}
catch (Exception e) when (e is IOException or UnauthorizedAccessException)
{
    Console.Error.WriteLine("raaicam-host: cannot read the pairing file (" + e.GetType().Name + "). Close other programs using it and try again.");
    return ExitCodes.StoreError;
}

using var sink = new VirtualCameraSink();
var adapter = new SinkAdapter(sink);
await using var wifi = new WifiPhoneConnector(log, store, new SettingsStore(SettingsStore.DefaultPath));
var host = new Host(adapter, new UsbPhoneConnector(new UsbmuxClient(), log, store), new H264DecoderFactory(), adapter,
    Stopwatch.GetTimestamp, log, wifi: wifi, pairedKey: () => store.Phone?.PublicKey);

Console.WriteLine("raaicam-host: running, Ctrl+C to exit.");
var run = host.RunAsync(ct);
while (!run.IsCompleted)
{
    try { await Task.WhenAny(run, Task.Delay(1000, ct)); }
    catch (OperationCanceledException) { }
    if (run.IsCompleted) break;
    Console.WriteLine(StatusLine.Format(host.Status));
}
try { await run; }
catch (OperationCanceledException) { }
Console.WriteLine("raaicam-host: stopped.");
return 0;
