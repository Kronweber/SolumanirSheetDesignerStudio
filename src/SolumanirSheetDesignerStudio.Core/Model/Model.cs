using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace SolumanirSheetDesignerStudio.Core.Model;

public static class AppInfo
{
    public const string Name = "Solumanir Sheet Designer Studio";
    public const string Author = "Kronweber";
    public const string DataFolderName = "SolumanirSheetDesignerStudio";
    public const string ProjectExtension = ".speciesproj";
    public const int FormatVersion = 2;
}

public enum SizingMode { Fixed, Auto }

public enum BorderKind { Plain, Ornate, None }

public sealed record NamedColor(string Name, string Hex);

public static class Palette
{
    public const string Ink = "#2A211A";
    public const string Gold = "#C9A154";

    public const string Parchment = "#EFE6D2";
    public const string Ivory = "#F5F1E6";
    public const string Stone = "#E3E1DA";

    public const string Oxblood = "#7A2318";
    public const string Forest = "#2F4A34";
    public const string Navy = "#253C5C";
    public const string Charcoal = "#3A3330";

    public static readonly IReadOnlyList<NamedColor> Papers = new NamedColor[]
    {
        new("Parchment", Parchment), new("Ivory", Ivory), new("Stone", Stone),
    };

    public static readonly IReadOnlyList<NamedColor> Accents = new NamedColor[]
    {
        new("Oxblood", Oxblood), new("Forest", Forest), new("Navy", Navy), new("Charcoal", Charcoal),
    };
}

public static class Ids
{
    public static string New() => Guid.NewGuid().ToString("N")[..12];
}

/// <summary>Per-project look of the sheet (section 4.7 of the handoff).</summary>
public sealed record SheetStyle
{
    public string Accent { get; init; } = Palette.Oxblood;
    public string Paper { get; init; } = Palette.Parchment;
    /// <summary>Lore column width in percent of the body width (24–52).</summary>
    public double LoreWidth { get; init; } = 30;
    public bool SlotFrames { get; init; } = true;
    public BorderKind PageBorder { get; init; } = BorderKind.None;
    public BorderKind RowBorder { get; init; } = BorderKind.None;
    public BorderKind DetailsBorder { get; init; } = BorderKind.None;
    public BorderKind LoreBorder { get; init; } = BorderKind.Ornate;

    public static SheetStyle Default { get; } = new();

    [JsonIgnore] public double EffectiveLoreWidth => Math.Clamp(double.IsFinite(LoreWidth) ? LoreWidth : 30, 24, 52);
}

public sealed record Sizing
{
    /// <summary>Image-area height of a model row unless the project says otherwise (sheet units).</summary>
    public const double DefaultHeight = 440;
    /// <summary>Image-area height of a new page's details row.</summary>
    public const double DefaultDetailsHeight = 190;
    public const double MinHeight = 40;
    /// <summary>Smallest cap that still shows an image.</summary>
    public const double MinMaxHeight = 20;
    /// <summary>Upper bounds that keep a damaged or hand-edited value from asking for an absurdly large sheet.</summary>
    public const double MaxHeightLimit = 20000;
    public const double MaxExportPx = 65535;

    public SizingMode Mode { get; init; } = SizingMode.Fixed;
    /// <summary>Image-area height in sheet units (Fixed mode).</summary>
    public double Height { get; init; } = DefaultHeight;
    /// <summary>Optional cap on image height (Auto mode).</summary>
    public double? MaxHeight { get; init; }
    /// <summary>Optional pixel height each image of the row gets in the exported PNG.</summary>
    public double? ExportPx { get; init; }

    [JsonIgnore] public double EffectiveHeight => Math.Clamp(double.IsFinite(Height) && Height > 0 ? Height : DefaultHeight, MinHeight, MaxHeightLimit);
    /// <summary>0 = no cap.</summary>
    [JsonIgnore] public double EffectiveMaxHeight => MaxHeight is { } m && double.IsFinite(m) && m > 0 ? Math.Clamp(m, MinMaxHeight, MaxHeightLimit) : 0;
    /// <summary>0 = not set.</summary>
    [JsonIgnore] public double EffectiveExportPx => ExportPx is { } e && double.IsFinite(e) && e > 0 ? Math.Min(e, MaxExportPx) : 0;
}

