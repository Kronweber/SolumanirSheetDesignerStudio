using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SolumanirSheetDesignerStudio.App.Services;
using SolumanirSheetDesignerStudio.Core.Export;
using SolumanirSheetDesignerStudio.Core.Layout;
using SolumanirSheetDesignerStudio.Core.Model;

namespace SolumanirSheetDesignerStudio.App.ViewModels;

/// <summary>
/// Context-sensitive properties panel (handoff 8.3). One object serves every selection kind: the view shows the
/// group matching <see cref="Kind"/>, and <see cref="Refresh"/> re-reads everything from the model after any change.
/// Setters turn into undoable edits.
/// </summary>
public sealed partial class InspectorViewModel : ObservableObject
{
    private readonly EditorViewModel _e;
    private ExportPlan? _plan;
    private int _loreSelectionStart, _loreSelectionEnd;

    public InspectorViewModel(EditorViewModel editor) => _e = editor;

    public void Refresh()
    {
        _plan = null;
        OnPropertyChanged(string.Empty);
    }

    private Page Page => _e.CurrentPage;
    private Selection Sel => _e.Selection;
    private ExportPlan Plan => _plan ??= ExportPlanner.Plan(_e.Layout, _e.CurrentExportOptions, _e.Settings.EffectiveExportThreads);

    public SelectionKind Kind => Sel.Kind;
    public bool IsSheet => Sel.Kind == SelectionKind.None;
    public bool IsSection => Sel.Kind == SelectionKind.Section;
    public bool IsSlot => Sel.Kind is SelectionKind.Slot or SelectionKind.Part;
    public bool IsLore => Sel.Kind == SelectionKind.Lore;
    public bool CanEdit => !_e.IsPreview;

    public string Heading => (Sel.Kind switch
    {
        SelectionKind.Section => Page.IsDetails(Sel.SectionId) ? Loc.T("Details row") : Loc.T("Row"),
        SelectionKind.Slot => Loc.T("Slot"),
        SelectionKind.Part => Loc.T("Slot part"),
        SelectionKind.Lore => Loc.T("Lore section"),
        _ => Loc.T("Sheet"),
    }).ToUpperInvariant();

    // =====================================================================================
    // Sheet: page text and style
    // =====================================================================================

    public string ProjectTitle
    {
        get => _e.Session.Project.Title;
        set { if (value != null) _e.SetProjectTitle(value); }
    }

    public string PageName
    {
        get => Page.Name;
        set { if (value != null && value != Page.Name) _e.SetFieldText(new TextFieldRef(TextFieldKind.PageName), value); }
    }

    public string Subtitle
    {
        get => Page.Subtitle;
        set { if (value != null && value != Page.Subtitle) _e.SetFieldText(new TextFieldRef(TextFieldKind.Subtitle), value); }
    }

    private SheetStyle Style => _e.EffectiveStyle;

    /// <summary>Nice to have from 4.7: this page carries its own style instead of the project's.</summary>
    public bool UsePageStyle
    {
        get => Page.Style != null;
        set { if (value != UsePageStyle) _e.SetPageStyleOverride(value); }
    }

    public string StyleScope => UsePageStyle ? Loc.T("These settings apply to this page only.") : Loc.T("These settings apply to every page of the project.");

    public IReadOnlyList<NamedColor> AccentPresets => Palette.Accents;
    public IReadOnlyList<NamedColor> PaperPresets => Palette.Papers;

    public string Accent
    {
        get => Style.Accent;
        set { if (ColorRgba.IsValidHex(value) && value != Style.Accent) _e.EditStyle(s => s with { Accent = Normalize(value) }, "accent"); }
    }

    public string Paper
    {
        get => Style.Paper;
        set { if (ColorRgba.IsValidHex(value) && value != Style.Paper) _e.EditStyle(s => s with { Paper = Normalize(value) }, "paper"); }
    }

