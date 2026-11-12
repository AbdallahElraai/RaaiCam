using Xunit;
using RaaiCam.Media;

namespace RaaiCam.Media.Tests;

public class TestPatternSourceTests
{
    private const int W = 1280, H = 720;

    private static byte[] Render(ulong id)
    {
        var buf = new byte[W * H * 3 / 2];
        Assert.Equal(buf.Length, new TestPatternSource().Render(buf, W, H, W, id));
        return buf;
    }

    [Fact]
    public void Pattern_EncodesFrameIdTopAndBottom()
    {
        const ulong id = 0xA5A5_0000_1234_5678;
        var buf = Render(id);
        Assert.Equal(id, TestPatternSource.DecodeId(buf.AsSpan(0, W)));
        Assert.Equal(id, TestPatternSource.DecodeId(buf.AsSpan((H - 1) * W, W)));
    }

    [Fact]
    public void Pattern_BarsUseBt601Values()
    {
        var buf = Render(1);
        int row = H / 3;
        Assert.Equal(0xB4, buf[row * W + W / 16]);
        Assert.Equal(0x10, buf[row * W + W - W / 16]);
    }

    [Theory]
    [InlineData(320, 240, 320)]
    [InlineData(1280, 720, 1344)]
    [InlineData(1280, 720, 1280)]
    public void Pattern_SmallAndPaddedStrideDoNotThrow(int w, int h, int stride)
    {
        var buf = new byte[stride * h * 3 / 2];
        const ulong id = 0xFFFF_0000_FFFF_0001;
        Assert.Equal(buf.Length, new TestPatternSource().Render(buf, w, h, stride, id));
        int n = Math.Min(w, 1024);
        var top = buf.AsSpan(0, n);
        Assert.True(top.SequenceEqual(buf.AsSpan((h - 1) * stride, n)));
        ulong mask = w >= 1024 ? ulong.MaxValue : ulong.MaxValue << (64 - w / 16);
        Assert.Equal(id & mask, TestPatternSource.DecodeId(top) & mask);
    }

    [Fact]
    public void Pattern_BarChromaValues()
    {
        var buf = Render(1);
        int uv = W * H + ((H / 3) / 2) * W;
        int x0 = (W / 16) & ~1, x7 = (W - W / 16) & ~1, x1 = (W / 8 + W / 16) & ~1;
        Assert.Equal(128, buf[uv + x0]);
        Assert.Equal(128, buf[uv + x0 + 1]);
        Assert.Equal(128, buf[uv + x7]);
        Assert.Equal(128, buf[uv + x7 + 1]);
        Assert.Equal(44, buf[uv + x1]);       // bar 1 (yellow) U
        Assert.Equal(142, buf[uv + x1 + 1]);  // bar 1 V
    }

    [Fact]
    public void Pattern_BlockMoves8PxPerFrame()
    {
        int FindX(byte[] b)
        {
            int row = H * 5 / 6;
            for (int x = 0; x < W; x++) if (b[row * W + x] == 235) return x;
            return -1;
        }
        int x1 = FindX(Render(1)), x2 = FindX(Render(2));
        Assert.True(x1 >= 0);
        Assert.Equal(8, x2 - x1);
    }
}