public sealed record Part
{
    public string Id { get; init; } = Ids.New();
    public string? ImageId { get; init; }
    /// <summary>Percent. Unsplit slot: 5–100 box scale. Split slot in a Fixed row: weight 5–1000.</summary>
    public double Scale { get; init; } = 100;

    /// <summary>Scale clamped to 5–1000 (0 / NaN → 100), as the prototype does.</summary>
    [JsonIgnore] public double Weight => Math.Clamp(double.IsFinite(Scale) && Scale != 0 ? Scale : 100, 5, 1000);
}

public sealed record Slot
{
    public string Id { get; init; } = Ids.New();
    public string Label { get; init; } = "";
    public ImmutableList<Part> Parts { get; init; } = ImmutableList.Create(new Part());

    [JsonIgnore] public bool IsSplit => Parts.Count > 1;
}

/// <summary>A model row or the details row.</summary>
public sealed record Section
{
    public string Id { get; init; } = Ids.New();
    public string Name { get; init; } = "";
    public Sizing Sizing { get; init; } = new();
    public ImmutableList<Slot> Slots { get; init; } = ImmutableList<Slot>.Empty;
}

public sealed record LoreSection
{
    public string Id { get; init; } = Ids.New();
    public string Title { get; init; } = "";
    public string Body { get; init; } = "";
}

public sealed record Page
{
    public string Id { get; init; } = Ids.New();
    public string Name { get; init; } = "";
    public string Subtitle { get; init; } = "";
    public ImmutableList<Section> Rows { get; init; } = ImmutableList<Section>.Empty;
    public Section Details { get; init; } = Factory.NewDetails();
    public ImmutableList<LoreSection> Lore { get; init; } = ImmutableList<LoreSection>.Empty;
    /// <summary>Optional per-page override of the project style.</summary>
    public SheetStyle? Style { get; init; }
}

/// <summary>The undoable document. Image bytes live in the asset library, referenced by id.</summary>
public sealed record Project
{
    public string Title { get; init; } = "My World";
    public SheetStyle Style { get; init; } = SheetStyle.Default;
    public ImmutableList<Page> Pages { get; init; } = ImmutableList<Page>.Empty;
}

public static class Factory
{
    /// <summary>Translates the default names given to new elements (set once by the app; identity by default).</summary>
    public static Func<string, string> Translate { get; set; } = text => text;

    private static string T(string text) => Translate(text);

    public const string DefaultLoreBody =
        "Click this text to edit. **Markdown** is supported:\n\n- *italic*, **bold**, `code`\n- lists, > quotes, tables, `---` rules\n\n### Sub-headings too";

    public static Slot NewSlot(string label) => new() { Label = label };

    public static Section NewRow(double height = Sizing.DefaultHeight) => new()
    {
        Name = T("New row"),
        Sizing = new Sizing { Height = height },
        Slots = ImmutableList.Create(NewSlot(T("Base model")), NewSlot(T("Outfit"))),
    };

    public static LoreSection NewLore() => new() { Title = T("New section"), Body = "" };

    /// <summary>An empty details row with the default title and height.</summary>
    public static Section NewDetails() => new() { Name = T("Distinguishing Features"), Sizing = new Sizing { Height = Sizing.DefaultDetailsHeight } };

    public static Page NewPage(double rowHeight = Sizing.DefaultHeight) => new()
    {
        Name = T("New Species"),
        Subtitle = "",
        Rows = ImmutableList.Create(new Section
        {
            Name = T("Female"),
            Sizing = new Sizing { Height = rowHeight },
            Slots = ImmutableList.Create(NewSlot(T("Base model")), NewSlot(T("Everyday")), NewSlot(T("Formal"))),
        }),
        Details = NewDetails() with { Slots = ImmutableList.Create(NewSlot(T("Ears")), NewSlot(T("Tail")), NewSlot(T("Markings"))) },
        Lore = ImmutableList.Create(new LoreSection { Title = T("Overview"), Body = T(DefaultLoreBody) }),
    };

    public static Project NewProject(SheetStyle? style = null, double rowHeight = Sizing.DefaultHeight, Page? firstPage = null) => new()
    {
        Title = T("My World"),
        Style = style ?? SheetStyle.Default,
        Pages = ImmutableList.Create(firstPage ?? NewPage(rowHeight)),
    };
}
