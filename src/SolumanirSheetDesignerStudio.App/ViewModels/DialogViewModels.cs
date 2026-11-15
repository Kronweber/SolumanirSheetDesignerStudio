using System.Collections.Immutable;
using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SolumanirSheetDesignerStudio.App.Services;
using SolumanirSheetDesignerStudio.Core.Export;
using SolumanirSheetDesignerStudio.Core.IO;
using SolumanirSheetDesignerStudio.Core.Layout;
using SolumanirSheetDesignerStudio.Core.Model;
using SolumanirSheetDesignerStudio.Core.Settings;

namespace SolumanirSheetDesignerStudio.App.ViewModels;

// =====================================================================================
// Export dialog (handoff 6.4)
// =====================================================================================

public sealed partial class ExportPageRow : ObservableObject
{
    public ExportPageRow(Page page, int number)
    {
        Page = page;
        Number = number;
    }

    public Page Page { get; }
    public int Number { get; }
    public string Name => string.IsNullOrWhiteSpace(Page.Name) ? Loc.T("Untitled") : Page.Name;
    public PageLayout? Layout { get; set; }

    [ObservableProperty] private bool _isChecked;
    [ObservableProperty] private bool _isIncluded;
    [ObservableProperty] private string _size = "";
    [ObservableProperty] private string _note = "";
}

public sealed partial class ExportDialogViewModel : ObservableObject
{
    private readonly EditorViewModel _editor;
    private bool _loading = true;

