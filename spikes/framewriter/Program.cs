// RaaiCam Phase 0 spike: writes animated NV12 frames into the virtual camera's shared memory.
//
//   FrameWriter [--seconds N] [--wait N]
//
// --seconds N  stop after N seconds (default: until Ctrl+C)
// --wait N     retry opening the mapping for up to N seconds (default 0). The mapping only exists
//              once Frame Server has loaded the camera DLL (an app opened the camera).
// Exit code 3 (with the exception message) if the mapping can't be opened.
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using FrameWriter;

int seconds = 0, waitSeconds = 0;
for (int i = 0; i + 1 < args.Length; i += 2)
{
    if (args[i] == "--seconds") seconds = int.Parse(args[i + 1]);
    else if (args[i] == "--wait") waitSeconds = int.Parse(args[i + 1]);
}

MemoryMappedFile? mmf = null;
string? mappingName = null;
var errors = new List<string>();
var waitUntil = Stopwatch.StartNew();
while (mmf == null)
{
    errors.Clear();
    foreach (var name in SharedFrameLayout.MappingNames)
    {
        try
        {
            mmf = MemoryMappedFile.OpenExisting(name, MemoryMappedFileRights.ReadWrite);
            mappingName = name;
            break;
        }
        catch (Exception ex)
        {
            errors.Add($"{name}: {ex.GetType().Name}: {ex.Message} (HResult 0x{ex.HResult:X8})");
        }
    }
    if (mmf != null) break;
    if (waitUntil.Elapsed.TotalSeconds >= waitSeconds)
    {
        Console.Error.WriteLine("ERROR: cannot open the shared frame mapping:");
        foreach (var e in errors) Console.Error.WriteLine("  " + e);
        return 3;
    }
    Thread.Sleep(200);
}
foreach (var e in errors) Console.WriteLine("  (tried) " + e);
Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} opened mapping {mappingName}");

EventWaitHandle? frameReady = null;
foreach (var name in SharedFrameLayout.EventNames)
{
    try
    {
        frameReady = EventWaitHandle.OpenExisting(name);
        Console.WriteLine($"opened event {name}");
        break;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  (tried) event {name}: {ex.GetType().Name}: {ex.Message}");
    }
}
if (frameReady == null) Console.WriteLine("WARNING: frame-ready event not available; writing without signalling");

using var view = mmf.CreateViewAccessor(0, SharedFrameLayout.MappingSize, MemoryMappedFileAccess.ReadWrite);
var stop = new ManualResetEventSlim(false);
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Set(); };

unsafe
{
    byte* basePtr = null;
    view.SafeMemoryMappedViewHandle.AcquirePointer(ref basePtr);
    try
    {
        basePtr += view.PointerOffset;
        Run(basePtr);
    }
    finally
    {
        view.SafeMemoryMappedViewHandle.ReleasePointer();
    }
}
frameReady?.Dispose();
mmf.Dispose();
Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} writer stopped");
return 0;

