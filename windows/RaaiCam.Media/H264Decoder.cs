using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RaaiCam.Media;

public enum DecodeResult { Frame, NeedMoreInput, Error }

/// <summary>
/// H.264 Annex-B access unit decoder on the Microsoft H.264 decoder MFT. System-memory NV12 output, cropped to
/// exactly width x height (tight stride = width). Never throws from <see cref="Decode"/> for bad input; instead it
/// returns <see cref="DecodeResult.Error"/>/<see cref="DecodeResult.NeedMoreInput"/>, raises
/// <see cref="KeyframeNeeded"/> (at most once per 500 ms) and waits for the next IDR.
/// Not thread-safe: call Decode from one thread.
/// </summary>
public sealed class H264Decoder : IDisposable
{
    private const long SampleDuration100ns = 333333;
    private static readonly TimeSpan KeyframeRequestInterval = TimeSpan.FromMilliseconds(500);

    private readonly int _w, _h;
    private readonly IDisposable _mf;
    private IMFTransform? _mft;

    private bool _haveKey;
    private long _lastRequestTicks;
    private bool _everRequested;

    private bool _providesSamples;
    private uint _outSize, _outAlign;
    private IMFSample? _ownSample;
    private IMFMediaBuffer? _ownBuffer;
    private int _typeStride;     // MF_MT_DEFAULT_STRIDE of the negotiated output type (0 = unknown)
    private uint _typeW, _typeH; // MF_MT_FRAME_SIZE of the negotiated output type

    /// <summary>Raised (throttled) when the decoder needs the sender to produce a keyframe.</summary>
    public event Action? KeyframeNeeded;

    /// <summary>Row pitch in bytes of the last decoded MFT buffer (diagnostic).</summary>
    /// <summary>Last exception swallowed by Decode (diagnostic).</summary>
    internal Exception? LastError { get; private set; }
    internal string Diag => $"provides={_providesSamples} outSize={_outSize} align={_outAlign} type={_typeW}x{_typeH} stride={_typeStride}";
    internal int LastSourceStride { get; private set; }
    /// <summary>Row count of the last decoded MFT buffer including padding (diagnostic).</summary>
    internal int LastPaddedHeight { get; private set; }

