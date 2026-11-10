using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Sockets;

const ushort DevicePort = 47820;

// Output path: --out <path>, else <repo root>/spikes/out/frame.jpg (repo root = nearest ancestor with .git).
string outPath;
var outIdx = Array.IndexOf(args, "--out");
if (outIdx >= 0 && outIdx + 1 < args.Length) outPath = Path.GetFullPath(args[outIdx + 1]);
else
{
    var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
    while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, ".git")) && !File.Exists(Path.Combine(dir.FullName, ".git")))
        dir = dir.Parent;
    outPath = Path.Combine(dir?.FullName ?? Directory.GetCurrentDirectory(), "spikes", "out", "frame.jpg");
}

static async Task<TcpClient?> OpenUsbmuxAsync()
{
    var c = new TcpClient();
    try { await c.ConnectAsync("127.0.0.1", 27015); return c; }
    catch (SocketException) { c.Dispose(); return null; }
}

static async Task<Dictionary<string, object>> RequestAsync(NetworkStream s, uint tag, Dictionary<string, object> req)
{
    var pkt = UsbmuxProtocol.EncodePlistPacket(tag, req);
    await s.WriteAsync(pkt);
    var header = new byte[16];
    await s.ReadExactlyAsync(header);
    var total = BinaryPrimitives.ReadUInt32LittleEndian(header);
    if (total < 16 || total > 1_048_576) throw new InvalidDataException($"Bad usbmux packet length {total}.");
    var full = new byte[total];
    header.CopyTo(full, 0);
    await s.ReadExactlyAsync(full.AsMemory(16));
    return UsbmuxProtocol.DecodePacket(full).Plist;
}

// 1. List devices.
using var listConn = await OpenUsbmuxAsync();
if (listConn == null)
{
    Console.WriteLine("Apple Mobile Device Service not reachable. Install the Apple Devices app.");
    return 1;
}
try
{
var reply = await RequestAsync(listConn.GetStream(), 1, UsbmuxProtocol.ListDevicesRequest());
var devices = (reply.TryGetValue("DeviceList", out var dl) ? (List<object>)dl : new()).Cast<Dictionary<string, object>>().ToList();
if (devices.Count == 0) { Console.WriteLine("No devices attached."); return 3; }

int? usbDeviceId = null;
foreach (var d in devices)
{
    var props = (Dictionary<string, object>)d["Properties"];
    var id = Convert.ToInt32(props["DeviceID"]);
    var type = props.GetValueOrDefault("ConnectionType")?.ToString();
    Console.WriteLine($"DeviceID={id} ConnectionType={type} SerialNumber={props.GetValueOrDefault("SerialNumber")}");
    if (usbDeviceId == null && type == "USB") usbDeviceId = id;
}
if (usbDeviceId == null) { Console.WriteLine("No USB device found."); return 3; }

// 2. Connect on a NEW socket; after Ok the socket is a raw tunnel to the phone.
var sw = Stopwatch.StartNew();
using var tunnel = await OpenUsbmuxAsync();
if (tunnel == null) { Console.WriteLine("Apple Mobile Device Service not reachable. Install the Apple Devices app."); return 1; }
var ts = tunnel.GetStream();
var res = UsbmuxProtocol.ParseResult(await RequestAsync(ts, 2, UsbmuxProtocol.ConnectRequest(usbDeviceId.Value, DevicePort)));
if (res == UsbmuxResult.ConnectionRefused)
{
    Console.WriteLine("Phone found, but the RaaiCam Spike app isn't open.");
    return 2;
}
if (res != UsbmuxResult.Ok) { Console.WriteLine($"Connect failed: {res}"); return 3; }

// 3. Read [u32 BE length][JPEG].
var lenBuf = new byte[4];
await ts.ReadExactlyAsync(lenBuf);
var len = (long)BinaryPrimitives.ReadUInt32BigEndian(lenBuf);
if (len > 32 * 1024 * 1024) throw new InvalidDataException($"Frame length {len} exceeds 32 MB.");
if (len == 0) { Console.WriteLine("Phone has no frame yet (length 0)."); return 4; }
var jpeg = new byte[(int)len];
await ts.ReadExactlyAsync(jpeg);
sw.Stop();

Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
await File.WriteAllBytesAsync(outPath, jpeg);
Console.WriteLine($"Saved {len} bytes to {outPath} in {sw.ElapsedMilliseconds} ms (connect + fetch).");
return 0;
}
catch (Exception ex) when (ex is IOException or EndOfStreamException or InvalidDataException or InvalidCastException)
{
    Console.WriteLine($"Protocol/connection error: {ex.GetType().Name}: {ex.Message}");
    return 3;
}
