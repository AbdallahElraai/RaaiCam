using System.Diagnostics;
using System.IO.MemoryMappedFiles;

namespace RaaiCam.Media;

public sealed record ConsumerState(bool Active, int RequestedWidth, int RequestedHeight, ulong Generation);

/// <summary>
/// Writer side of the shared-frame mapping (protocol/SHARED_FRAME.md). Opens the mapping created by the
/// virtual camera DLL; never creates it. Callers poll <see cref="ReadConsumerState"/>.
/// Single-writer assumption: use exactly one sink instance per mapping (the seqlock and frameId are per instance).
/// </summary>
public sealed unsafe class VirtualCameraSink : IDisposable
{
    private const uint NoSlot = 0xFFFFFFFF;

    private readonly string _mappingName;
    private readonly Func<long> _qpcNow;
    private readonly object _lock = new();

    private MemoryMappedFile? _mmf;
    private MemoryMappedViewAccessor? _view;
    private byte* _base;
    private ulong _openGeneration;
    private ulong _frameId;
    private int _lastSlot = -1;
    private bool _disposed;
    private long _lastAttemptQpc;
    private bool _everAttempted;

    private static readonly long RetryIntervalQpc = Stopwatch.Frequency / 2; // 500 ms

    /// <summary>Successful reopens performed by the generation-change / self-healing logic (test observability).</summary>
    internal int ReopenCount { get; private set; }

    /// <summary>Number of times the mapping open was actually attempted (test observability).</summary>
    internal int OpenAttempts { get; private set; }

    public VirtualCameraSink(string mappingName = SharedFrameLayout.MappingName, Func<long>? qpcNow = null)
    {
        _mappingName = mappingName;
        _qpcNow = qpcNow ?? Stopwatch.GetTimestamp;
    }

    public bool TryOpen()
    {
        lock (_lock) return TryOpenLocked();
    }

    public ConsumerState? ReadConsumerState()
    {
        lock (_lock)
        {
            if (!EnsureCurrentLocked()) return null;
            return ReadStateLocked();
        }
    }

    /// <summary>
    /// Writes one NV12 frame. <paramref name="timestampQpc"/> must be the host <c>Stopwatch.GetTimestamp()</c> (QPC)
    /// value taken at write time: the reader only accepts frames at most 500 ms old and at most 1 s in the future
    /// (protocol/SHARED_FRAME.md). The frame must be exactly the size the consumer requested; the sink does no scaling,
    /// so the compositor has to render at the requested size.
    /// </summary>
    public bool WriteFrame(ReadOnlySpan<byte> nv12, int width, int height, int stride, long timestampQpc)
    {
        lock (_lock)
        {
            if (!EnsureCurrentLocked()) return false;

            var state = ReadStateLocked();
            if (!state.Active || state.RequestedWidth <= 0 || state.RequestedHeight <= 0) return false;
            if (width != state.RequestedWidth || height != state.RequestedHeight) return false;
            if (width <= 0 || height <= 0 || stride < width) return false;
            long frameBytes = (long)stride * height * 3 / 2;
            if (frameBytes > SharedFrameLayout.MaxFrameBytes || nv12.Length < frameBytes) return false;

            uint latest = Volatile.Read(ref *(uint*)(_base + SharedFrameLayout.OffLatestSlot));
            int slot = PickSlot(latest);
            byte* s = _base + SharedFrameLayout.SlotOff(slot);

            ulong* seq = (ulong*)(s + SharedFrameLayout.SlotOffSeq);
            ulong cur = Volatile.Read(ref *seq);
            ulong odd = (cur | 1UL) == cur ? cur + 2 : cur + 1; // next odd value
            Volatile.Write(ref *seq, odd);
            Interlocked.MemoryBarrier();

            *(uint*)(s + SharedFrameLayout.SlotOffWidth) = (uint)width;
            *(uint*)(s + SharedFrameLayout.SlotOffHeight) = (uint)height;
            *(uint*)(s + SharedFrameLayout.SlotOffStride) = (uint)stride;
            *(uint*)(s + SharedFrameLayout.SlotOffFormat) = (uint)SharedFrameLayout.FormatNv12;
            *(long*)(s + SharedFrameLayout.SlotOffTimestamp) = timestampQpc;
            *(ulong*)(s + SharedFrameLayout.SlotOffFrameId) = ++_frameId;
            nv12.Slice(0, (int)frameBytes).CopyTo(new Span<byte>(s + SharedFrameLayout.SlotHeaderBytes, (int)frameBytes));

            Interlocked.MemoryBarrier();
            Volatile.Write(ref *seq, odd + 1);

            *(uint*)(_base + SharedFrameLayout.OffWriterPid) = (uint)Environment.ProcessId;
            Volatile.Write(ref *(uint*)(_base + SharedFrameLayout.OffLatestSlot), (uint)slot);
            Volatile.Write(ref *(long*)(_base + SharedFrameLayout.OffWriterHeartbeat), _qpcNow());
            _lastSlot = slot;
            return true;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            CloseLocked();
        }
    }