    private static string Normalize(string hex) => ColorRgba.Parse(hex, default).ToHex();

    [RelayCommand] private void PickAccent(string? hex) { if (hex != null) Accent = hex; }
    [RelayCommand] private void PickPaper(string? hex) { if (hex != null) Paper = hex; }

    public double LoreWidth
    {
        get => Style.EffectiveLoreWidth;
        set
        {
            double v = Math.Round(Math.Clamp(value, 24, 52));
            if (v != Style.LoreWidth) _e.EditStyle(s => s with { LoreWidth = v }, "lore-width");
            OnPropertyChanged(nameof(LoreWidthText));
        }
    }

    public string LoreWidthText => Math.Round(Style.EffectiveLoreWidth) + " %";

    public bool SlotFrames
    {
        get => Style.SlotFrames;
        set { if (value != Style.SlotFrames) _e.EditStyle(s => s with { SlotFrames = value }, "frames"); }
    }

    public BorderKind[] BorderKinds { get; } = Enum.GetValues<BorderKind>();

    public BorderKind PageBorder
    {
        get => Style.PageBorder;
        set { if (value != Style.PageBorder) _e.EditStyle(s => s with { PageBorder = value }, "b-page"); }
    }

    public BorderKind RowBorder
    {
        get => Style.RowBorder;
        set { if (value != Style.RowBorder) _e.EditStyle(s => s with { RowBorder = value }, "b-row"); }
    }

    public BorderKind DetailsBorder
    {
        get => Style.DetailsBorder;
        set { if (value != Style.DetailsBorder) _e.EditStyle(s => s with { DetailsBorder = value }, "b-details"); }
    }

    public BorderKind LoreBorder
    {
        get => Style.LoreBorder;
        set { if (value != Style.LoreBorder) _e.EditStyle(s => s with { LoreBorder = value }, "b-lore"); }
    }

    public string SheetReadout
    {
        get
        {
            var p = Plan;
            string s = Loc.T("Exports at {0} ({1:0.##}× scale)", Format.Pixels(p.Width, p.Height), p.Scale);
            if (p.Capped) s += "\n" + Loc.T("Limited by the max long edge setting.");
            return s;
        }
    }

    // =====================================================================================
    // Row / details row
    // =====================================================================================

    private Section? Sec => Page.FindSection(Sel.SectionId);

    private void EditSection(string label, Func<Section, Section> f, string key)
    {
        if (Sec is not { } s) return;
        _e.Edit(label, p => p.UpdateSection(s.Id, f), $"section:{key}:{s.Id}");
    }

    public string SectionName
    {
        get => Sec?.Name ?? "";
        set { if (value != null && Sec is { } s && value != s.Name) _e.SetFieldText(new TextFieldRef(TextFieldKind.SectionName, s.Id), value); }
    }

    public bool IsDetailsSection => Page.IsDetails(Sel.SectionId);

    /// <summary>0 = Fixed height, 1 = Auto height.</summary>
    public int SizingModeIndex
    {
        get => Sec?.Sizing.Mode == SizingMode.Auto ? 1 : 0;
        set
        {
            var mode = value == 1 ? SizingMode.Auto : SizingMode.Fixed;
            if (Sec != null && Sec.Sizing.Mode != mode) EditSection(Loc.T("change row sizing"), s => s with { Sizing = s.Sizing with { Mode = mode } }, "mode");
        }
    }

    public bool IsFixedSizing => Sec?.Sizing.Mode != SizingMode.Auto;
    public bool IsAutoSizing => Sec?.Sizing.Mode == SizingMode.Auto;

    public decimal? SectionHeight
    {
        get => Sec == null ? null : (decimal)Math.Round(Sec.Sizing.EffectiveHeight);
        set
        {
            if (value is not { } v || Sec == null) return;
            double h = Math.Clamp((double)v, 40, 4000);
            if (h != Sec.Sizing.Height) EditSection(Loc.T("change row height"), s => s with { Sizing = s.Sizing with { Height = h } }, "height");
        }
    }

