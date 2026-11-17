using SolumanirSheetDesignerStudio.Core.Layout;

namespace SolumanirSheetDesignerStudio.Core.Export;

/// <summary>Computed output of one page before anything is rendered (shown in the export dialog).</summary>
public sealed record ExportPlan
{
    public required double Scale { get; init; }
    /// <summary>Scale before the long-edge cap was applied.</summary>
    public required double RequestedScale { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public bool Capped { get; init; }
    public long EstimatedFileBytes { get; init; }
    public long EstimatedPeakMemoryBytes { get; init; }
    public int ImageCount { get; init; }
    /// <summary>Images that end up larger than their source pixels at this scale.</summary>
    public int UpscaledImages { get; init; }
    /// <summary>Images that end up smaller than their source pixels at this scale.</summary>
    public int DownscaledImages { get; init; }
    public int RowsPerStrip { get; init; }
    /// <summary>True when a side exceeds 65 535 px, which many viewers cannot open.</summary>
    public bool ExceedsViewerLimit => Width > ExportOptions.MaxPngSide || Height > ExportOptions.MaxPngSide;
}

/// <summary>Export scale rules from handoff section 6.2.</summary>
public static class ExportPlanner
{
    public const double MinAutoScale = 2;

    /// <summary>
    /// Source pixels (as bytes) the exporter decodes per pass for formats it can only read as row ranges (PNG, WebP).
    /// Larger = fewer passes over huge images, smaller = less memory.
    /// </summary>
    public const long DecodeWindowBytes = 384L << 20;

    /// <summary>
    /// Auto: the largest "need" over all images (so the most-downscaled image lands at exactly its native pixels or
    /// its row's export height), never below 2. Fixed modes: the chosen factor.
    /// </summary>
    public static double RequestedScale(PageLayout layout, ExportOptions o)
    {
        switch (o.ScaleMode)
        {
            case ExportScaleMode.X2: return 2;
            case ExportScaleMode.X4: return 4;
            case ExportScaleMode.X8: return 8;
            case ExportScaleMode.CustomScale: return Math.Clamp(double.IsFinite(o.CustomScale) ? o.CustomScale : 2, 0.1, 1000);
            case ExportScaleMode.TargetWidth: return Math.Max(16, o.TargetWidth) / layout.Width;
            default:
                double s = MinAutoScale;
                foreach (var img in layout.Images)
                {
                    double need = img.Need;
                    if (double.IsFinite(need) && need > s) s = need;
                }
                return s;
        }
    }

    public static ExportPlan Plan(PageLayout layout, ExportOptions o, int workers = 1)
    {
        double requested = RequestedScale(layout, o);
        double scale = requested;
        bool capped = false;

        // PNG stores sizes in 31 bits; stay well inside that and inside 32-bit row lengths.
        double hardCap = 400_000_000.0 / Math.Max(layout.Width, layout.Height);
        double cap = o.MaxLongEdge > 0 ? Math.Min(hardCap, o.MaxLongEdge / Math.Max(layout.Width, layout.Height)) : hardCap;
        if (scale > cap)
        {
            scale = cap;
            capped = true;
        }

        // The painter's matrix is single precision. Settling on that value here means the sizes planned below and
        // the pixels drawn later are computed from the very same number.
        scale = (float)scale;

        int width = Math.Max(1, PixelGrid.Round(layout.Width * scale));
        int height = Math.Max(1, PixelGrid.Round(layout.Height * scale));
        int rows = StripRows(width, height, workers);

        int up = 0, down = 0;
        foreach (var img in layout.Images)
        {
            double native = NativeScale(img);
            if (native <= 0) continue;
            if (scale > native * 1.01) up++;
            else if (scale < native * 0.99) down++;
        }

        long raw = (long)width * height * 4;
        double ratio = o.Compression switch { PngCompression.Fast => 0.42, PngCompression.Small => 0.27, _ => 0.30 };
        long stripBytes = (long)width * rows * 4;
        int inFlight = Math.Max(1, Math.Min(workers, (int)Math.Ceiling(height / (double)rows)));

        return new ExportPlan
        {
            Scale = scale, RequestedScale = requested, Width = width, Height = height, Capped = capped,
            EstimatedFileBytes = (long)(raw * ratio),
            // Per strip in flight: the pixels, their filtered copy and the deflate output; plus the decoded artwork.
            EstimatedPeakMemoryBytes = (long)(stripBytes * 2.6 * inFlight) + EstimateSourceBytes(layout, scale) + (64L << 20),
            ImageCount = layout.Images.Count, UpscaledImages = up, DownscaledImages = down, RowsPerStrip = rows,
        };
    }

    /// <summary>Rows per strip: a pixel budget split between the worker threads (never more than 64 MP per strip).</summary>
    public static int StripRows(int width, int height, int workers)
    {
        width = Math.Max(1, width);
        double budget = Math.Clamp(192e6 / Math.Max(1, workers), 4e6, 64e6);
        long rows = (long)Math.Max(16, budget / width);
        long fitsInOneBuffer = Math.Max(1, (int.MaxValue - 4096L) / ((long)width * 4)); // a strip is addressed with 32-bit lengths
        return (int)Math.Clamp(Math.Min(rows, fitsInOneBuffer), 1, Math.Max(1, height));
    }

    /// <summary>
    /// Decoded artwork alive at the worst moment: every image that crosses the same strip is held at about its
    /// output resolution (sources are pre-reduced to less than twice that) or one decode window, whichever is
    /// smaller, plus one window being decoded.
    /// </summary>
    private static long EstimateSourceBytes(PageLayout layout, double scale)
    {
        var edges = new List<(double Y, long Delta)>();
        long decoding = 0;
        foreach (var img in layout.Images)
        {
            var (w, h) = OutputSize(img, scale);
            if (w <= 0 || h <= 0) continue;
            long native = (long)img.FileWidth * img.FileHeight * 4;
            long held = Math.Min(Math.Min(native, (long)w * h * 8), DecodeWindowBytes);
            edges.Add((img.Container.Y, held));
            edges.Add((img.Container.Bottom, -held));
            decoding = Math.Max(decoding, Math.Min(native, DecodeWindowBytes));
        }
        edges.Sort((a, b) => a.Y != b.Y ? a.Y.CompareTo(b.Y) : a.Delta.CompareTo(b.Delta));
        long current = 0, peak = 0;
        foreach (var (_, delta) in edges) peak = Math.Max(peak, current += delta);
        return peak + decoding;
    }

    /// <summary>Scale at which the image is drawn at exactly its source resolution.</summary>
    public static double NativeScale(ImagePlacement img) => img.Drawn.W > 0 ? img.FileWidth / img.Drawn.W : 0;

    /// <summary>Pixel size the image gets in the export at <paramref name="scale"/>.</summary>
    public static (int Width, int Height) OutputSize(ImagePlacement img, double scale)
    {
        if (img.FileWidth <= 0 || img.FileHeight <= 0) return (0, 0);
        var device = PixelGrid.PlaceImage(img, scale);
        return (device.W, device.H);
    }

    public static bool IsUpscaled(ImagePlacement img, double scale)
    {
        double native = NativeScale(img);
        return native > 0 && scale > native * 1.01;
    }
}
