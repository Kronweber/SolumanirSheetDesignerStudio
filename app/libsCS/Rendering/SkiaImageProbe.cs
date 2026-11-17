using SkiaSharp;
using SolumanirSheetDesignerStudio.Core.Assets;

namespace SolumanirSheetDesignerStudio.Rendering;

/// <summary>Reads size and format with <see cref="SKCodec"/> (EXIF orientation applied). PNG, JPEG, WebP, BMP and GIF are accepted.</summary>
public sealed class SkiaImageProbe : IImageProbe
{
    public ImageProbeResult? Probe(Stream stream)
    {
        try
        {
            using var codec = SKCodec.Create(stream);
            if (codec == null) return null;
            string? mime = codec.EncodedFormat switch
            {
                SKEncodedImageFormat.Png => "image/png",
                SKEncodedImageFormat.Jpeg => "image/jpeg",
                SKEncodedImageFormat.Webp => "image/webp",
                SKEncodedImageFormat.Bmp => "image/bmp",
                SKEncodedImageFormat.Gif => "image/gif",
                _ => null,
            };
            if (mime == null || ImageFormats.ExtensionFor(mime) is not { } extension) return null;
            var info = codec.Info;
            bool swap = ImageOps.SwapsAxes(codec.EncodedOrigin);
            return new ImageProbeResult(swap ? info.Height : info.Width, swap ? info.Width : info.Height, mime, extension);
        }
        catch (Exception)
        {
            return null; // whatever the codec choked on, the answer is the same: not an image this app can use
        }
    }
}