unsafe void Run(byte* p)
{
    var magic = Unsafe.ReadUnaligned<uint>(p + SharedFrameLayout.OffMagic);
    if (magic != SharedFrameLayout.Magic)
        Console.WriteLine($"WARNING: header magic 0x{magic:X8}, expected 0x{SharedFrameLayout.Magic:X8}");

    byte* frame = p + SharedFrameLayout.HeaderSize;
    var clock = Stopwatch.StartNew();
    var period = TimeSpan.FromSeconds(1.0 / 30);
    long written = 0;
    int lastActive = -1, curW = 0, curH = 0, curStride = 0;
    byte[] bars = Array.Empty<byte>();
    var lastReport = TimeSpan.Zero;

    while (!stop.IsSet && (seconds == 0 || clock.Elapsed.TotalSeconds < seconds))
    {
        int active = (int)Unsafe.ReadUnaligned<uint>(p + SharedFrameLayout.OffConsumerActive);
        if (active != lastActive)
        {
            Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} consumerActive={active}");
            lastActive = active;
        }

        int w = (int)Unsafe.ReadUnaligned<uint>(p + SharedFrameLayout.OffWidth);
        int h = (int)Unsafe.ReadUnaligned<uint>(p + SharedFrameLayout.OffHeight);
        int stride = (int)Unsafe.ReadUnaligned<uint>(p + SharedFrameLayout.OffStride);
        if (stride == 0) stride = w;
        bool valid = w > 0 && h > 0 && w <= SharedFrameLayout.MaxWidth && h <= SharedFrameLayout.MaxHeight
                     && (w & 1) == 0 && (h & 1) == 0 && stride >= w && (long)stride * h * 3 / 2 <= SharedFrameLayout.MaxFrameBytes;

        if (valid)
        {
            if (w != curW || h != curH || stride != curStride)
            {
                Console.WriteLine($"frame size from header: {w}x{h} stride {stride}");
                bars = BuildBars(w, h, stride);
                curW = w; curH = h; curStride = stride;
            }

            fixed (byte* src = bars)
                Buffer.MemoryCopy(src, frame, SharedFrameLayout.MaxFrameBytes, bars.Length);
            DrawMovingBar(frame, w, h, stride, written);

            Unsafe.WriteUnaligned(p + SharedFrameLayout.OffTimestampQpc, Stopwatch.GetTimestamp()); // QPC ticks
            var counter = Unsafe.ReadUnaligned<ulong>(p + SharedFrameLayout.OffFrameCounter);
            Thread.MemoryBarrier(); // frame bytes before the counter
            Unsafe.WriteUnaligned(p + SharedFrameLayout.OffFrameCounter, counter + 1);
            frameReady?.Set();
            written++;
        }

        if (clock.Elapsed - lastReport >= TimeSpan.FromSeconds(1))
        {
            lastReport = clock.Elapsed;
            Console.WriteLine($"t={clock.Elapsed.TotalSeconds:F0}s written={written} size={(valid ? $"{w}x{h}" : "waiting for consumer size")}");
        }

        // pace at 30 fps
        var next = TimeSpan.FromTicks(period.Ticks * (written + 1));
        var sleep = next - clock.Elapsed;
        if (!valid) sleep = period;
        if (sleep > TimeSpan.Zero) stop.Wait(sleep);
    }
}

// Top 2/3: 75% color bars (BT.601 video range). Bottom 1/3: dark grey strip for the moving bar.
static byte[] BuildBars(int w, int h, int stride)
{
    (byte y, byte u, byte v)[] colors =
    {
        (180, 128, 128), // white
        (162, 44, 142),  // yellow
        (131, 156, 44),  // cyan
        (112, 72, 58),   // green
        (84, 184, 198),  // magenta
        (65, 100, 212),  // red
        (35, 212, 114),  // blue
        (16, 128, 128),  // black
    };
    var buf = new byte[stride * h * 3 / 2];
    int barsBottom = h * 2 / 3;
    for (int row = 0; row < h; row++)
    {
        for (int x = 0; x < w; x++)
            buf[row * stride + x] = row < barsBottom ? colors[Math.Min(7, x * 8 / w)].y : (byte)40;
    }
    int uvBase = stride * h;
    for (int row = 0; row < h / 2; row++)
    {
        for (int x = 0; x < w; x += 2)
        {
            (byte y, byte u, byte v) c = row * 2 < barsBottom ? colors[Math.Min(7, x * 8 / w)] : ((byte)40, (byte)128, (byte)128);
            buf[uvBase + row * stride + x] = c.u;
            buf[uvBase + row * stride + x + 1] = c.v;
        }
    }
    return buf;
}

// A large white bar in the bottom strip whose position is the frame number: it moves 8 px per
// frame, so freezes and lag are visible.
static unsafe void DrawMovingBar(byte* frame, int w, int h, int stride, long frameNumber)
{
    int barW = Math.Max(16, w / 20);
    int x0 = (int)(frameNumber * 8 % Math.Max(1, w - barW));
    int top = h * 2 / 3 + h / 24, bottom = h - h / 24;
    for (int row = top; row < bottom; row++)
        new Span<byte>(frame + (long)row * stride + x0, barW).Fill(235);
}