    /// <summary>Empty = no limit.</summary>
    public decimal? SectionMaxHeight
    {
        get => Sec?.Sizing.MaxHeight is { } m && m > 0 ? (decimal)Math.Round(m) : null;
        set
        {
            if (Sec == null) return;
            // No lower clamp here: "150" is typed as 1, 15, 150. The layout applies the minimum.
            double? m = value is { } v && v > 0 ? Math.Min((double)v, Sizing.MaxHeightLimit) : null;
            if (m != Sec.Sizing.MaxHeight) EditSection(Loc.T("change max height"), s => s with { Sizing = s.Sizing with { MaxHeight = m } }, "max");
        }
    }

    /// <summary>Empty = each file's native resolution.</summary>
    public decimal? SectionExportPx
    {
        get => Sec?.Sizing.ExportPx is { } e && e > 0 ? (decimal)Math.Round(e) : null;
        set
        {
            if (Sec == null) return;
            double? px = value is { } v && v > 0 ? Math.Clamp((double)v, 1, 100000) : null;
            if (px != Sec.Sizing.ExportPx) EditSection(Loc.T("change export height"), s => s with { Sizing = s.Sizing with { ExportPx = px } }, "export");
        }
    }

    public string SectionReadout
    {
        get
        {
            var layout = _e.Layout.FindSection(Sel.SectionId);
            if (layout == null || layout.Slots.Count == 0) return Loc.T("No slots yet.");
            var area = layout.Slots[0].Area;
            double s = Plan.Scale;
            string size = Format.Pixels((int)Math.Round((area.W - 2) * s), (int)Math.Round((area.H - 2) * s));
            string text = Loc.T("Exports at {0} per image area ({1:0.##}× scale)", size, s);
            if (layout.Section.Sizing.EffectiveExportPx > 0)
                text += "\n" + Loc.T("Each image is rendered {0} px tall.", Math.Round(layout.Section.Sizing.EffectiveExportPx));
            return text;
        }
    }

    public int SlotCount => Sec?.Slots.Count ?? 0;

    [RelayCommand] private void AddSlotHere() { if (Sec is { } s) _e.AddSlotTo(s.Id); }

    // =====================================================================================
    // Slot / part
    // =====================================================================================

    private (Section Section, Slot Slot, int Index)? SlotRef => Page.FindSlot(Sel.SlotId ?? Page.FindPart(Sel.PartId)?.Slot.Id);

    /// <summary>The part the panel edits: the selected one, or the only/first part of the selected slot.</summary>
    private Part? CurrentPart
    {
        get
        {
            if (Sel.Kind == SelectionKind.Part) return Page.FindPart(Sel.PartId)?.Part;
            return SlotRef?.Slot.Parts[0];
        }
    }

    public string SlotLabel
    {
        get => SlotRef?.Slot.Label ?? "";
        set { if (value != null && SlotRef is { } r && value != r.Slot.Label) _e.SetFieldText(new TextFieldRef(TextFieldKind.SlotLabel, r.Slot.Id), value); }
    }

    public bool IsSplit => SlotRef?.Slot.Parts.Count > 1;

    public string PartPosition
    {
        get
        {
            if (SlotRef is not { } r) return "";
            int n = r.Slot.Parts.Count;
            if (n <= 1) return Loc.T("Single image");
            int i = CurrentPart == null ? 0 : r.Slot.Parts.IndexOf(CurrentPart);
            return Sel.Kind == SelectionKind.Part ? Loc.T("Part {0} of {1}", i + 1, n) : Loc.T("Split into {0} parts — click a part to edit it", n);
        }
    }

    public bool HasImage => CurrentPart?.ImageId != null && _e.Session.Assets.Get(CurrentPart.ImageId) != null;
    public bool IsImageMissing => CurrentPart?.ImageId != null && _e.Session.Assets.Get(CurrentPart.ImageId) == null;

