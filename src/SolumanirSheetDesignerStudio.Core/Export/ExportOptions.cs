using System.Collections.Immutable;
using System.Text;
using SolumanirSheetDesignerStudio.Core.Model;

namespace SolumanirSheetDesignerStudio.Core.Export;

public enum ExportScope { CurrentPage, SelectedPages, AllPages }

public enum ExportScaleMode { Auto, X2, X4, X8, CustomScale, TargetWidth }

public enum PngCompression { Fast, Balanced, Small }

/// <summary>Export dialog choices. Stored per project (last used) and in the app settings (defaults).</summary>
public sealed record ExportOptions
{
    public const int DefaultMaxLongEdge = 30000;
    public const int MaxPngSide = 65535;
    public const string DefaultPattern = "{name}.png";

    public ExportScope Scope { get; init; } = ExportScope.CurrentPage;
    public ImmutableList<string> SelectedPageIds { get; init; } = ImmutableList<string>.Empty;
    public string? Folder { get; init; }
    /// <summary>Tokens: {name} species name, {index} page number, {project} project title.</summary>
    public string NamePattern { get; init; } = DefaultPattern;
    public ExportScaleMode ScaleMode { get; init; } = ExportScaleMode.Auto;
    public double CustomScale { get; init; } = 3;
    public int TargetWidth { get; init; } = 4800;
    /// <summary>Cap on the longer output side in pixels; 0 = unlimited.</summary>
    public int MaxLongEdge { get; init; } = DefaultMaxLongEdge;
    public PngCompression Compression { get; init; } = PngCompression.Balanced;
    public bool UseGpu { get; init; }
    public bool TransparentBackground { get; init; }
    public bool OpenFolderAfter { get; init; }

    /// <summary>The same options with whatever a hand-edited file left null or not finite put back to its default.</summary>
    public ExportOptions GetSanitized() => this with
    {
        SelectedPageIds = SelectedPageIds ?? ImmutableList<string>.Empty,
        CustomScale = double.IsFinite(CustomScale) ? CustomScale : new ExportOptions().CustomScale,
    };
}

public static class FileNaming
{
    private static readonly char[] Invalid = Path.GetInvalidFileNameChars().Concat("<>:\"/\\|?*").Distinct().ToArray();

    public static string Sanitize(string? name, string fallback = "untitled")
    {
        if (string.IsNullOrWhiteSpace(name)) return fallback;
        var sb = new StringBuilder(name.Length);
        foreach (char c in name) sb.Append(Array.IndexOf(Invalid, c) >= 0 || char.IsControl(c) ? ' ' : c);
        string s = string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim('.', ' ');
        if (s.Length > 120) s = s[..120].TrimEnd();
        return s.Length == 0 ? fallback : s;
    }

    /// <summary>Expands the naming pattern for one page; always ends in ".png".</summary>
    public static string Expand(string? pattern, Project project, Page page, int pageIndex)
    {
        if (string.IsNullOrWhiteSpace(pattern)) pattern = ExportOptions.DefaultPattern;
        string name = pattern
            .Replace("<Species name>", "{name}", StringComparison.OrdinalIgnoreCase)
            .Replace("{name}", Sanitize(page.Name), StringComparison.OrdinalIgnoreCase)
            .Replace("{index}", (pageIndex + 1).ToString("00"), StringComparison.OrdinalIgnoreCase)
            .Replace("{project}", Sanitize(project.Title), StringComparison.OrdinalIgnoreCase);
        if (name.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        return Sanitize(name) + ".png";
    }

    /// <summary>Expands the pattern for several pages, making duplicate names unique with " (2)", " (3)"…</summary>
    public static IReadOnlyList<string> ExpandAll(string? pattern, Project project, IReadOnlyList<Page> pages)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>(pages.Count);
        foreach (var page in pages)
        {
            string file = Expand(pattern, project, page, project.PageIndex(page.Id));
            string stem = file[..^4], candidate = file;
            for (int n = 2; !used.Add(candidate); n++) candidate = $"{stem} ({n}).png";
            result.Add(candidate);
        }
        return result;
    }
}
