using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using SolumanirSheetDesignerStudio.Core.Assets;
using SolumanirSheetDesignerStudio.Core.Export;
using SolumanirSheetDesignerStudio.Core.Model;

namespace SolumanirSheetDesignerStudio.Core.IO;

/// <summary>Shape of <c>project.json</c> inside a v2 <c>.speciesproj</c> (also used for recovery snapshots and templates).</summary>
public sealed class ProjectDocument
{
    public int FormatVersion { get; set; } = AppInfo.FormatVersion;
    public string? App { get; set; }
    public ProjectDto Project { get; set; } = new();
    public List<ImageMeta> Images { get; set; } = new();
}

public sealed class ProjectDto
{
    public string Title { get; set; } = "";
    public SheetStyle Style { get; set; } = SheetStyle.Default;
    public ImmutableList<Page> Pages { get; set; } = ImmutableList<Page>.Empty;
    public string? CurrentPageId { get; set; }
    /// <summary>Last zoom factor; null = fit to width.</summary>
    public double? Zoom { get; set; }
    public ExportOptions? LastExport { get; set; }
}

public sealed class ImageMeta
{
    public string Id { get; set; } = "";
    public string FileName { get; set; } = "";
    public string MimeType { get; set; } = "";
    public int PixelWidth { get; set; }
    public int PixelHeight { get; set; }
    public long ByteLength { get; set; }
    public string? Sha256 { get; set; }
    /// <summary>Recovery snapshots only: file name inside the recovery folder, or null when the bytes are in the project file.</summary>
    public string? RecoveryFile { get; set; }

    public static ImageMeta From(ImageAsset a) => new()
    {
        Id = a.Id, FileName = a.FileName, MimeType = a.MimeType, PixelWidth = a.PixelWidth,
        PixelHeight = a.PixelHeight, ByteLength = a.ByteLength, Sha256 = a.Sha256,
    };

    public ImageAsset ToAsset(AssetSource source) => new()
    {
        Id = Id, FileName = FileName, MimeType = MimeType, PixelWidth = PixelWidth,
        PixelHeight = PixelHeight, ByteLength = source.Length, Sha256 = Sha256, Source = source,
    };
}

public static class ProjectJson
{
    public static readonly JsonSerializerOptions Options = CreateOptions(indented: false);
    public static readonly JsonSerializerOptions Indented = CreateOptions(indented: true);

    private static JsonSerializerOptions CreateOptions(bool indented) => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = indented,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static ProjectDocument BuildDocument(Project project, string? currentPageId, double? zoom, ExportOptions? lastExport, IEnumerable<ImageAsset> images) => new()
    {
        App = AppInfo.Name,
        Project = new ProjectDto
        {
            Title = project.Title, Style = project.Style, Pages = project.Pages,
            CurrentPageId = currentPageId, Zoom = zoom, LastExport = lastExport,
        },
        Images = images.OrderBy(a => a.Id, StringComparer.Ordinal).Select(ImageMeta.From).ToList(),
    };

    public static byte[] Serialize(ProjectDocument doc, bool indented = false) =>
        JsonSerializer.SerializeToUtf8Bytes(doc, indented ? Indented : Options);

    public static ProjectDocument Deserialize(Stream stream)
    {
        ProjectDocument? doc;
        try
        {
            doc = JsonSerializer.Deserialize<ProjectDocument>(stream, Options);
        }
        catch (JsonException ex)
        {
            throw new InvalidProjectException("The project data is corrupt: " + ex.Message, ex);
        }
        if (doc == null) throw new InvalidProjectException("The project data is empty.");
        if (doc.FormatVersion > AppInfo.FormatVersion)
            throw new InvalidProjectException($"This project was saved by a newer version of {AppInfo.Name} (format {doc.FormatVersion}).");
        doc.Project ??= new ProjectDto();
        doc.Images ??= new List<ImageMeta>();
        doc.Images.RemoveAll(m => m == null || string.IsNullOrEmpty(m.Id));
        return doc;
    }

    public static Project ToProject(ProjectDto dto) => ProjectOps.Normalize(new Project
    {
        Title = dto.Title ?? "", Style = dto.Style ?? SheetStyle.Default, Pages = dto.Pages ?? ImmutableList<Page>.Empty,
    });

    public static byte[] SerializePage(Page page) => JsonSerializer.SerializeToUtf8Bytes(page, Indented);

    public static Page? DeserializePage(ReadOnlySpan<byte> json)
    {
        try
        {
            var page = JsonSerializer.Deserialize<Page>(json, Options);
            return page == null ? null : ProjectOps.NormalizePage(page);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

public sealed class InvalidProjectException : Exception
{
    public InvalidProjectException(string message, Exception? inner = null) : base(message, inner) { }
}
