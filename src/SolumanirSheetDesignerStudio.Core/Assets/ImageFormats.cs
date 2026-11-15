namespace SolumanirSheetDesignerStudio.Core.Assets;

/// <summary>The image formats the app accepts: one table for MIME types and file extensions.</summary>
public static class ImageFormats
{
    public const string UnknownMime = "application/octet-stream";

    private static readonly (string Mime, string Extension)[] Known =
    {
        ("image/png", ".png"), ("image/jpeg", ".jpg"), ("image/webp", ".webp"), ("image/gif", ".gif"), ("image/bmp", ".bmp"),
    };

    public static IEnumerable<string> MimeTypes => Known.Select(k => k.Mime);

    /// <summary>".png" for "image/png"; null for a type the app does not know.</summary>
    public static string? ExtensionFor(string? mime)
    {
        foreach (var k in Known)
            if (string.Equals(k.Mime, mime, StringComparison.OrdinalIgnoreCase)) return k.Extension;
        return null;
    }

    public static string MimeForFile(string fileName)
    {
        string ext = Path.GetExtension(fileName).ToLowerInvariant();
        if (ext == ".jpeg") ext = ".jpg";
        foreach (var k in Known)
            if (k.Extension == ext) return k.Mime;
        return UnknownMime;
    }
}
