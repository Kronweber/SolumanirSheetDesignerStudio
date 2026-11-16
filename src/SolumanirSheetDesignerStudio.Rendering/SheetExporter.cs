using System.Diagnostics;
using System.Runtime.InteropServices;
using SkiaSharp;
using SolumanirSheetDesignerStudio.Core.Assets;
using SolumanirSheetDesignerStudio.Core.Export;
using SolumanirSheetDesignerStudio.Core.Layout;
using SolumanirSheetDesignerStudio.Core.Model;
using SolumanirSheetDesignerStudio.Core.Png;

namespace SolumanirSheetDesignerStudio.Rendering;

public sealed record ExportProgress(int PageIndex, int PageCount, string PageName, double PageFraction)
{
    public double Overall => PageCount <= 0 ? 0 : (PageIndex + Math.Clamp(PageFraction, 0, 1)) / PageCount;
}

public sealed record ExportPageResult(string Path, int Width, int Height, bool Capped, string Renderer, long FileBytes, TimeSpan Elapsed);

public sealed class ExportResult
{
    public List<ExportPageResult> Pages { get; } = new();
    public List<string> Warnings { get; } = new();
    public bool Cancelled { get; set; }
    public Exception? Error { get; set; }
    public string? FailedPage { get; set; }
    public bool Succeeded => !Cancelled && Error == null;
}

/// <summary>
/// Optional GPU path for a strip: draw on a GPU-backed surface and read the pixels back.
/// Returns false whenever that is not possible; the exporter then rasterises the strip on the CPU.
/// </summary>
public interface IGpuStripRenderer
{
    /// <summary>False while the renderer cannot work (no GPU context, or no frames are being drawn).</summary>
    bool IsAvailable { get; }
    int MaxSurfaceSize { get; }
    bool TryRender(SKImageInfo info, Action<SKCanvas> draw, IntPtr pixels, int rowBytes, CancellationToken ct);
}

/// <summary>
/// Tiled, streaming PNG export (handoff 6.3). The page is rendered in horizontal strips with the shared painter;
/// each strip decodes only the image rows it needs, is filtered + deflated on a worker thread and appended to the
/// file in order. Nothing the size of the output is ever allocated. Output goes to "*.tmp" and is moved into place.
/// </summary>
public sealed class SheetExporter
{
    private readonly SheetLayoutEngine _engine;
    private readonly ScenePainter _painter;
    private readonly AssetLibrary _assets;

    public SheetExporter(SheetLayoutEngine engine, ScenePainter painter, AssetLibrary assets)
    {
        _engine = engine;
        _painter = painter;
        _assets = assets;
    }

    /// <param name="paths">One output file per page.</param>
    /// <param name="workers">Strips rendered and compressed concurrently.</param>
    public Task<ExportResult> ExportAsync(Project project, IReadOnlyList<Page> pages, IReadOnlyList<string> paths, ExportOptions options,
        int workers, IGpuStripRenderer? gpu, IProgress<ExportProgress>? progress, CancellationToken ct) =>
        Task.Run(() =>
        {
            var result = new ExportResult();
            workers = Math.Clamp(workers, 1, 64);
            for (int i = 0; i < pages.Count; i++)
            {
                var page = pages[i];
                int index = i;
                try
                {
                    ct.ThrowIfCancellationRequested();
                    progress?.Report(new ExportProgress(index, pages.Count, page.Name, 0));
                    result.Pages.Add(ExportPage(page, page.Style ?? project.Style, paths[i], options, workers, options.UseGpu ? gpu : null,
                        f => progress?.Report(new ExportProgress(index, pages.Count, page.Name, f)), result.Warnings, ct));
                }
                catch (OperationCanceledException)
                {
                    result.Cancelled = true; // completed pages stay in place; the partial file is already deleted
                    break;
                }
                catch (Exception ex)
                {
                    result.Error = ex;
                    result.FailedPage = page.Name;
                    break;
                }
            }
            return result;
        });

    private sealed class Source
    {
        public required ImagePlacement Placement { get; init; }
        public required ImageAsset Asset { get; init; }
        public required SKRectI Device { get; init; } // whole-output device pixels
        public BandDecoder? Decoder;
        public bool Failed;
    }

    private sealed record Band(ImagePlacement Placement, SKImage Image, int RowOffset, SKRectI Device, int SourceHeight, bool Exact);

    private sealed class Counters { public int Gpu, Cpu; }