    public ExportDialogViewModel(EditorViewModel editor, IReadOnlyList<string> selectedPageIds)
    {
        _editor = editor;
        var o = editor.CurrentExportOptions;
        var project = editor.Session.Project;

        var preselected = o.SelectedPageIds.Count > 0 && selectedPageIds.Count <= 1 ? o.SelectedPageIds.ToHashSet() : selectedPageIds.ToHashSet();
        for (int i = 0; i < project.Pages.Count; i++)
        {
            var row = new ExportPageRow(project.Pages[i], i + 1) { IsChecked = preselected.Contains(project.Pages[i].Id) };
            row.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ExportPageRow.IsChecked)) Recompute(); };
            Rows.Add(row);
        }

        _scopeIndex = selectedPageIds.Count > 1 ? 1 : (int)o.Scope;
        _folder = o.Folder ?? editor.Settings.ExportDefaults.Folder ?? (editor.Session.FilePath is { } f ? Path.GetDirectoryName(f) ?? "" : Environment.GetFolderPath(Environment.SpecialFolder.MyPictures));
        _namePattern = string.IsNullOrWhiteSpace(o.NamePattern) ? ExportOptions.DefaultPattern : o.NamePattern;
        _scaleModeIndex = (int)o.ScaleMode;
        _customScale = (decimal)o.CustomScale;
        _targetWidth = o.TargetWidth;
        _unlimitedSize = o.MaxLongEdge <= 0;
        _maxLongEdge = o.MaxLongEdge > 0 ? o.MaxLongEdge : ExportOptions.DefaultMaxLongEdge;
        _compressionIndex = (int)o.Compression;
        _useGpu = o.UseGpu;
        _transparent = o.TransparentBackground;
        _openFolder = o.OpenFolderAfter;
        _loading = false;
        Recompute();
    }

    public ObservableCollection<ExportPageRow> Rows { get; } = new();

    [ObservableProperty] private int _scopeIndex;
    [ObservableProperty] private string _folder;
    [ObservableProperty] private string _namePattern;
    [ObservableProperty] private int _scaleModeIndex;
    [ObservableProperty] private decimal? _customScale;
    [ObservableProperty] private decimal? _targetWidth;
    [ObservableProperty] private decimal? _maxLongEdge;
    [ObservableProperty] private bool _unlimitedSize;
    [ObservableProperty] private int _compressionIndex;
    [ObservableProperty] private bool _useGpu;
    [ObservableProperty] private bool _transparent;
    [ObservableProperty] private bool _openFolder;
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string _warning = "";
    [ObservableProperty] private string _fileExample = "";
    [ObservableProperty] private bool _canExport;

    public string TokenHint => Loc.T("Tokens: {name} species name · {index} page number · {project} project title");
    public bool IsCustomScale => ScaleModeIndex == (int)ExportScaleMode.CustomScale;
    public bool IsTargetWidth => ScaleModeIndex == (int)ExportScaleMode.TargetWidth;
    public bool IsSelectedScope => ScopeIndex == (int)ExportScope.SelectedPages;
    public bool IsSizeLimited => !UnlimitedSize;
    public bool GpuAvailable => _editor.Main.GpuRenderer is { IsAvailable: true };
    public string GpuHint => GpuAvailable
        ? Loc.T("Strips that fit the GPU's texture size are drawn on the graphics card; the rest fall back to the CPU.")
        : Loc.T("No GPU renderer is available in this session; the CPU is used.");

    partial void OnScopeIndexChanged(int value) { OnPropertyChanged(nameof(IsSelectedScope)); Recompute(); }
    partial void OnScaleModeIndexChanged(int value) { OnPropertyChanged(nameof(IsCustomScale)); OnPropertyChanged(nameof(IsTargetWidth)); Recompute(); }
    partial void OnCustomScaleChanged(decimal? value) => Recompute();
    partial void OnTargetWidthChanged(decimal? value) => Recompute();
    partial void OnMaxLongEdgeChanged(decimal? value) => Recompute();
    partial void OnUnlimitedSizeChanged(bool value) { OnPropertyChanged(nameof(IsSizeLimited)); Recompute(); }
    partial void OnCompressionIndexChanged(int value) => Recompute();
    partial void OnFolderChanged(string value) => Recompute();
    partial void OnNamePatternChanged(string value) => Recompute();

    public ExportOptions BuildOptions() => new()
    {
        Scope = (ExportScope)Math.Clamp(ScopeIndex, 0, 2),
        SelectedPageIds = Rows.Where(r => r.IsChecked).Select(r => r.Page.Id).ToImmutableList(),
        Folder = string.IsNullOrWhiteSpace(Folder) ? null : Folder.Trim(),
        NamePattern = string.IsNullOrWhiteSpace(NamePattern) ? ExportOptions.DefaultPattern : NamePattern.Trim(),
        ScaleMode = (ExportScaleMode)Math.Clamp(ScaleModeIndex, 0, 5),
        CustomScale = (double)Math.Clamp(CustomScale ?? 2, 0.1m, 1000m),
        TargetWidth = (int)Math.Clamp(TargetWidth ?? 4800, 16, 400000),
        MaxLongEdge = UnlimitedSize ? 0 : (int)Math.Clamp(MaxLongEdge ?? ExportOptions.DefaultMaxLongEdge, 256, 400000),
        Compression = (PngCompression)Math.Clamp(CompressionIndex, 0, 2),
        UseGpu = UseGpu && GpuAvailable,
        TransparentBackground = Transparent,
        OpenFolderAfter = OpenFolder,
    };

    public static IReadOnlyList<Page> ResolvePages(Project project, string currentPageId, ExportOptions o) => o.Scope switch
    {
        ExportScope.AllPages => project.Pages,
        ExportScope.SelectedPages => project.Pages.Where(p => o.SelectedPageIds.Contains(p.Id)).ToList(),
        _ => project.FindPage(currentPageId) is { } p ? new[] { p } : Array.Empty<Page>(),
    };

    /// <summary>Recomputes the output size of every page so the result is known before anything is rendered.</summary>
    private void Recompute()
    {
        if (_loading) return;
        var o = BuildOptions();
        var session = _editor.Session;
        var included = ResolvePages(session.Project, session.CurrentPageId, o).Select(p => p.Id).ToHashSet();
        int workers = _editor.Settings.EffectiveExportThreads;

        long file = 0, memory = 0;
        int capped = 0, tooLarge = 0, count = 0;
        foreach (var row in Rows)
        {
            row.Layout ??= _editor.Engine.Layout(row.Page, session.Project.Style);
            var plan = ExportPlanner.Plan(row.Layout, o, workers);
            row.IsIncluded = included.Contains(row.Page.Id);
            row.Size = Format.Pixels(plan.Width, plan.Height);
            var notes = new List<string> { Loc.T("{0:0.##}×", plan.Scale) };
            if (plan.Capped) notes.Add(Loc.T("capped"));
            if (plan.UpscaledImages > 0) notes.Add(Loc.T("{0} upscaled", plan.UpscaledImages));
            if (plan.DownscaledImages > 0) notes.Add(Loc.T("{0} below native", plan.DownscaledImages));
            row.Note = string.Join(" · ", notes);
            if (!row.IsIncluded) continue;
            count++;
            file += plan.EstimatedFileBytes;
            memory = Math.Max(memory, plan.EstimatedPeakMemoryBytes);
            if (plan.Capped) capped++;
            if (plan.ExceedsViewerLimit) tooLarge++;
        }

        Summary = count == 0
            ? Loc.T("No pages selected.")
            : Loc.T("{0} page(s) · up to about {1} on disk (often far less) · up to {2} of memory while rendering", count, Format.Bytes(file), Format.Bytes(memory));
        var warnings = new List<string>();
        if (capped > 0) warnings.Add(Loc.T("{0} page(s) are limited by the max long edge and will be smaller than their native resolution.", capped));
        if (tooLarge > 0) warnings.Add(Loc.T("{0} page(s) exceed 65 535 px on a side; many viewers and editors cannot open such files.", tooLarge));
        else if (UnlimitedSize) warnings.Add(Loc.T("Without a size limit the output can exceed what viewers and editors can open."));
        Warning = string.Join("\n", warnings);

        var first = session.Project.Pages.FirstOrDefault(p => included.Contains(p.Id)) ?? session.CurrentPage;
        FileExample = Loc.T("e.g. {0}", FileNaming.Expand(o.NamePattern, session.Project, first, session.Project.PageIndex(first.Id)));
        CanExport = count > 0 && !string.IsNullOrWhiteSpace(Folder);
    }

    [RelayCommand]
    private async Task BrowseFolderAsync()
    {
        string? picked = await _editor.Main.Dialogs.PickFolderAsync(Loc.T("Export folder"), Directory.Exists(Folder) ? Folder : null);
        if (picked != null) Folder = picked;
    }

    [RelayCommand]
    private void CheckAll()
    {
        bool all = Rows.All(r => r.IsChecked);
        foreach (var r in Rows) r.IsChecked = !all;
    }
}

