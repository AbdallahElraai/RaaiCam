using System.Runtime.InteropServices;
using RaaiCam.Media;

namespace RaaiCam.Media.Tests;

internal sealed record EncodedAu(byte[] Data, long Time, bool IsKey);

/// <summary>Test-only H.264 encoder (MS encoder MFT): NV12 in, Annex-B access units out.</summary>
internal sealed class TestH264Encoder : IDisposable
{
    private static readonly Guid AvEncCommonMeanBitRate = new("f7222374-2144-4815-b550-a37f8e12ee52");
    private static readonly Guid AvEncMpvGopSize = new("95f31b26-95a4-41aa-9303-246a7fc6eef1");
    private static readonly Guid AvLowLatencyMode = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");
    private static readonly Guid AvEncMpvDefaultBPictureCount = new("8d390aac-dc5c-4200-b57f-814d04babab2");

    private readonly IDisposable _mf;
    private readonly IMFTransform _mft;
    private readonly int _w, _h;
    private bool _provides;
    private uint _outSize, _outAlign;

    public static bool IsAvailable()
    {
        try { using var e = new TestH264Encoder(64, 64, 1_000_000); return true; }
        catch { return false; }
    }

    public TestH264Encoder(int width, int height, int bitrate, int gop = 30)
    {
        _w = width; _h = height;
        _mf = Mf.Startup();
        try
        {
            _mft = Mf.CreateMft<IMFTransform>(Mf.CLSID_MSH264EncoderMFT);
            var api = (ICodecAPI)_mft;
            api.SetValue(Mf.MF_LOW_LATENCY, PropVariant.UInt32(1));
            api.SetValue(AvEncCommonMeanBitRate, PropVariant.UInt32((uint)bitrate));
            api.SetValue(AvEncMpvGopSize, PropVariant.UInt32((uint)gop));
            api.SetValue(AvEncMpvDefaultBPictureCount, PropVariant.UInt32(0));

            Mf.Check(Mf.MFCreateMediaType(out var outType));
            outType.SetGUID(Mf.MF_MT_MAJOR_TYPE, Mf.MFMediaType_Video);
            outType.SetGUID(Mf.MF_MT_SUBTYPE, Mf.MFVideoFormat_H264);
            outType.SetUINT32(Mf.MF_MT_AVG_BITRATE, (uint)bitrate);
            outType.SetUINT64(Mf.MF_MT_FRAME_SIZE, Mf.Pack((uint)width, (uint)height));
            outType.SetUINT64(Mf.MF_MT_FRAME_RATE, Mf.Pack(30, 1));
            outType.SetUINT64(Mf.MF_MT_PIXEL_ASPECT_RATIO, Mf.Pack(1, 1));
            outType.SetUINT32(Mf.MF_MT_INTERLACE_MODE, 2);
            outType.SetUINT32(Mf.MF_MT_MPEG2_PROFILE, 77); // Main
            Mf.Check(_mft.SetOutputType(0, outType, 0));

            IMFMediaType? nv12 = null;
            for (uint i = 0; ; i++)
            {
                int hr = _mft.GetInputAvailableType(0, i, out var t);
                if (hr < 0) break;
                t.GetGUID(Mf.MF_MT_SUBTYPE, out var sub);
                if (sub == Mf.MFVideoFormat_NV12) { nv12 = t; break; }
            }
            if (nv12 == null) throw new InvalidOperationException("encoder offers no NV12 input");
            nv12.SetUINT64(Mf.MF_MT_FRAME_SIZE, Mf.Pack((uint)width, (uint)height));
            nv12.SetUINT64(Mf.MF_MT_FRAME_RATE, Mf.Pack(30, 1));
            nv12.SetUINT64(Mf.MF_MT_PIXEL_ASPECT_RATIO, Mf.Pack(1, 1));
            nv12.SetUINT32(Mf.MF_MT_INTERLACE_MODE, 2);
            Mf.Check(_mft.SetInputType(0, nv12, 0));

            Mf.Check(_mft.GetOutputStreamInfo(0, out var osi));
            _provides = (osi.dwFlags & (Mf.MFT_OUTPUT_STREAM_PROVIDES_SAMPLES | Mf.MFT_OUTPUT_STREAM_CAN_PROVIDE_SAMPLES)) != 0;
            _outSize = osi.cbSize; _outAlign = osi.cbAlignment;

            Mf.Check(_mft.ProcessMessage(Mf.MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, IntPtr.Zero));
            Mf.Check(_mft.ProcessMessage(Mf.MFT_MESSAGE_NOTIFY_START_OF_STREAM, IntPtr.Zero));
        }
        catch { _mf.Dispose(); throw; }
    }

