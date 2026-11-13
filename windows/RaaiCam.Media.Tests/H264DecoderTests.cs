using System.Diagnostics;
using Xunit;
using Xunit.Abstractions;
using RaaiCam.Media;

namespace RaaiCam.Media.Tests;

public class H264DecoderTests
{
    private readonly ITestOutputHelper _out;
    public H264DecoderTests(ITestOutputHelper o) => _out = o;

    private static List<EncodedAu> Encode(int w, int h, int frames, int bitrate)
    {
        var src = new TestPatternSource();
        var frame = new byte[w * h * 3 / 2];
        var list = new List<EncodedAu>();
        using var enc = new TestH264Encoder(w, h, bitrate);
        for (int i = 0; i < frames; i++)
        {
            src.Render(frame, w, h, w, (ulong)i);
            list.AddRange(enc.Encode(frame, i * 333333L));
        }
        list.AddRange(enc.Flush());
        return list;
    }

    private bool EncoderUnavailable()
    {
        if (TestH264Encoder.IsAvailable()) return false;
        _out.WriteLine("SKIPPED: MS H.264 encoder MFT unavailable on this machine.");
        return true;
    }

    [Fact]
    public void Decoder_RoundTrip_FrameIdsSurvive()
    {
        if (EncoderUnavailable()) return;
        const int W = 1280, H = 720;
        var aus = Encode(W, H, 30, 8_000_000);
        Assert.Equal(30, aus.Count);
        Assert.True(aus[0].IsKey);

        using var dec = new H264Decoder(W, H);
        var outBuf = new byte[W * H * 3 / 2];
        int ok = 0; var ms = new List<double>();
        foreach (var au in aus)
        {
            var sw = Stopwatch.StartNew();
            var r = dec.Decode(au.Data, au.Time, outBuf, out int stride);
            ms.Add(sw.Elapsed.TotalMilliseconds);
            if (r != DecodeResult.Frame) continue;
            Assert.Equal(W, stride);
            ulong id = TestPatternSource.DecodeId(outBuf.AsSpan(0, W));
            if (id == (ulong)(au.Time / 333333)) ok++;
        }
        _out.WriteLine($"720p decode ms: min {ms.Min():F2} avg {ms.Average():F2} max {ms.Max():F2}; ids ok {ok}/30; stride {dec.LastSourceStride} paddedH {dec.LastPaddedHeight}");
        Assert.True(ok >= 28, $"only {ok} of 30 frame ids survived");
    }

    [Fact]
    public void Decoder_OutputIsExactSize_1080p()
    {
        if (EncoderUnavailable()) return;
        const int W = 1920, H = 1080;
        var aus = Encode(W, H, 10, 8_000_000);
        using var dec = new H264Decoder(W, H);
        // Poison the buffer so unwritten bytes cannot pass as pattern.
        var outBuf = new byte[W * H * 3 / 2 + 4096];
        Array.Fill(outBuf, (byte)0x77);
        int frames = 0; var ms = new List<double>();
        foreach (var au in aus)
        {
            var sw = Stopwatch.StartNew();
            var r = dec.Decode(au.Data, au.Time, outBuf, out int stride);
            ms.Add(sw.Elapsed.TotalMilliseconds);
            if (r != DecodeResult.Frame) continue;
            frames++;
            Assert.Equal(W, stride);
            ulong id = (ulong)(au.Time / 333333);
            Assert.Equal(id, TestPatternSource.DecodeId(outBuf.AsSpan(0, W)));
            Assert.Equal(id, TestPatternSource.DecodeId(outBuf.AsSpan((H - 1) * W, W))); // last visible row is pattern
            Assert.Equal(0x77, outBuf[W * H * 3 / 2]);                                   // nothing written past the frame
            // UV plane starts right after the visible Y rows and is neutral/bars, not poison.
            Assert.NotEqual(0x77, outBuf[W * H + 5]);
            Assert.NotEqual(0x77, outBuf[W * H * 3 / 2 - 2]);
        }
        _out.WriteLine($"1080p decode ms: min {ms.Min():F2} avg {ms.Average():F2} max {ms.Max():F2}; source stride {dec.LastSourceStride} paddedH {dec.LastPaddedHeight}");
        Assert.True(frames >= 8, $"only {frames} frames decoded");
    }

    [Fact]
    public void Decoder_CorruptAccessUnit_NoThrow_RequestsKeyframe()
    {
        if (EncoderUnavailable()) return;
        const int W = 1280, H = 720;
        var aus = Encode(W, H, 3, 8_000_000);
        using var dec = new H264Decoder(W, H);
        int requests = 0;
        dec.KeyframeNeeded += () => requests++;
        var outBuf = new byte[W * H * 3 / 2];
        Assert.Equal(DecodeResult.Frame, dec.Decode(aus[0].Data, 0, outBuf, out _));
        Assert.Equal(0, requests);

        var garbage = new byte[2000];
        new Random(1).NextBytes(garbage);
        garbage[0] = 0xFF; // no start code
        var r = DecodeResult.Frame;
        var ex = Record.Exception(() => r = dec.Decode(garbage, 333333, outBuf, out _));
        Assert.Null(ex);
        Assert.NotEqual(DecodeResult.Frame, r);
        Assert.True(requests >= 1);

        // Recovers on the next keyframe.
        Assert.Equal(DecodeResult.Frame, dec.Decode(aus[0].Data, 666666, outBuf, out _));
        Assert.Null(Record.Exception(() => dec.Decode(ReadOnlySpan<byte>.Empty, 0, outBuf, out _)));
    }

    [Fact]
    public void Decoder_DeltaBeforeKeyframe_RequestsKeyframe()
    {
        if (EncoderUnavailable()) return;
        const int W = 1280, H = 720;
        var aus = Encode(W, H, 3, 8_000_000);
        Assert.False(aus[1].IsKey);
        using var dec = new H264Decoder(W, H);
        int requests = 0;
        dec.KeyframeNeeded += () => requests++;
        var outBuf = new byte[W * H * 3 / 2];
        Assert.Equal(DecodeResult.NeedMoreInput, dec.Decode(aus[1].Data, aus[1].Time, outBuf, out _));
        Assert.Equal(1, requests);
        // Throttled: an immediate second delta does not raise again.
        Assert.Equal(DecodeResult.NeedMoreInput, dec.Decode(aus[2].Data, aus[2].Time, outBuf, out _));
        Assert.Equal(1, requests);
        Assert.Equal(DecodeResult.Frame, dec.Decode(aus[0].Data, aus[0].Time, outBuf, out _));
    }
}