// =====================================================================================
// Import pages
// =====================================================================================

public sealed partial class ImportPageRow : ObservableObject
{
    public ImportPageRow(Page page, Bitmap? thumbnail)
    {
        Page = page;
        Thumbnail = thumbnail;
    }

    public Page Page { get; }
    public Bitmap? Thumbnail { get; }
    public string Name => string.IsNullOrWhiteSpace(Page.Name) ? Loc.T("Untitled") : Page.Name;
    public string Details => Loc.T("{0} row(s) · {1} image(s)", Page.Rows.Count, Page.ImageIds().Distinct().Count());

    [ObservableProperty] private bool _isChecked;
}

public sealed partial class ImportPagesViewModel : ObservableObject
{
    public ImportPagesViewModel(LoadedProject loaded, string path)
    {
        Title = Loc.T("Import pages from \"{0}\"", string.IsNullOrWhiteSpace(loaded.Project.Title) ? Path.GetFileNameWithoutExtension(path) : loaded.Project.Title);
        foreach (var page in loaded.Project.Pages)
            Items.Add(new ImportPageRow(page, loaded.Thumbs.TryGetValue(page.Id, out var png) ? SkiaInterop.FromPng(png) : null));
    }

    public string Title { get; }
    public ObservableCollection<ImportPageRow> Items { get; } = new();

    [RelayCommand]
    private void CheckAll()
    {
        bool all = Items.All(r => r.IsChecked);
        foreach (var r in Items) r.IsChecked = !all;
    }
}

// =====================================================================================
// Settings dialog (handoff 8.2)
// =====================================================================================

public sealed partial class SettingsDialogViewModel : ObservableObject
{
    private readonly IDialogService _dialogs;

    public SettingsDialogViewModel(AppSettings current, IDialogService dialogs)
    {
        _dialogs = dialogs;
        _s = current.Clone();
        Templates = new[] { "" }.Concat(EditorViewModel.ListTemplates()).ToList();
    }

    /// <summary>The edited copy; plain properties are bound directly (e.g. <c>S.ConfirmDelete</c>).</summary>
    [ObservableProperty] private AppSettings _s;

    public IReadOnlyList<string> Templates { get; }
    public IReadOnlyList<NamedColor> AccentPresets => Palette.Accents;
    public IReadOnlyList<NamedColor> PaperPresets => Palette.Papers;
    public BorderKind[] BorderKinds { get; } = Enum.GetValues<BorderKind>();
    public string[] Languages { get; } = DiscoverLanguages();