    private ExportPageResult ExportPage(Page page, SheetStyle style, string path, ExportOptions options, int workers, IGpuStripRenderer? gpu,
        Action<double> report, List<string> warnings, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        var layout = _engine.Layout(page, style, new LayoutOptions { TransparentBackground = options.TransparentBackground });
        var plan = ExportPlanner.Plan(layout, options, workers);
        float scale = (float)plan.Scale; // the canvas matrix stores floats; use the same value everywhere
        int width = plan.Width, height = plan.Height, rows = plan.RowsPerStrip;
        int stripCount = (height + rows - 1) / rows;

        var sources = new List<Source>();
        foreach (var p in layout.Images)
            if (_assets.Get(p.ImageId) is { } asset)
                sources.Add(new Source { Placement = p, Asset = asset, Device = ScenePainter.DeviceRect(p, scale, 0, 0) });

        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string tmp = path + ".tmp";
        var counters = new Counters();
        try
        {
            using (var file = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
            {
                var writer = new PngStreamWriter(file, width, height);
                using var slots = new SemaphoreSlim(workers);
                var pending = new Queue<Task<PngStrip>>();
                int written = 0;

                void WriteNext()
                {
                    var strip = pending.Dequeue().GetAwaiter().GetResult();
                    writer.WriteStrip(strip);
                    report(++written / (double)stripCount);
                }

                try
                {
                    for (int k = 0; k < stripCount; k++)
                    {
                        ct.ThrowIfCancellationRequested();
                        slots.Wait(ct);
                        int y0 = k * rows, h = Math.Min(rows, height - y0);
                        bool last = k == stripCount - 1;
                        List<Band> bands;
                        try
                        {
                            // Sequential on purpose: the scanline decoders only move forward.
                            bands = PrepareBands(sources, y0, h, warnings);
                        }
                        catch
                        {
                            slots.Release();
                            throw;
                        }

                        pending.Enqueue(Task.Run(() =>
                        {
                            try
                            {
                                return RenderStrip(layout, scale, width, y0, h, bands, options, gpu, last, counters, ct);
                            }
                            finally
                            {
                                foreach (var b in bands) b.Image.Dispose();
                                slots.Release();
                            }
                        }));

                        // Write what is ready; if the oldest strip lags far behind, wait for it so finished strips cannot pile up.
                        while (pending.Count > 0 && (pending.Peek().IsCompleted || pending.Count >= workers * 3)) WriteNext();

                        // Images that end above the next strip are finished: close their files now.
                        foreach (var src in sources)
                            if (src.Decoder != null && src.Device.Bottom <= y0 + h)
                            {
                                src.Decoder.Dispose();
                                src.Decoder = null;
                            }
                    }
                    while (pending.Count > 0) WriteNext();
                }
                catch
                {
                    // Let strips that are still running finish before the file is closed and removed.
                    try { Task.WaitAll(pending.ToArray<Task>()); } catch (Exception) { /* already failing */ }
                    throw;
                }
                finally
                {
                    foreach (var src in sources) src.Decoder?.Dispose();
                }

                writer.Finish();
                file.Flush(flushToDisk: true);
            }
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch (Exception) { /* best effort */ }
            throw;
        }

        string renderer = counters.Gpu == 0 ? "CPU" : counters.Cpu == 0 ? "GPU" : $"GPU + CPU ({counters.Cpu} of {stripCount} strips on CPU)";
        return new ExportPageResult(path, width, height, plan.Capped, renderer, new FileInfo(path).Length, clock.Elapsed);
    }

    /// <summary>Decodes, for every image crossing the strip, just the source rows that strip needs.</summary>
    private List<Band> PrepareBands(List<Source> sources, int y0, int h, List<string> warnings)
    {
        var bands = new List<Band>();
        try
        {
            foreach (var src in sources)
            {
                var d = src.Device;
                if (src.Failed || d.Bottom <= y0 || d.Top >= y0 + h || d.Width <= 0 || d.Height <= 0) continue;
                try
                {
                    var asset = src.Asset;
                    src.Decoder ??= new BandDecoder(() => _assets.OpenRead(asset),
                        Math.Max(d.Width / (double)src.Placement.FileWidth, d.Height / (double)src.Placement.FileHeight));
                    var decoder = src.Decoder;
                    bool exact = decoder.Width == d.Width && decoder.Height == d.Height; // 1:1 → copy source pixels untouched
                    double ratio = decoder.Height / (double)d.Height; // source rows per output row
                    int margin = exact ? 0 : 3 + (int)Math.Ceiling(ratio); // neighbours for the resampling filter
                    int d0 = Math.Max(d.Top, y0) - d.Top, d1 = Math.Min(d.Bottom, y0 + h) - d.Top;
                    int r0 = Math.Max(0, (int)Math.Floor(d0 * ratio) - margin);
                    int r1 = Math.Min(decoder.Height, (int)Math.Ceiling(d1 * ratio) + margin);
                    var (image, offset) = decoder.GetBand(r0, r1);
                    bands.Add(new Band(src.Placement, image, offset, d, decoder.Height, exact));
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException)
                {
                    // An unreadable image leaves its slot empty instead of failing the whole export.
                    src.Failed = true;
                    src.Decoder?.Dispose();
                    src.Decoder = null;
                    lock (warnings) warnings.Add($"\"{src.Asset.FileName}\" could not be read and was left out: {ex.Message}");
                }
            }
            return bands;
        }
        catch
        {
            foreach (var b in bands) b.Image.Dispose();
            throw;
        }
    }

    private unsafe PngStrip RenderStrip(PageLayout layout, float scale, int width, int y0, int rows, List<Band> bands, ExportOptions options,
        IGpuStripRenderer? gpu, bool last, Counters counters, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        int rowBytes = checked(width * 4);
        long bytes = (long)rowBytes * rows;
        // Allocated per strip on purpose: reusing the buffers was measured (no faster, 300 MB more at the peak,
        // because the strips' peak and the image decoders' peak then coincide).
        IntPtr pixels = Marshal.AllocHGlobal((IntPtr)bytes);
        try
        {
            var info = new SKImageInfo(width, rows, SKColorType.Rgba8888, SKAlphaType.Premul);
            var imagePainter = new StripImagePainter(bands, y0);

            void Draw(SKCanvas canvas)
            {
                canvas.Clear(SKColors.Transparent);
                canvas.Translate(0, -y0);
                canvas.Scale(scale);
                _painter.Paint(canvas, layout.Content, imagePainter);
            }

            bool onGpu = false;
            if (gpu is { IsAvailable: true } && width <= gpu.MaxSurfaceSize && rows <= gpu.MaxSurfaceSize &&
                bands.All(b => b.Image.Width <= gpu.MaxSurfaceSize && b.Image.Height <= gpu.MaxSurfaceSize))
            {
                try { onGpu = gpu.TryRender(info, Draw, pixels, rowBytes, ct); }
                catch (OperationCanceledException) { throw; }
                catch (Exception) { onGpu = false; }
            }
            if (!onGpu)
            {
                using var surface = SKSurface.Create(info, pixels, rowBytes) ?? throw new InvalidOperationException("Could not create the render surface.");
                Draw(surface.Canvas);
                surface.Canvas.Flush();
            }
            if (onGpu) Interlocked.Increment(ref counters.Gpu);
            else Interlocked.Increment(ref counters.Cpu);

            if (options.TransparentBackground) ImageOps.Unpremultiply((byte*)pixels, (long)width * rows);
            ct.ThrowIfCancellationRequested();
            return PngStripEncoder.Encode(new ReadOnlySpan<byte>((void*)pixels, checked((int)bytes)), width, rows, rowBytes, options.Compression, last);
        }
        finally
        {
            Marshal.FreeHGlobal(pixels);
        }
    }

    /// <summary>Draws the decoded bands of one strip in device pixels.</summary>
    private sealed class StripImagePainter : IImagePainter
    {
        private readonly Dictionary<ImagePlacement, Band> _bands;
        private readonly int _stripY;

        public StripImagePainter(List<Band> bands, int stripY)
        {
            _bands = new Dictionary<ImagePlacement, Band>(ReferenceEqualityComparer.Instance);
            foreach (var b in bands) _bands[b.Placement] = b;
            _stripY = stripY;
        }

        public void Draw(SKCanvas canvas, ImagePlacement placement, SKRect local, SKRectI device)
        {
            if (!_bands.TryGetValue(placement, out var b)) return;
            using var paint = new SKPaint { FilterQuality = b.Exact ? SKFilterQuality.None : SKFilterQuality.High, IsAntialias = false };
            canvas.Save();
            canvas.ResetMatrix();
            if (b.Exact)
            {
                // 1:1 — whole pixels, no arithmetic.
                canvas.DrawImage(b.Image, b.Device.Left, b.Device.Top + b.RowOffset - _stripY, paint);
            }
            else
            {
                // One mapping for the whole image, identical in every strip apart from the (whole-pixel) strip offset;
                // a band only says which of the image's rows it holds. That keeps the result independent of how the
                // image was cut into bands, so strips join without seams.
                canvas.Translate(b.Device.Left, b.Device.Top - _stripY);
                canvas.Scale(b.Device.Width / (float)b.Image.Width, b.Device.Height / (float)b.SourceHeight);
                canvas.DrawImage(b.Image, 0, b.RowOffset, paint);
            }
            canvas.Restore();
        }
    }
}