    public H264Decoder(int width, int height)
    {
        if (width <= 0 || height <= 0 || (width & 1) != 0 || (height & 1) != 0)
            throw new ArgumentOutOfRangeException(nameof(width), "width/height must be positive and even");
        _w = width; _h = height;
        _mf = Mf.Startup();
        try
        {
            _mft = Mf.CreateMft<IMFTransform>(Mf.CLSID_CMSH264DecoderMFT);

            // Low latency must be set before the types.
            if (_mft.GetAttributes(out var attrs) >= 0) attrs.SetUINT32(Mf.MF_LOW_LATENCY, 1);

            Mf.Check(Mf.MFCreateMediaType(out var inType));
            inType.SetGUID(Mf.MF_MT_MAJOR_TYPE, Mf.MFMediaType_Video);
            inType.SetGUID(Mf.MF_MT_SUBTYPE, Mf.MFVideoFormat_H264);
            inType.SetUINT64(Mf.MF_MT_FRAME_SIZE, Mf.Pack((uint)width, (uint)height));
            inType.SetUINT64(Mf.MF_MT_FRAME_RATE, Mf.Pack(30, 1));
            inType.SetUINT64(Mf.MF_MT_PIXEL_ASPECT_RATIO, Mf.Pack(1, 1));
            inType.SetUINT32(Mf.MF_MT_INTERLACE_MODE, 2);
            Mf.Check(_mft.SetInputType(0, inType, 0));
            Marshal.ReleaseComObject(inType);

            SelectOutputType();
            Mf.Check(_mft.ProcessMessage(Mf.MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, IntPtr.Zero));
            Mf.Check(_mft.ProcessMessage(Mf.MFT_MESSAGE_NOTIFY_START_OF_STREAM, IntPtr.Zero));
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public DecodeResult Decode(ReadOnlySpan<byte> annexBAccessUnit, long sampleTime100ns, Span<byte> nv12Out, out int outStride)
    {
        outStride = _w;
        if (_mft == null || nv12Out.Length < _w * _h * 3 / 2) return DecodeResult.Error;

        try
        {
            if (!ScanAnnexB(annexBAccessUnit, out bool hasIdr))
                return Fail();

            if (!_haveKey && !hasIdr)
            {
                RaiseKeyframeNeeded();
                return DecodeResult.NeedMoreInput;
            }
            if (hasIdr) _haveKey = true;

            Feed(annexBAccessUnit, sampleTime100ns);
            return DrainLatest(nv12Out) ? DecodeResult.Frame : DecodeResult.NeedMoreInput;
        }
        catch (Exception ex)
        {
            LastError = ex;
            return Fail();
        }
    }

    /// <summary>Error path: flush the MFT, wait for a keyframe, ask for one (throttled).</summary>
    private DecodeResult Fail()
    {
        _haveKey = false;
        try { _mft?.ProcessMessage(Mf.MFT_MESSAGE_COMMAND_FLUSH, IntPtr.Zero); } catch { }
        RaiseKeyframeNeeded();
        return DecodeResult.Error;
    }

    private void RaiseKeyframeNeeded()
    {
        long now = Stopwatch.GetTimestamp();
        if (_everRequested && Stopwatch.GetElapsedTime(_lastRequestTicks, now) < KeyframeRequestInterval) return;
        _everRequested = true;
        _lastRequestTicks = now;
        try { KeyframeNeeded?.Invoke(); } catch { /* a subscriber must not break decoding */ }
    }

    /// <summary>
    /// Validates the Annex-B structure (start code present, every NAL header has forbidden_zero_bit = 0 and a
    /// defined type) and reports whether an IDR slice (nal_unit_type 5) is present.
    /// </summary>
    internal static bool ScanAnnexB(ReadOnlySpan<byte> d, out bool hasIdr)
    {
        hasIdr = false;
        int nals = 0;
        for (int i = 0; i + 3 < d.Length; i++)
        {
            if (d[i] != 0 || d[i + 1] != 0 || d[i + 2] != 1) continue;
            byte hdr = d[i + 3];
            if ((hdr & 0x80) != 0) return false;
            int type = hdr & 0x1F;
            if (type == 0 || type > 23) return false;
            if (type == 5) hasIdr = true;
            nals++;
            i += 3;
        }
        // The first bytes must be a start code (optionally preceded by zero padding).
        int first = 0;
        while (first < d.Length && d[first] == 0) first++;
        return nals > 0 && first >= 2 && first < d.Length && d[first] == 1;
    }

    private unsafe void Feed(ReadOnlySpan<byte> au, long time)
    {
        Mf.Check(Mf.MFCreateAlignedMemoryBuffer((uint)au.Length, 15, out var buf));
        IMFSample? sample = null;
        try
        {
            buf.Lock(out var p, out _, out _);
            try { au.CopyTo(new Span<byte>((void*)p, au.Length)); }
            finally { buf.Unlock(); }
            buf.SetCurrentLength((uint)au.Length);
            Mf.Check(Mf.MFCreateSample(out sample));
            sample.AddBuffer(buf);
            sample.SetSampleTime(time);
            sample.SetSampleDuration(SampleDuration100ns);

            int hr = _mft!.ProcessInput(0, sample, 0);
            if (hr == Mf.MF_E_NOTACCEPTING)
            {
                // Output is pending; make room and retry once.
                DrainLatest(Span<byte>.Empty);
                hr = _mft.ProcessInput(0, sample, 0);
            }
            Mf.Check(hr);
        }
        finally
        {
            if (sample != null) Marshal.ReleaseComObject(sample);
            Marshal.ReleaseComObject(buf);
        }
    }

    /// <summary>Pulls every available output frame; the latest is cropped into <paramref name="dst"/> (empty = discard).</summary>
    private bool DrainLatest(Span<byte> dst)
    {
        bool got = false;
        for (int guard = 0; guard < 64; guard++)
        {
            var odb = new MFT_OUTPUT_DATA_BUFFER();
            if (!_providesSamples)
            {
                _ownSample ??= AllocOutputSample();
                _ownSample.RemoveAllBuffers(); _ownSample.AddBuffer(_ownBuffer!);
                _ownBuffer!.SetCurrentLength(0);
                odb.pSample = Marshal.GetIUnknownForObject(_ownSample);
            }
            int hr = _mft!.ProcessOutput(0, 1, ref odb, out _);
            IMFSample? sample = null;
            if (odb.pSample != IntPtr.Zero)
            {
                sample = (IMFSample)Marshal.GetObjectForIUnknown(odb.pSample);
                Marshal.Release(odb.pSample);
            }
            if (odb.pEvents != IntPtr.Zero) Marshal.Release(odb.pEvents);

            if (hr == Mf.MF_E_TRANSFORM_NEED_MORE_INPUT) { ReleaseIfProvided(sample); break; }
            if (hr == Mf.MF_E_TRANSFORM_STREAM_CHANGE)
            {
                ReleaseIfProvided(sample);
                SelectOutputType();
                continue;
            }
            Mf.Check(hr);
            try
            {
                if (!dst.IsEmpty) CopyCropped(sample!, dst);
                got = true;
            }
            finally { ReleaseIfProvided(sample); }
        }
        return got;
    }

    private void ReleaseIfProvided(IMFSample? s)
    {
        // Our own cached sample stays alive; MFT-provided samples are released each time.
        if (s != null && !ReferenceEquals(s, _ownSample) && _providesSamples) Marshal.ReleaseComObject(s);
    }

    private IMFSample AllocOutputSample()
    {
        Mf.Check(Mf.MFCreateSample(out var s));
        Mf.Check(Mf.MFCreateAlignedMemoryBuffer(Math.Max(_outSize, (uint)(_w * _h * 3 / 2)), Math.Max(_outAlign, 1u) - 1, out var b));
        s.AddBuffer(b); _ownBuffer = b;

        return s;
    }

    private unsafe void CopyCropped(IMFSample sample, Span<byte> dst)
    {
        sample.ConvertToContiguousBuffer(out var mb);
        IMF2DBuffer? b2d = mb as IMF2DBuffer;
        bool locked2D = false, locked1D = false;
        try
        {
            IntPtr basePtr; int pitch;
            mb.GetCurrentLength(out uint curLen);
            if (b2d != null)
            {
                b2d.Lock2D(out basePtr, out pitch);
                locked2D = true;
            }
            else
            {
                mb.Lock(out basePtr, out _, out _);
                locked1D = true;
                pitch = _typeStride != 0 ? _typeStride : (int)_typeW;
            }
            if (pitch <= 0) throw new InvalidOperationException("bottom-up or unknown stride not supported");
            if (pitch < _w) throw new InvalidOperationException("stride smaller than width");

            int paddedH = curLen > 0 ? (int)((long)curLen * 2 / 3 / pitch) : 0;
            if (paddedH < _h) paddedH = (_h + 15) & ~15; // length didn't tell: assume 16-row macroblock alignment
            LastSourceStride = pitch; LastPaddedHeight = paddedH;

            byte* src = (byte*)basePtr;
            for (int r = 0; r < _h; r++)
                new ReadOnlySpan<byte>(src + (long)r * pitch, _w).CopyTo(dst.Slice(r * _w, _w));
            byte* uv = src + (long)pitch * paddedH;
            int uvDst = _w * _h;
            for (int r = 0; r < _h / 2; r++)
                new ReadOnlySpan<byte>(uv + (long)r * pitch, _w).CopyTo(dst.Slice(uvDst + r * _w, _w));
        }
        finally
        {
            if (locked2D) b2d!.Unlock2D();
            if (locked1D) mb.Unlock();
            Marshal.ReleaseComObject(mb);
        }
    }

    /// <summary>Selects the NV12 output type and caches its geometry and stream info.</summary>
    private void SelectOutputType()
    {
        IMFMediaType? chosen = null;
        for (uint i = 0; ; i++)
        {
            int hr = _mft!.GetOutputAvailableType(0, i, out var t);
            if (hr == Mf.MF_E_NO_MORE_TYPES) break;
            Mf.Check(hr);
            t.GetGUID(Mf.MF_MT_SUBTYPE, out var sub);
            if (sub == Mf.MFVideoFormat_NV12) { chosen = t; break; }
            Marshal.ReleaseComObject(t);
        }
        if (chosen == null) throw new InvalidOperationException("decoder offers no NV12 output");
        Mf.Check(_mft!.SetOutputType(0, chosen, 0));
        Marshal.ReleaseComObject(chosen);

        Mf.Check(_mft.GetOutputCurrentType(0, out var cur));
        try
        {
            cur.GetUINT64(Mf.MF_MT_FRAME_SIZE, out ulong fs);
            _typeW = (uint)(fs >> 32); _typeH = (uint)fs;
            try { cur.GetUINT32(Mf.MF_MT_DEFAULT_STRIDE, out uint s); _typeStride = (int)s; }
            catch (COMException) { _typeStride = 0; }
        }
        finally { Marshal.ReleaseComObject(cur); }
        if (_typeW != (uint)_w || _typeH < (uint)_h)
            throw new InvalidOperationException($"decoded size {_typeW}x{_typeH} does not match requested {_w}x{_h}");

        Mf.Check(_mft.GetOutputStreamInfo(0, out var osi));
        _providesSamples = (osi.dwFlags & Mf.MFT_OUTPUT_STREAM_PROVIDES_SAMPLES) != 0;
        _outSize = osi.cbSize; _outAlign = osi.cbAlignment;
        ReleaseOwn();
    }

    public void Dispose()
    {
        ReleaseOwn();
        if (_mft != null)
        {
            try { _mft.ProcessMessage(Mf.MFT_MESSAGE_NOTIFY_END_STREAMING, IntPtr.Zero); } catch { }
            try { Marshal.ReleaseComObject(_mft); } catch { }
            _mft = null;
            _mf.Dispose();
        }
        else if (!_mfDisposed) { _mf.Dispose(); }
        _mfDisposed = true;
    }

    private bool _mfDisposed;

    private void ReleaseOwn()
    {
        try { if (_ownSample != null) Marshal.ReleaseComObject(_ownSample); if (_ownBuffer != null) Marshal.ReleaseComObject(_ownBuffer); } catch { }
        _ownSample = null; _ownBuffer = null;
    }
}
