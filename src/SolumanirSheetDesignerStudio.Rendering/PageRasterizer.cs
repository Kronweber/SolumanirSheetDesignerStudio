using SkiaSharp;
using SolumanirSheetDesignerStudio.Core.Layout;

namespace SolumanirSheetDesignerStudio.Rendering;

/// <summary>Renders a laid-out page into a bitmap at a small scale (page thumbnails, previews, tests).</summary>
public static class PageRasterizer
{
    public static SKBitmap Render(ScenePainter painter, PageLayout layout, double scale, IImagePainter? images, bool chrome = false)
    {
        int w = Math.Max(1, PixelGrid.Round(layout.Width * scale)), h = Math.Max(1, PixelGrid.Round(layout.Height * scale));
        var bitmap = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        canvas.Scale((float)scale);
        painter.Paint(canvas, layout.Content, images);
        if (chrome) painter.Paint(canvas, layout.Chrome, null);
        canvas.Flush();
        return bitmap;
    }

    public static byte[] RenderPng(ScenePainter painter, PageLayout layout, double scale, IImagePainter? images)
    {
        using var bitmap = Render(painter, layout, scale, images);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        return data.ToArray();
    }
}
