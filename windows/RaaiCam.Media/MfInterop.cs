using System.Runtime.InteropServices;

namespace RaaiCam.Media;

/// <summary>Minimal direct COM interop for the Media Foundation transform plumbing (no third-party package).</summary>
internal static class Mf
{
    public const uint MF_VERSION = 0x00020070;

    public const int MF_E_TRANSFORM_NEED_MORE_INPUT = unchecked((int)0xC00D6D72);
    public const int MF_E_TRANSFORM_STREAM_CHANGE = unchecked((int)0xC00D6D61);
    public const int MF_E_NO_MORE_TYPES = unchecked((int)0xC00D36B9);
    public const int MF_E_NOTACCEPTING = unchecked((int)0xC00D36B5);

    public const uint MFT_MESSAGE_COMMAND_FLUSH = 0;
    public const uint MFT_MESSAGE_COMMAND_DRAIN = 1;
    public const uint MFT_MESSAGE_NOTIFY_BEGIN_STREAMING = 0x10000000;
    public const uint MFT_MESSAGE_NOTIFY_END_STREAMING = 0x10000001;
    public const uint MFT_MESSAGE_NOTIFY_END_OF_STREAM = 0x10000002;
    public const uint MFT_MESSAGE_NOTIFY_START_OF_STREAM = 0x10000003;

    public const uint MFT_OUTPUT_STREAM_PROVIDES_SAMPLES = 0x100;
    public const uint MFT_OUTPUT_STREAM_CAN_PROVIDE_SAMPLES = 0x200;

    public static readonly Guid CLSID_CMSH264DecoderMFT = new("62CE7E72-4C71-4D20-B15D-452831A87D9D");
    public static readonly Guid CLSID_MSH264EncoderMFT = new("6CA50344-051A-4DED-9779-A43305165E35");

    public static readonly Guid MF_MT_MAJOR_TYPE = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    public static readonly Guid MF_MT_SUBTYPE = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    public static readonly Guid MF_MT_FRAME_SIZE = new("1652c33d-d6b2-4012-b834-72030849a37d");
    public static readonly Guid MF_MT_FRAME_RATE = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    public static readonly Guid MF_MT_DEFAULT_STRIDE = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
    public static readonly Guid MF_MT_INTERLACE_MODE = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
    public static readonly Guid MF_MT_PIXEL_ASPECT_RATIO = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
    public static readonly Guid MF_MT_AVG_BITRATE = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
    public static readonly Guid MF_MT_MPEG2_PROFILE = new("ad76a80b-2d5c-4e0b-b375-64e520137036");
    public static readonly Guid MF_LOW_LATENCY = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");

    public static readonly Guid MFMediaType_Video = new("73646976-0000-0010-8000-00AA00389B71");
    public static readonly Guid MFVideoFormat_H264 = new("34363248-0000-0010-8000-00AA00389B71");
    public static readonly Guid MFVideoFormat_NV12 = new("3231564E-0000-0010-8000-00AA00389B71");

    [DllImport("mfplat.dll")] public static extern int MFStartup(uint version, uint flags);
    [DllImport("mfplat.dll")] public static extern int MFShutdown();
    [DllImport("mfplat.dll")] public static extern int MFCreateMediaType(out IMFMediaType type);
    [DllImport("mfplat.dll")] public static extern int MFCreateSample(out IMFSample sample);
    [DllImport("mfplat.dll")] public static extern int MFCreateAlignedMemoryBuffer(uint maxLength, uint alignment, out IMFMediaBuffer buffer);

    public static void Check(int hr) { if (hr < 0) Marshal.ThrowExceptionForHR(hr); }

    public static ulong Pack(uint hi, uint lo) => ((ulong)hi << 32) | lo;

    private static readonly object Gate = new();
    private static int _refs;

    /// <summary>Ref-counted MFStartup/MFShutdown. Dispose the returned handle to release.</summary>
    public static IDisposable Startup()
    {
        lock (Gate)
        {
            if (_refs == 0) Check(MFStartup(MF_VERSION, 0));
            _refs++;
        }
        return new Handle();
    }

