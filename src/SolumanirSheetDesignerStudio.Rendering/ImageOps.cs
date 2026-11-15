using SkiaSharp;

namespace SolumanirSheetDesignerStudio.Rendering;

internal static class ImageOps
{
    public static readonly SKColorSpace Srgb = SKColorSpace.CreateSrgb();

    public static SKImageInfo Info(int width, int height) => new(width, height, SKColorType.Rgba8888, SKAlphaType.Premul, Srgb);

    public static bool SwapsAxes(SKEncodedOrigin origin) =>
        origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;

    /// <summary>Returns the bitmap rotated/flipped upright (a new bitmap; the source is disposed), or the source when already upright.</summary>
    public static SKBitmap ApplyOrigin(SKBitmap src, SKEncodedOrigin origin)
    {
        if (origin == SKEncodedOrigin.TopLeft || (int)origin == 0) return src;
        bool swap = SwapsAxes(origin);
        int w = swap ? src.Height : src.Width, h = swap ? src.Width : src.Height;
        var matrix = origin switch
        {
            SKEncodedOrigin.TopRight => new SKMatrix(-1, 0, w, 0, 1, 0, 0, 0, 1),
            SKEncodedOrigin.BottomRight => new SKMatrix(-1, 0, w, 0, -1, h, 0, 0, 1),
            SKEncodedOrigin.BottomLeft => new SKMatrix(1, 0, 0, 0, -1, h, 0, 0, 1),
            SKEncodedOrigin.LeftTop => new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1),
            SKEncodedOrigin.RightTop => new SKMatrix(0, -1, w, 1, 0, 0, 0, 0, 1),
            SKEncodedOrigin.RightBottom => new SKMatrix(0, -1, w, -1, 0, h, 0, 0, 1),
            SKEncodedOrigin.LeftBottom => new SKMatrix(0, 1, 0, -1, 0, h, 0, 0, 1),
            _ => SKMatrix.Identity,
        };
        var dst = new SKBitmap(Info(w, h));
        using (var canvas = new SKCanvas(dst))
        using (var paint = new SKPaint { BlendMode = SKBlendMode.Src, FilterQuality = SKFilterQuality.None })
        {
            canvas.SetMatrix(matrix);
            canvas.DrawBitmap(src, 0, 0, paint);
        }
        src.Dispose();
        return dst;
    }

    /// <summary>
    /// Averages <paramref name="f"/>×<paramref name="srcRows"/> blocks of premultiplied RGBA into one output row
    /// (exact box filter; partial blocks at the right edge average what exists).
    /// </summary>
    public static unsafe void BoxReduceRow(byte* src, int srcWidth, int srcRows, int srcStride, byte* dst, int dstWidth, int f)
    {
        for (int x = 0; x < dstWidth; x++)
        {
            int x0 = x * f, x1 = Math.Min(srcWidth, x0 + f);
            uint r = 0, g = 0, b = 0, a = 0;
            for (int y = 0; y < srcRows; y++)
            {
                byte* p = src + (long)y * srcStride + x0 * 4;
                for (int xx = x0; xx < x1; xx++, p += 4)
                {
                    r += p[0]; g += p[1]; b += p[2]; a += p[3];
                }
            }
            uint n = (uint)((x1 - x0) * srcRows);
            if (n == 0) n = 1;
            uint half = n / 2;
            byte* d = dst + x * 4;
            d[0] = (byte)((r + half) / n);
            d[1] = (byte)((g + half) / n);
            d[2] = (byte)((b + half) / n);
            d[3] = (byte)((a + half) / n);
        }
    }

    /// <summary>Converts premultiplied RGBA to straight alpha in place (needed for PNGs with a transparent background).</summary>
    public static unsafe void Unpremultiply(byte* pixels, long pixelCount)
    {
        for (long i = 0; i < pixelCount; i++, pixels += 4)
        {
            uint a = pixels[3];
            if (a == 255 || a == 0) continue;
            pixels[0] = (byte)Math.Min(255u, (pixels[0] * 255u + a / 2) / a);
            pixels[1] = (byte)Math.Min(255u, (pixels[1] * 255u + a / 2) / a);
            pixels[2] = (byte)Math.Min(255u, (pixels[2] * 255u + a / 2) / a);
        }
    }
}
