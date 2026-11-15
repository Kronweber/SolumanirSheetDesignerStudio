using Avalonia.Media.Imaging;
using SkiaSharp;

namespace SolumanirSheetDesignerStudio.App.Services;

public static class SkiaInterop
{
    /// <summary>Converts a small Skia image to an Avalonia bitmap (thumbnails only — it round-trips through PNG).</summary>
    public static Bitmap? ToBitmap(SKImage? image)
    {
        if (image == null) return null;
        using var data = image.Encode(SKEncodedImageFormat.Png, 80);
        return data == null ? null : FromPng(data.ToArray());
    }

    public static Bitmap? FromPng(byte[]? png)
    {
        if (png == null || png.Length == 0) return null;
        try
        {
            using var ms = new MemoryStream(png);
            return new Bitmap(ms);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
