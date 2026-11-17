using SkiaSharp;
using SolumanirSheetDesignerStudio.Core.Layout;

namespace SolumanirSheetDesignerStudio.Rendering;

/// <summary>Draws artwork from the preview cache (screen and thumbnails).</summary>
public sealed class PreviewImagePainter : IImagePainter
{
    private readonly PreviewImageCache _cache;
    private readonly bool _blocking;

    /// <param name="blocking">True to decode synchronously when an image is not cached (off-screen rendering).</param>
    public PreviewImagePainter(PreviewImageCache cache, bool blocking = false)
    {
        _cache = cache;
        _blocking = blocking;
    }

    /// <summary>Set while painting when at least one image was not available at full preview quality yet.</summary>
    public bool Incomplete { get; private set; }

    public void Draw(SKCanvas canvas, ImagePlacement placement, SKRect local, SKRectI device)
    {
        int need = Math.Max(device.Width, device.Height);
        var image = _blocking ? _cache.GetBlocking(placement.ImageId, need) : _cache.Get(placement.ImageId, need);
        if (image == null)
        {
            Incomplete = true;
            return;
        }
        using var paint = new SKPaint { IsAntialias = false };
        canvas.DrawImage(image, SKRect.Create(image.Width, image.Height), local, _cache.Quality, paint);
    }
}