    private static string[] DiscoverLanguages()
    {
        var set = new SortedSet<string> { "en" };
        foreach (string dir in new[] { Path.Combine(AppContext.BaseDirectory, "lang"), AppPaths.LanguagesDir })
        {
            try
            {
                if (Directory.Exists(dir))
                    foreach (string f in Directory.EnumerateFiles(dir, "*.json")) set.Add(Path.GetFileNameWithoutExtension(f));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return set.ToArray();
    }

    // Enums and records need small adapters; everything else binds straight to S.

    public int DefaultZoomIndex { get => (int)S.DefaultZoom; set => S.DefaultZoom = (ZoomDefault)Math.Clamp(value, 0, 1); }
    public int PreviewQualityIndex { get => (int)S.PreviewQuality; set => S.PreviewQuality = (PreviewQuality)Math.Clamp(value, 0, 2); }
    public string DefaultTemplate { get => S.DefaultPageTemplate ?? ""; set => S.DefaultPageTemplate = string.IsNullOrEmpty(value) ? null : value; }
    public string RecoveryFolder { get => S.RecoveryFolder ?? ""; set { S.RecoveryFolder = string.IsNullOrWhiteSpace(value) ? null : value.Trim(); OnPropertyChanged(); } }
    public string RecoveryFolderHint => Loc.T("Default: {0}", AppPaths.DefaultRecoveryDir);
    public decimal? UiScalePercent { get => (decimal)Math.Round(S.UiScale * 100); set { if (value is { } v) S.UiScale = (double)v / 100; } }

    public int ExportScaleIndex { get => (int)S.ExportDefaults.ScaleMode; set => S.ExportDefaults = S.ExportDefaults with { ScaleMode = (ExportScaleMode)Math.Clamp(value, 0, 5) }; }
    public int ExportCompressionIndex { get => (int)S.ExportDefaults.Compression; set => S.ExportDefaults = S.ExportDefaults with { Compression = (PngCompression)Math.Clamp(value, 0, 2) }; }
    public decimal? ExportMaxLongEdge { get => S.ExportDefaults.MaxLongEdge; set => S.ExportDefaults = S.ExportDefaults with { MaxLongEdge = (int)Math.Clamp(value ?? 0, 0, 400000) }; }
    public bool ExportUseGpu { get => S.ExportDefaults.UseGpu; set => S.ExportDefaults = S.ExportDefaults with { UseGpu = value }; }
    public bool ExportOpenFolder { get => S.ExportDefaults.OpenFolderAfter; set => S.ExportDefaults = S.ExportDefaults with { OpenFolderAfter = value }; }
    public string ExportPattern { get => S.ExportDefaults.NamePattern; set => S.ExportDefaults = S.ExportDefaults with { NamePattern = string.IsNullOrWhiteSpace(value) ? ExportOptions.DefaultPattern : value }; }
    public string ExportFolder { get => S.ExportDefaults.Folder ?? ""; set { S.ExportDefaults = S.ExportDefaults with { Folder = string.IsNullOrWhiteSpace(value) ? null : value.Trim() }; OnPropertyChanged(); } }

    public string StyleAccent { get => S.NewProjectStyle.Accent; set { if (ColorRgba.IsValidHex(value)) { S.NewProjectStyle = S.NewProjectStyle with { Accent = value }; OnPropertyChanged(); } } }
    public string StylePaper { get => S.NewProjectStyle.Paper; set { if (ColorRgba.IsValidHex(value)) { S.NewProjectStyle = S.NewProjectStyle with { Paper = value }; OnPropertyChanged(); } } }
    public decimal? StyleLoreWidth { get => (decimal)S.NewProjectStyle.EffectiveLoreWidth; set { if (value is { } v) S.NewProjectStyle = S.NewProjectStyle with { LoreWidth = (double)Math.Clamp(v, 24, 52) }; } }
    public bool StyleSlotFrames { get => S.NewProjectStyle.SlotFrames; set => S.NewProjectStyle = S.NewProjectStyle with { SlotFrames = value }; }
    public BorderKind StylePageBorder { get => S.NewProjectStyle.PageBorder; set => S.NewProjectStyle = S.NewProjectStyle with { PageBorder = value }; }
    public BorderKind StyleRowBorder { get => S.NewProjectStyle.RowBorder; set => S.NewProjectStyle = S.NewProjectStyle with { RowBorder = value }; }
    public BorderKind StyleDetailsBorder { get => S.NewProjectStyle.DetailsBorder; set => S.NewProjectStyle = S.NewProjectStyle with { DetailsBorder = value }; }
    public BorderKind StyleLoreBorder { get => S.NewProjectStyle.LoreBorder; set => S.NewProjectStyle = S.NewProjectStyle with { LoreBorder = value }; }

    [RelayCommand] private void PickStyleAccent(string? hex) { if (hex != null) StyleAccent = hex; }
    [RelayCommand] private void PickStylePaper(string? hex) { if (hex != null) StylePaper = hex; }

    [RelayCommand]
    private async Task BrowseRecoveryFolderAsync()
    {
        string? picked = await _dialogs.PickFolderAsync(Loc.T("Recovery folder"), Directory.Exists(RecoveryFolder) ? RecoveryFolder : null);
        if (picked != null) RecoveryFolder = picked;
    }

    [RelayCommand]
    private async Task BrowseExportFolderAsync()
    {
        string? picked = await _dialogs.PickFolderAsync(Loc.T("Default export folder"), Directory.Exists(ExportFolder) ? ExportFolder : null);
        if (picked != null) ExportFolder = picked;
    }

    /// <summary>Reset to defaults keeps what is state rather than preference (window layout, last project).</summary>
    [RelayCommand]
    private void Reset()
    {
        var fresh = new AppSettings { Window = S.Window, LastProjectPath = S.LastProjectPath, LastZoom = S.LastZoom };
        S = fresh;
        OnPropertyChanged(string.Empty);
    }

    public AppSettings Result()
    {
        S.Sanitize();
        return S;
    }
}