    public string ImageName => CurrentPart?.ImageId is { } id ? _e.Session.Assets.Get(id)?.FileName ?? Loc.T("Missing image") : Loc.T("No image");

    public string ImageDetails
    {
        get
        {
            var a = _e.Session.Assets.Get(CurrentPart?.ImageId);
            return a == null ? "" : Loc.T("Source: {0} · {1}", Format.Pixels(a.PixelWidth, a.PixelHeight), Format.Bytes(a.ByteLength));
        }
    }

    private bool IsWeight => IsSplit && SlotRef?.Section.Sizing.Mode == SizingMode.Fixed;

    public string ScaleLabel => IsWeight ? Loc.T("Height share (weight)") : Loc.T("Scale %");

    public string ScaleHint => IsWeight
        ? Loc.T("5–1000. Parts share the slot's height by weight: 150 and 100 give 60 % and 40 %.")
        : Loc.T("5–100. Shrinks the image inside its slot.");

    public decimal ScaleMaximum => IsWeight ? 1000 : 100;

    public decimal? PartScale
    {
        get => CurrentPart == null ? null : (decimal)Math.Min((double)ScaleMaximum, CurrentPart.Weight);
        set
        {
            if (value is not { } v || CurrentPart is not { } part) return;
            double scale = Math.Clamp((double)v, 5, (double)ScaleMaximum);
            if (scale != part.Scale) _e.SetPartScale(part.Id, scale);
        }
    }

    /// <summary>The same number for the slider (dragging it is one undo step: the edits share a key).</summary>
    public double PartScaleSlider
    {
        get => (double)(PartScale ?? 100);
        set => PartScale = (decimal)Math.Round(value);
    }

    public double ScaleSliderMaximum => (double)ScaleMaximum;

    private ImagePlacement? Placement => CurrentPart == null ? null : _e.Layout.FindPart(CurrentPart.Id)?.Image;

    public string ExportReadout
    {
        get
        {
            if (Placement is not { } p) return "";
            var (w, h) = ExportPlanner.OutputSize(p, Plan.Scale);
            return Loc.T("Exports at {0}", Format.Pixels(w, h));
        }
    }

    public bool IsUpscaled => Placement is { } p && ExportPlanner.IsUpscaled(p, Plan.Scale);

    public string UpscaleWarning
    {
        get
        {
            if (Placement is not { } p || !IsUpscaled) return "";
            double factor = Plan.Scale / ExportPlanner.NativeScale(p);
            return Loc.T("Upscaled {0:0.0}× in the current export settings — the source has fewer pixels than the export needs.", factor);
        }
    }

    [RelayCommand]
    private Task ReplaceImageAsync() => CurrentPart is { } part ? _e.BrowseImageAsync(part.Id) : Task.CompletedTask;

    [RelayCommand]
    private void ClearImage() { if (CurrentPart is { } part) _e.ClearImage(part.Id); }

    [RelayCommand]
    private void RevealInLibrary()
    {
        if (CurrentPart?.ImageId is not { } id) return;
        _e.Main.ShowLibrary();
        _e.Library.Select(id);
    }

    [RelayCommand]
    private void Split() { if (SlotRef is { } r) _e.SplitSlot(r.Slot.Id); }

    [RelayCommand]
    private void RemovePart()
    {
        if (SlotRef is not { } r || r.Slot.Parts.Count <= 1) return;
        var part = Sel.Kind == SelectionKind.Part ? CurrentPart : r.Slot.Parts[^1];
        if (part != null) _e.RemovePart(part.Id);
    }

    // =====================================================================================
    // Lore section
    // =====================================================================================

    private LoreSection? LoreSec => Page.FindLore(Sel.LoreId);

    public string LoreTitle
    {
        get => LoreSec?.Title ?? "";
        set { if (value != null && LoreSec is { } l && value != l.Title) _e.SetFieldText(new TextFieldRef(TextFieldKind.LoreTitle, l.Id), value); }
    }

