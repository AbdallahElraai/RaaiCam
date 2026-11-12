using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace RaaiCam.Media;

/// <summary>
/// Fixed-rate pacing against absolute Stopwatch deadlines. Waits on a high-resolution waitable timer
/// (no spinning); falls back to timeBeginPeriod(1) + Sleep when the flag is unsupported.
/// Resyncs (drops the backlog) when more than 100 ms behind.
/// </summary>
public sealed class FramePacer : IDisposable
{
    private const uint CreateWaitableTimerHighResolution = 0x2;
    private const uint TimerAllAccess = 0x1F0003;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeWaitHandle CreateWaitableTimerExW(IntPtr attrs, string? name, uint flags, uint access);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWaitableTimer(SafeWaitHandle timer, ref long dueTime, int period, IntPtr completion, IntPtr arg, [MarshalAs(UnmanagedType.Bool)] bool resume);

    [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint ms);
    [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint ms);

    private readonly long _intervalTicks;
    private readonly long _resyncTicks = Stopwatch.Frequency / 10;
    private readonly SafeWaitHandle? _timer;
    private readonly ManualResetEvent? _timerWait;
    private bool _periodRaised;
    private bool _disposed;
    private long _next;
    private bool _started;

    public FramePacer(double fps)
    {
        if (fps <= 0) throw new ArgumentOutOfRangeException(nameof(fps));
        _intervalTicks = (long)(Stopwatch.Frequency / fps);
        var t = CreateWaitableTimerExW(IntPtr.Zero, null, CreateWaitableTimerHighResolution, TimerAllAccess);
        if (t.IsInvalid)
        {
            t.Dispose();
            _periodRaised = timeBeginPeriod(1) == 0; // TIMERR_NOERROR; only then is timeEndPeriod owed
        }
        else
        {
            _timer = t;
            _timerWait = new ManualResetEvent(false) { SafeWaitHandle = t };
        }
    }

    /// <summary>Waits for the next frame deadline. Returns false when cancelled.</summary>
    public bool WaitNext(CancellationToken ct)
    {
        long now = Stopwatch.GetTimestamp();
        if (!_started) { _started = true; _next = now; }
        _next += _intervalTicks;
        if (now - _next > _resyncTicks) _next = now + _intervalTicks; // fell far behind: resync, don't burst

        while (!ct.IsCancellationRequested)
        {
            long remaining = _next - Stopwatch.GetTimestamp();
            if (remaining <= 0) return true;
            if (_timer != null)
            {
                long due = -(remaining * 10_000_000L / Stopwatch.Frequency); // relative, 100 ns units
                if (due == 0) return true;
                if (!SetWaitableTimer(_timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, false)) return SpinTo(ct);
                WaitHandle.WaitAny(new WaitHandle[] { _timerWait!, ct.WaitHandle });
            }
            else
            {
                int ms = (int)(remaining * 1000 / Stopwatch.Frequency);
                if (ms > 0) ct.WaitHandle.WaitOne(ms);
                else Thread.Yield();
            }
        }
        return false;
    }

    private bool SpinTo(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (Stopwatch.GetTimestamp() >= _next) return true;
            Thread.Yield();
        }
        return false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timerWait?.Dispose(); // also releases the underlying handle
        _timer?.Dispose();
        if (_periodRaised) { _periodRaised = false; timeEndPeriod(1); }
    }
}
