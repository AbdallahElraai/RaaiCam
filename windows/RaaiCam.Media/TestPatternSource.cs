namespace RaaiCam.Media;

/// <summary>
/// NV12 test pattern: 8 BT.601 color bars in the top 2/3, a white block moving 8 px/frame in the bottom 1/3,
/// and the frameId encoded as 64 bits (MSB first, 16-px-wide runs, 0x10 = 0, 0xEB = 1) in the first and last
/// Y rows so a probe can detect tearing (top id != bottom id). Width and height must be even (NV12); width >= 1024 is needed for all 64 bits.
/// </summary>
public sealed class TestPatternSource
{
    public const int BitRunPixels = 16;
    public const byte BitZero = 0x10;
    public const byte BitOne = 0xEB;
    private const byte StripY = 40;

    private static readonly (byte y, byte u, byte v)[] Bars =
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

    public static int BlockWidth(int width) => Math.Max(16, width / 20);

    /// <summary>Left edge of the moving white block for a given frame.</summary>
    public static int BlockX(int width, ulong frameId) =>
        (int)(frameId * 8 % (ulong)Math.Max(1, width - BlockWidth(width)));

    public int Render(Span<byte> nv12, int width, int height, int stride, ulong frameId)
    {
        if (width <= 0 || height <= 0 || stride < width) return 0;
        int total = stride * height * 3 / 2;
        if (nv12.Length < total) return 0;

        int barsBottom = height * 2 / 3;

        // The bars/strip background is static per geometry: build once, then a single block copy per frame.
        if (_bg == null || _bgW != width || _bgH != height || _bgStride != stride)
        {
            _bg = new byte[total];
            BuildBackground(_bg, width, height, stride, barsBottom);
            _bgW = width; _bgH = height; _bgStride = stride;
        }
        _bg.AsSpan(0, total).CopyTo(nv12);

        // Moving block (Y only; chroma stays neutral in the strip).
        int bw = BlockWidth(width);
        int bx = BlockX(width, frameId);
        int top = barsBottom + height / 24, bottom = height - height / 24;
        for (int row = top; row < bottom; row++)
            nv12.Slice(row * stride + bx, Math.Min(bw, width - bx)).Fill(235);

        EncodeId(nv12.Slice(0, width), frameId);
        EncodeId(nv12.Slice((height - 1) * stride, width), frameId);
        return total;
    }

    private static void EncodeId(Span<byte> row, ulong id)
    {
        for (int bit = 0; bit < 64; bit++)
        {
            int x0 = bit * BitRunPixels;
            if (x0 >= row.Length) break;
            int n = Math.Min(BitRunPixels, row.Length - x0);
            bool one = ((id >> (63 - bit)) & 1UL) != 0;
            row.Slice(x0, n).Fill(one ? BitOne : BitZero);
        }
    }

    /// <summary>Decodes an id written by <see cref="Render"/> from a Y row (samples each run's center).</summary>
    public static ulong DecodeId(ReadOnlySpan<byte> row)
    {
        ulong id = 0; int bits = 0;
        for (int bit = 0; bit < 64; bit++)
        {
            int x = bit * BitRunPixels + BitRunPixels / 2;
            if (x >= row.Length) break;
            id = (id << 1) | (row[x] >= 0x80 ? 1UL : 0UL); bits++;
        }
        return bits >= 64 ? id : id << (64 - bits); // left-aligned when the row is too narrow
    }

    private byte[]? _bg;
    private int _bgW, _bgH, _bgStride;

    private static void BuildBackground(Span<byte> nv12, int width, int height, int stride, int barsBottom)
    {
        for (int row = 0; row < height; row++)
        {
            var line = nv12.Slice(row * stride, width);
            if (row < barsBottom)
            {
                for (int b = 0; b < 8; b++)
                {
                    int x0 = b * width / 8, x1 = (b + 1) * width / 8;
                    line.Slice(x0, x1 - x0).Fill(Bars[b].y);
                }
            }
            else line.Fill(StripY);
        }

        int uvBase = stride * height;
        for (int row = 0; row < height / 2; row++)
        {
            bool inBars = row * 2 < barsBottom;
            for (int x = 0; x + 1 < width; x += 2)
            {
                byte u = 128, v = 128;
                if (inBars)
                {
                    var c = Bars[Math.Min(7, x * 8 / width)];
                    u = c.u; v = c.v;
                }
                nv12[uvBase + row * stride + x] = u;
                nv12[uvBase + row * stride + x + 1] = v;
            }
        }
    }
}