    public string LoreBody
    {
        get => LoreSec?.Body ?? "";
        set { if (value != null && LoreSec is { } l && value != l.Body) _e.SetFieldText(new TextFieldRef(TextFieldKind.LoreBody, l.Id), value); }
    }

    public int LoreSelectionStart
    {
        get => _loreSelectionStart;
        set => SetProperty(ref _loreSelectionStart, value);
    }

    public int LoreSelectionEnd
    {
        get => _loreSelectionEnd;
        set => SetProperty(ref _loreSelectionEnd, value);
    }

    public string CheatSheet => Loc.T(
        "**bold**   *italic*   ~~strike~~   `code`\n# Heading … ###### smallest\n- list item      1. numbered item\n> quote\n[link text](address)\n--- (a rule on its own line)\n| A | B |\n|---|---|\n| 1 | 2 |\nA single line break starts a new line.");

    /// <summary>Formatting toolbar: wraps the selection or prefixes the selected lines.</summary>
    [RelayCommand]
    private void ApplyFormat(string? kind)
    {
        if (LoreSec is not { } lore || kind == null) return;
        string text = lore.Body;
        int a = Math.Clamp(Math.Min(_loreSelectionStart, _loreSelectionEnd), 0, text.Length);
        int b = Math.Clamp(Math.Max(_loreSelectionStart, _loreSelectionEnd), 0, text.Length);
        string selected = text[a..b];
        string result;
        int selStart, selEnd;

        (string Open, string Close, string Placeholder)? wrap = kind switch
        {
            "bold" => ("**", "**", Loc.T("bold text")),
            "italic" => ("*", "*", Loc.T("italic text")),
            "strike" => ("~~", "~~", Loc.T("struck text")),
            "code" => ("`", "`", Loc.T("code")),
            "link" => ("[", "](https://)", Loc.T("link text")),
            _ => null,
        };

        if (wrap is { } w)
        {
            string inner = selected.Length > 0 ? selected : w.Placeholder;
            result = text[..a] + w.Open + inner + w.Close + text[b..];
            selStart = a + w.Open.Length;
            selEnd = selStart + inner.Length;
        }
        else if (kind is "rule" or "table")
        {
            string block = kind == "rule" ? "---" : "| Column | Column |\n|---|---|\n| value | value |";
            string before = a > 0 && text[a - 1] != '\n' ? "\n\n" : a > 1 && text[a - 2] != '\n' ? "\n" : "";
            string insert = before + block + "\n\n";
            result = text[..a] + insert + text[b..];
            selStart = selEnd = a + insert.Length;
        }
        else
        {
            string prefix = kind switch { "heading" => "### ", "list" => "- ", "numbered" => "1. ", "quote" => "> ", _ => "" };
            if (prefix.Length == 0) return;
            // Extend to whole lines, then prefix each of them.
            int lineStart = a == 0 ? 0 : text.LastIndexOf('\n', a - 1) + 1;
            int lineEnd = text.IndexOf('\n', b);
            if (lineEnd < 0) lineEnd = text.Length;
            string[] lines = text[lineStart..lineEnd].Split('\n');
            for (int i = 0; i < lines.Length; i++)
                lines[i] = (kind == "numbered" ? (i + 1) + ". " : prefix) + lines[i];
            string block = string.Join('\n', lines);
            result = text[..lineStart] + block + text[lineEnd..];
            selStart = lineStart;
            selEnd = lineStart + block.Length;
        }

        _e.Session.BreakCoalescing();
        LoreBody = result;
        _e.Session.BreakCoalescing();
        LoreSelectionStart = selStart;
        LoreSelectionEnd = selEnd;
    }

    [RelayCommand]
    private void EditOnSheet() { if (LoreSec is { } l) _e.BeginEdit(new TextFieldRef(TextFieldKind.LoreBody, l.Id)); }
}