    // Round-robin among the two slots other than latest; first write (no latest) uses slot 0.
    private int PickSlot(uint latest)
    {
        if (latest >= SharedFrameLayout.SlotCount) return 0;
        int s = (_lastSlot + 1) % SharedFrameLayout.SlotCount;
        if (s == (int)latest) s = (s + 1) % SharedFrameLayout.SlotCount;
        return s;
    }

    private ConsumerState ReadStateLocked()
    {
        ulong gen = Volatile.Read(ref *(ulong*)(_base + SharedFrameLayout.OffConsumerGeneration));
        uint active = Volatile.Read(ref *(uint*)(_base + SharedFrameLayout.OffConsumerActive));
        uint w = Volatile.Read(ref *(uint*)(_base + SharedFrameLayout.OffRequestedWidth));
        uint h = Volatile.Read(ref *(uint*)(_base + SharedFrameLayout.OffRequestedHeight));
        return new ConsumerState(active != 0, (int)w, (int)h, gen);
    }

    // If the generation changed (or is 0), drop the view and reopen. False when not open afterwards.
    private bool EnsureCurrentLocked()
    {
        if (_disposed) return false;
        if (_base == null)
        {
            // Closed (failed reopen or never opened): retry, throttled to once per 500 ms.
            if (_everAttempted && _qpcNow() - _lastAttemptQpc < RetryIntervalQpc) return false;
            return ReopenLocked();
        }
        ulong gen = Volatile.Read(ref *(ulong*)(_base + SharedFrameLayout.OffConsumerGeneration));
        if (gen != 0 && gen == _openGeneration) return true;
        CloseLocked();
        return ReopenLocked();
    }

    private bool ReopenLocked()
    {
        bool ok = TryOpenLocked();
        if (ok) ReopenCount++;
        return ok;
    }

    private bool TryOpenLocked()
    {
        if (_disposed) return false;
        if (_base != null) return true;
        _everAttempted = true;
        _lastAttemptQpc = _qpcNow();
        OpenAttempts++;
        MemoryMappedFile? mmf = null;
        MemoryMappedViewAccessor? view = null;
        bool acquired = false;
        try
        {
            mmf = MemoryMappedFile.OpenExisting(_mappingName, MemoryMappedFileRights.ReadWrite);
            view = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.ReadWrite);
            if (view.Capacity < SharedFrameLayout.MappingBytes) throw new InvalidDataException();

            byte* p = null;
            view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
            acquired = true;
            p += view.PointerOffset;

            if (*(uint*)p != SharedFrameLayout.Magic ||
                *(uint*)(p + 4) != (uint)SharedFrameLayout.Version ||
                *(uint*)(p + 8) != SharedFrameLayout.HeaderBytes ||
                *(uint*)(p + 12) != SharedFrameLayout.SlotCount ||
                *(uint*)(p + 16) != SharedFrameLayout.SlotStride ||
                *(uint*)(p + SharedFrameLayout.OffMaxWidth) != 1920 ||
                *(uint*)(p + SharedFrameLayout.OffMaxHeight) != 1080)
                throw new InvalidDataException();

            ulong gen = Volatile.Read(ref *(ulong*)(p + SharedFrameLayout.OffConsumerGeneration));
            if (gen == 0) throw new InvalidDataException();

            _mmf = mmf;
            _view = view;
            _base = p;
            _openGeneration = gen;

            // Continue frameIds past anything already in the slots, so a new sink in the same process
            // (or after a reopen) is not rejected by the reader's "frameId must exceed last good" rule.
            for (int i = 0; i < SharedFrameLayout.SlotCount; i++)
            {
                ulong id = Volatile.Read(ref *(ulong*)(p + SharedFrameLayout.SlotOff(i) + SharedFrameLayout.SlotOffFrameId));
                if (id > _frameId) _frameId = id;
            }
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException
                                      or ArgumentException or FileNotFoundException)
        {
            if (acquired) view!.SafeMemoryMappedViewHandle.ReleasePointer();
            view?.Dispose();
            mmf?.Dispose();
            return false;
        }
    }

    private void CloseLocked()
    {
        if (_view != null)
        {
            if (_base != null) _view.SafeMemoryMappedViewHandle.ReleasePointer();
            _view.Dispose();
        }
        _mmf?.Dispose();
        _view = null;
        _mmf = null;
        _base = null;
        _openGeneration = 0;
    }
}