    private sealed class Handle : IDisposable
    {
        private int _done;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) != 0) return;
            lock (Gate) { if (--_refs == 0) MFShutdown(); }
        }
    }

    public static T CreateMft<T>(Guid clsid) where T : class
    {
        var t = Type.GetTypeFromCLSID(clsid) ?? throw new InvalidOperationException("MFT class not registered");
        return (T)Activator.CreateInstance(t)!;
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct MFT_INPUT_STREAM_INFO { public long hnsMaxLatency; public uint dwFlags; public uint cbSize; public uint cbMaxLookahead; public uint cbAlignment; }

[StructLayout(LayoutKind.Sequential)]
internal struct MFT_OUTPUT_STREAM_INFO { public uint dwFlags; public uint cbSize; public uint cbAlignment; }

[StructLayout(LayoutKind.Sequential)]
internal struct MFT_OUTPUT_DATA_BUFFER { public uint dwStreamID; public IntPtr pSample; public uint dwStatus; public IntPtr pEvents; }

/// <summary>x64 PROPVARIANT (24 bytes) for ICodecAPI.SetValue.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct PropVariant
{
    public ushort vt; public ushort r1; public ushort r2; public ushort r3; public ulong data; public ulong pad;
    public static PropVariant UInt32(uint v) => new() { vt = 19, data = v };
}

[ComImport, Guid("2cd2d921-c447-44a7-a13c-4adabfc247e3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFAttributes
{
    void GetItem(in Guid key, IntPtr pv);
    void GetItemType(in Guid key, out int type);
    void CompareItem(in Guid key, IntPtr pv, [MarshalAs(UnmanagedType.Bool)] out bool result);
    void Compare(IMFAttributes theirs, int matchType, [MarshalAs(UnmanagedType.Bool)] out bool result);
    void GetUINT32(in Guid key, out uint value);
    void GetUINT64(in Guid key, out ulong value);
    void GetDouble(in Guid key, out double value);
    void GetGUID(in Guid key, out Guid value);
    void GetStringLength(in Guid key, out uint length);
    void GetString(in Guid key, IntPtr buf, uint cch, out uint length);
    void GetAllocatedString(in Guid key, out IntPtr buf, out uint length);
    void GetBlobSize(in Guid key, out uint size);
    void GetBlob(in Guid key, IntPtr buf, uint size, out uint written);
    void GetAllocatedBlob(in Guid key, out IntPtr buf, out uint size);
    void GetUnknown(in Guid key, in Guid riid, out IntPtr obj);
    void SetItem(in Guid key, IntPtr pv);
    void DeleteItem(in Guid key);
    void DeleteAllItems();
    void SetUINT32(in Guid key, uint value);
    void SetUINT64(in Guid key, ulong value);
    void SetDouble(in Guid key, double value);
    void SetGUID(in Guid key, in Guid value);
    void SetString(in Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);
    void SetBlob(in Guid key, IntPtr buf, uint size);
    void SetUnknown(in Guid key, IntPtr unk);
    void LockStore();
    void UnlockStore();
    void GetCount(out uint count);
    void GetItemByIndex(uint index, out Guid key, IntPtr pv);
    void CopyAllItems(IMFAttributes dest);
}

[ComImport, Guid("44ae0fa8-ea31-4109-8d2e-4cae4997c555"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFMediaType
{
    void GetItem(in Guid key, IntPtr pv);
    void GetItemType(in Guid key, out int type);
    void CompareItem(in Guid key, IntPtr pv, [MarshalAs(UnmanagedType.Bool)] out bool result);
    void Compare(IMFAttributes theirs, int matchType, [MarshalAs(UnmanagedType.Bool)] out bool result);
    void GetUINT32(in Guid key, out uint value);
    void GetUINT64(in Guid key, out ulong value);
    void GetDouble(in Guid key, out double value);
    void GetGUID(in Guid key, out Guid value);
    void GetStringLength(in Guid key, out uint length);
    void GetString(in Guid key, IntPtr buf, uint cch, out uint length);
    void GetAllocatedString(in Guid key, out IntPtr buf, out uint length);
    void GetBlobSize(in Guid key, out uint size);
    void GetBlob(in Guid key, IntPtr buf, uint size, out uint written);
    void GetAllocatedBlob(in Guid key, out IntPtr buf, out uint size);
    void GetUnknown(in Guid key, in Guid riid, out IntPtr obj);
    void SetItem(in Guid key, IntPtr pv);
    void DeleteItem(in Guid key);
    void DeleteAllItems();
    void SetUINT32(in Guid key, uint value);
    void SetUINT64(in Guid key, ulong value);
    void SetDouble(in Guid key, double value);
    void SetGUID(in Guid key, in Guid value);
    void SetString(in Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);
    void SetBlob(in Guid key, IntPtr buf, uint size);
    void SetUnknown(in Guid key, IntPtr unk);
    void LockStore();
    void UnlockStore();
    void GetCount(out uint count);
    void GetItemByIndex(uint index, out Guid key, IntPtr pv);
    void CopyAllItems(IMFAttributes dest);
    void GetMajorType(out Guid guid);
    [PreserveSig] int IsCompressedFormat([MarshalAs(UnmanagedType.Bool)] out bool compressed);
    [PreserveSig] int IsEqual(IMFMediaType other, out uint flags);
    void GetRepresentation(Guid rep, out IntPtr pv);
    void FreeRepresentation(Guid rep, IntPtr pv);
}

[ComImport, Guid("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFSample
{
    void GetItem(in Guid key, IntPtr pv);
    void GetItemType(in Guid key, out int type);
    void CompareItem(in Guid key, IntPtr pv, [MarshalAs(UnmanagedType.Bool)] out bool result);
    void Compare(IMFAttributes theirs, int matchType, [MarshalAs(UnmanagedType.Bool)] out bool result);
    void GetUINT32(in Guid key, out uint value);
    void GetUINT64(in Guid key, out ulong value);
    void GetDouble(in Guid key, out double value);
    void GetGUID(in Guid key, out Guid value);
    void GetStringLength(in Guid key, out uint length);
    void GetString(in Guid key, IntPtr buf, uint cch, out uint length);
    void GetAllocatedString(in Guid key, out IntPtr buf, out uint length);
    void GetBlobSize(in Guid key, out uint size);
    void GetBlob(in Guid key, IntPtr buf, uint size, out uint written);
    void GetAllocatedBlob(in Guid key, out IntPtr buf, out uint size);
    void GetUnknown(in Guid key, in Guid riid, out IntPtr obj);
    void SetItem(in Guid key, IntPtr pv);
    void DeleteItem(in Guid key);
    void DeleteAllItems();
    void SetUINT32(in Guid key, uint value);
    void SetUINT64(in Guid key, ulong value);
    void SetDouble(in Guid key, double value);
    void SetGUID(in Guid key, in Guid value);
    void SetString(in Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);
    void SetBlob(in Guid key, IntPtr buf, uint size);
    void SetUnknown(in Guid key, IntPtr unk);
    void LockStore();
    void UnlockStore();
    void GetCount(out uint count);
    void GetItemByIndex(uint index, out Guid key, IntPtr pv);
    void CopyAllItems(IMFAttributes dest);
    void GetSampleFlags(out uint flags);
    void SetSampleFlags(uint flags);
    void GetSampleTime(out long time);
    void SetSampleTime(long time);
    void GetSampleDuration(out long duration);
    void SetSampleDuration(long duration);
    void GetBufferCount(out uint count);
    void GetBufferByIndex(uint index, out IMFMediaBuffer buffer);
    void ConvertToContiguousBuffer(out IMFMediaBuffer buffer);
    void AddBuffer(IMFMediaBuffer buffer);
    void RemoveBufferByIndex(uint index);
    void RemoveAllBuffers();
    void GetTotalLength(out uint length);
    void CopyToBuffer(IMFMediaBuffer buffer);
}

[ComImport, Guid("045fa593-8799-42b8-bc8d-8968c6453507"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFMediaBuffer
{
    void Lock(out IntPtr buffer, out uint maxLength, out uint currentLength);
    void Unlock();
    void GetCurrentLength(out uint length);
    void SetCurrentLength(uint length);
    void GetMaxLength(out uint length);
}

[ComImport, Guid("7dc9d5f9-9ed9-44ec-9bbf-0600bb589fbb"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMF2DBuffer
{
    void Lock2D(out IntPtr scanline0, out int pitch);
    void Unlock2D();
    void GetScanline0AndPitch(out IntPtr scanline0, out int pitch);
    void IsContiguousFormat([MarshalAs(UnmanagedType.Bool)] out bool contiguous);
    void GetContiguousLength(out uint length);
    void ContiguousCopyTo(IntPtr dest, uint destLength);
    void ContiguousCopyFrom(IntPtr src, uint srcLength);
}

[ComImport, Guid("bf94c121-5b05-4e6f-8000-ba598961414d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFTransform
{
    [PreserveSig] int GetStreamLimits(out uint inMin, out uint inMax, out uint outMin, out uint outMax);
    [PreserveSig] int GetStreamCount(out uint inputs, out uint outputs);
    [PreserveSig] int GetStreamIDs(uint inSize, IntPtr inIds, uint outSize, IntPtr outIds);
    [PreserveSig] int GetInputStreamInfo(uint id, out MFT_INPUT_STREAM_INFO info);
    [PreserveSig] int GetOutputStreamInfo(uint id, out MFT_OUTPUT_STREAM_INFO info);
    [PreserveSig] int GetAttributes(out IMFAttributes attributes);
    [PreserveSig] int GetInputStreamAttributes(uint id, out IMFAttributes attributes);
    [PreserveSig] int GetOutputStreamAttributes(uint id, out IMFAttributes attributes);
    [PreserveSig] int DeleteInputStream(uint id);
    [PreserveSig] int AddInputStreams(uint count, IntPtr ids);
    [PreserveSig] int GetInputAvailableType(uint id, uint index, out IMFMediaType type);
    [PreserveSig] int GetOutputAvailableType(uint id, uint index, out IMFMediaType type);
    [PreserveSig] int SetInputType(uint id, IMFMediaType? type, uint flags);
    [PreserveSig] int SetOutputType(uint id, IMFMediaType? type, uint flags);
    [PreserveSig] int GetInputCurrentType(uint id, out IMFMediaType type);
    [PreserveSig] int GetOutputCurrentType(uint id, out IMFMediaType type);
    [PreserveSig] int GetInputStatus(uint id, out uint flags);
    [PreserveSig] int GetOutputStatus(out uint flags);
    [PreserveSig] int SetOutputBounds(long lower, long upper);
    [PreserveSig] int ProcessEvent(uint id, IntPtr evt);
    [PreserveSig] int ProcessMessage(uint message, IntPtr param);
    [PreserveSig] int ProcessInput(uint id, IMFSample sample, uint flags);
    [PreserveSig] int ProcessOutput(uint flags, uint count, ref MFT_OUTPUT_DATA_BUFFER buffers, out uint status);
}

[ComImport, Guid("901db4c7-31ce-41a2-85dc-8fa0bf41b8da"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ICodecAPI
{
    [PreserveSig] int IsSupported(in Guid api);
    [PreserveSig] int IsModifiable(in Guid api);
    [PreserveSig] int GetParameterRange(in Guid api, out PropVariant min, out PropVariant max, out PropVariant step);
    [PreserveSig] int GetParameterValues(in Guid api, out IntPtr values, out uint count);
    [PreserveSig] int GetDefaultValue(in Guid api, out PropVariant value);
    [PreserveSig] int GetValue(in Guid api, out PropVariant value);
    [PreserveSig] int SetValue(in Guid api, in PropVariant value);
}
