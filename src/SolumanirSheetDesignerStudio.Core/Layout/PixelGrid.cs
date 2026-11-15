namespace SolumanirSheetDesignerStudio.Core.Layout;

/// <summary>
/// How sheet coordinates land on device pixels. Every place that snaps (the painter, the export planner, the image
/// placer) uses this, so an edge computed twice always lands on the same pixel.
/// </summary>
public static class PixelGrid
{
    /// <summary>
    /// Rounds half up. Unlike <see cref="Math.Round(double)"/> (half to even) this is translation-invariant:
    /// Round(v + n) == Round(v) + n for whole n, so an edge snaps identically in every export strip.
    /// </summary>
    public static int Round(double v) => (int)Math.Floor(v + 0.5);

    /// <summary>
    /// Device-pixel rectangle an image is drawn into: object-fit "contain" inside the snapped container, centred
    /// horizontally, and bottom-aligned or centred vertically. When the fit is within a pixel of the file's own size
    /// the file size is used, so native-resolution exports copy source pixels 1:1.
    /// </summary>
    public static PixelRect PlaceImage(ImagePlacement p, double scale, double offsetX = 0, double offsetY = 0)
    {
        var c = p.Container;
        int left = Round(c.X * scale + offsetX), right = Round(c.Right * scale + offsetX);
        int top = Round(c.Y * scale + offsetY), bottom = Round(c.Bottom * scale + offsetY);
        double k = Math.Min(c.W * scale / p.FileWidth, c.H * scale / p.FileHeight);
        double dw = p.FileWidth * k, dh = p.FileHeight * k;
        int w, h;
        if (Math.Abs(dw - p.FileWidth) <= 1.0 && Math.Abs(dh - p.FileHeight) <= 1.0)
        {
            w = p.FileWidth;
            h = p.FileHeight;
        }
        else
        {
            w = Math.Max(1, Round(dw));
            h = Math.Max(1, Round(dh));
        }
        int x = Round((left + right - w) / 2.0);
        int y = p.AlignBottom ? bottom - h : Round((top + bottom - h) / 2.0);
        return new PixelRect(x, y, w, h);
    }
}

/// <summary>A rectangle in whole device pixels.</summary>
public readonly record struct PixelRect(int X, int Y, int W, int H)
{
    public int Right => X + W;
    public int Bottom => Y + H;
}