    /// <summary>Encodes one tightly packed NV12 frame; returns zero or more completed access units.</summary>
    public List<EncodedAu> Encode(ReadOnlySpan<byte> nv12, long time100ns)
    {
        Mf.Check(Mf.MFCreateAlignedMemoryBuffer((uint)nv12.Length, 15, out var buf));
        buf.Lock(out var p, out _, out _);
        Marshal.Copy(nv12.ToArray(), 0, p, nv12.Length);
        buf.Unlock();
        buf.SetCurrentLength((uint)nv12.Length);
        Mf.Check(Mf.MFCreateSample(out var s));
        s.AddBuffer(buf);
        s.SetSampleTime(time100ns);
        s.SetSampleDuration(333333);
        Mf.Check(_mft.ProcessInput(0, s, 0));
        Marshal.ReleaseComObject(s); Marshal.ReleaseComObject(buf);
        return Drain();
    }

    public List<EncodedAu> Flush()
    {
        _mft.ProcessMessage(Mf.MFT_MESSAGE_NOTIFY_END_OF_STREAM, IntPtr.Zero);
        _mft.ProcessMessage(Mf.MFT_MESSAGE_COMMAND_DRAIN, IntPtr.Zero);
        return Drain();
    }

    private List<EncodedAu> Drain()
    {
        var list = new List<EncodedAu>();
        while (true)
        {
            var odb = new MFT_OUTPUT_DATA_BUFFER();
            IMFSample? mine = null;
            if (!_provides)
            {
                Mf.Check(Mf.MFCreateSample(out mine));
                Mf.Check(Mf.MFCreateAlignedMemoryBuffer(Math.Max(_outSize, 1 << 20), Math.Max(_outAlign, 1) - 1, out var ob));
                mine.AddBuffer(ob);
                odb.pSample = Marshal.GetIUnknownForObject(mine);
            }
            int hr = _mft.ProcessOutput(0, 1, ref odb, out _);
            IMFSample? got = null;
            if (odb.pSample != IntPtr.Zero)
            {
                got = (IMFSample)Marshal.GetObjectForIUnknown(odb.pSample);
                Marshal.Release(odb.pSample);
            }
            if (hr == Mf.MF_E_TRANSFORM_NEED_MORE_INPUT) { if (got != null) Marshal.ReleaseComObject(got); break; }
            Mf.Check(hr);
            got!.ConvertToContiguousBuffer(out var cb);
            cb.Lock(out var p, out _, out var len);
            var data = new byte[len];
            Marshal.Copy(p, data, 0, (int)len);
            cb.Unlock();
            got.GetSampleTime(out var t);
            Marshal.ReleaseComObject(cb); Marshal.ReleaseComObject(got);
            data = EnsureAnnexB(data);
            list.Add(new EncodedAu(data, t, ContainsIdr(data)));
        }
        return list;
    }

    private static bool HasStartCode(byte[] d) =>
        d.Length >= 4 && d[0] == 0 && d[1] == 0 && (d[2] == 1 || (d[2] == 0 && d[3] == 1));

    private static byte[] EnsureAnnexB(byte[] d)
    {
        if (HasStartCode(d)) return d;
        throw new InvalidOperationException("encoder output is not Annex-B");
    }

    internal static bool ContainsIdr(byte[] d)
    {
        for (int i = 0; i + 3 < d.Length; i++)
            if (d[i] == 0 && d[i + 1] == 0 && d[i + 2] == 1 && (d[i + 3] & 0x1F) == 5) return true;
        return false;
    }

    public void Dispose()
    {
        try { Marshal.ReleaseComObject(_mft); } catch { }
        _mf.Dispose();
    }
}
