using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SkiaSharp;
using SolumanirSheetDesignerStudio.App.Services;
using SolumanirSheetDesignerStudio.Core.Layout;
using SolumanirSheetDesignerStudio.Core.Model;
using SolumanirSheetDesignerStudio.Core.Session;
using SolumanirSheetDesignerStudio.Core.Settings;
using SolumanirSheetDesignerStudio.Rendering;

namespace SolumanirSheetDesignerStudio.App.ViewModels;

/// <summary>
/// One open project in the editor: wraps the <see cref="DocumentSession"/>, keeps the current page's layout,
/// selection, zoom and save state, and exposes every edit as a command. (Editing.cs and Files.cs hold the rest.)
/// </summary>
public sealed partial class EditorViewModel : ObservableObject, IDisposable
{
    public const double MinZoom = 0.1, MaxZoom = 3.0;

    private readonly DispatcherTimer _timer;
    private long _recoveryRevision = -1;
    private DateTime _lastRecoveryWrite = DateTime.MinValue;
    private DateTime _lastSaveAttempt = DateTime.UtcNow;
    private DateTime? _autosavedAt;
    private bool _recoverySnapshotWritten, _recoveryErrorShown;
    private bool _repaintQueued, _assetsQueued, _disposed;
    private int _busyCount;
    private string _layoutPageId = "";

    public EditorViewModel(MainViewModel main, DocumentSession session)
    {
        Main = main;
        Session = session;
        Render = main.Render;
        Engine = Render.CreateEngine(session.Assets);
        Previews = new PreviewImageCache(session.Assets, (long)main.Settings.PreviewCacheMb << 20) { Quality = ToQuality(main.Settings.PreviewQuality) };
        Previews.Updated += OnPreviewsUpdated;
        Previews.Idle += () => Trace.Log("preview images of the visible page are ready");
        Previews.DecodeFailed += (asset, ex) => Main.Toasts.Error(Loc.T("Could not read image \"{0}\": {1}", asset.FileName, ex.Message));
        Thumbnails = new ThumbnailService(Render.Painter, Engine, Previews, OnThumbnailReady);

        _fitToWidth = main.Settings.DefaultZoom == ZoomDefault.Fit || session.Zoom == null;
        _zoom = Math.Clamp(session.Zoom ?? main.Settings.LastZoom, MinZoom, MaxZoom);
        _layout = BuildLayout();
        _layoutPageId = session.CurrentPageId;

        Inspector = new InspectorViewModel(this);
        Library = new LibraryViewModel(this);
        SyncPages();
        Thumbnails.Seed(session.LoadedThumbs, session.Project);
        Thumbnails.Update(session.Project);

        session.Changed += OnSessionChanged;
        session.Assets.Changed += OnAssetsChanged;
        UpdateSaveState();
        UpdateSelectionInfo();

        _timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => Tick());
        _timer.Start();
    }

    public MainViewModel Main { get; }
    public DocumentSession Session { get; }
    public RenderServices Render { get; }
    public SheetLayoutEngine Engine { get; }
    public PreviewImageCache Previews { get; }
    public ThumbnailService Thumbnails { get; }
    public InspectorViewModel Inspector { get; }
    public LibraryViewModel Library { get; }
    public AppSettings Settings => Main.Settings;
    public ObservableCollection<PageItemViewModel> Pages { get; } = new();

    public Page CurrentPage => Session.CurrentPage;
    public SheetStyle EffectiveStyle => CurrentPage.Style ?? Session.Project.Style;

    [ObservableProperty] private PageLayout _layout;
    [ObservableProperty] private Selection _selection = Selection.None;
    [ObservableProperty] private bool _isPreview;
    [ObservableProperty] private double _zoom;
    [ObservableProperty] private bool _fitToWidth;
    [ObservableProperty] private TextFieldRef? _editingField;
    [ObservableProperty] private string _saveState = "";
    [ObservableProperty] private string _selectionInfo = "";
    [ObservableProperty] private bool _isExporting;
    [ObservableProperty] private double _exportProgress;
    [ObservableProperty] private string _exportStatus = "";
    [ObservableProperty] private bool _isBusy;

    /// <summary>Height the in-place Markdown editor currently needs (sheet units).</summary>
    public double EditingLoreHeight { get; private set; } = 180;

    public string ZoomText => Math.Round(Zoom * 100) + "%";
    public string PreviewLabel => IsPreview ? Loc.T("Back to editing") : Loc.T("Preview");
    public bool IsEditing => !IsPreview;

    /// <summary>The sheet needs repainting although the layout did not change (a preview image finished decoding).</summary>
    public event Action? RepaintRequested;

    /// <summary>The current page changed (the view scrolls back to the top).</summary>
    public event Action? CurrentPageChanged;

    // ---------- layout ----------

    private PageLayout BuildLayout()
    {
        var field = EditingField;
        bool body = field is { Kind: TextFieldKind.LoreBody };
        return Engine.Layout(CurrentPage, Session.Project.Style, new LayoutOptions
        {
            EditChrome = !IsPreview,
            ShowPlaceholders = Settings.ShowSlotPlaceholders,
            EditingLoreId = body ? field!.Value.Id : null,
            EditingLoreHeight = EditingLoreHeight,
            HiddenField = body ? null : field,
        });
    }

    public void Relayout() => Layout = BuildLayout();

    public void SetEditingLoreHeight(double height)
    {
        height = Math.Max(180, Math.Ceiling(height));
        if (Math.Abs(height - EditingLoreHeight) < 0.5) return;
        EditingLoreHeight = height;
        if (EditingField is { Kind: TextFieldKind.LoreBody }) Relayout();
    }

    partial void OnIsPreviewChanged(bool value)
    {
        if (value) EndEdit();
        Relayout();
        OnPropertyChanged(nameof(PreviewLabel));
        OnPropertyChanged(nameof(IsEditing));
        Inspector.Refresh(); // the inspector is read-only while previewing
        NotifyCommands();
    }

    partial void OnZoomChanged(double value)
    {
        OnPropertyChanged(nameof(ZoomText));
        Session.Zoom = FitToWidth ? null : value;
        Settings.LastZoom = value;
    }

    partial void OnFitToWidthChanged(bool value) => Session.Zoom = value ? null : Zoom;

    partial void OnSelectionChanged(Selection value)
    {
        Session.BreakCoalescing(); // typing into another element starts a new undo step
        Inspector.Refresh();
        UpdateSelectionInfo();
        NotifyCommands();
    }

    // ---------- session events ----------

    private void OnSessionChanged(SessionChange change)
    {
        bool pageSwitched = Session.CurrentPageId != _layoutPageId;
        _layoutPageId = Session.CurrentPageId;
        if (pageSwitched || (EditingField is { } field && !FieldExists(field))) EditingField = null;

        SyncPages();
        Relayout();
        var valid = Validate(Selection);
        if (pageSwitched) valid = Selection.None;
        if (valid != Selection) Selection = valid; // refreshes the inspector
        else Inspector.Refresh();

        if (change.Kind != ChangeKind.PageSwitch && change.Kind != ChangeKind.Saved)
        {
            Thumbnails.Update(Session.Project);
            Library.Refresh();
        }
        UpdateSaveState();
        UpdateSelectionInfo();
        NotifyCommands();
        Main.UpdateTitle();
        OnPropertyChanged(nameof(CurrentPage));
        if (pageSwitched) CurrentPageChanged?.Invoke();
    }

    private void OnAssetsChanged()
    {
        // May be raised from a background import or save.
        if (_assetsQueued) return;
        _assetsQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _assetsQueued = false;
            if (_disposed) return;
            Relayout();
            Library.Refresh();
            Inspector.Refresh();
        });
    }

    private void OnPreviewsUpdated()
    {
        if (_repaintQueued) return;
        _repaintQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _repaintQueued = false;
            if (!_disposed) RepaintRequested?.Invoke();
        }, DispatcherPriority.Render);
    }

    private void OnThumbnailReady(string pageId, Avalonia.Media.Imaging.Bitmap? bitmap)
    {
        if (bitmap == null) return;
        var item = _disposed ? null : Pages.FirstOrDefault(p => p.Id == pageId);
        if (item == null)
        {
            bitmap.Dispose();
            return;
        }
        var previous = item.Thumbnail;
        item.Thumbnail = bitmap;
        previous?.Dispose(); // replaced after every burst of edits: do not leave them to the finalizer
    }

    /// <summary>True while the page list is being rebuilt from the document; list selection changes are not user input then.</summary>
    public bool IsSyncingPages { get; private set; }

    private void SyncPages()
    {
        IsSyncingPages = true;
        try { SyncPagesCore(); }
        finally { IsSyncingPages = false; }
    }

    private void SyncPagesCore()
    {
        var pages = Session.Project.Pages;
        var byId = Pages.ToDictionary(p => p.Id);
        var ids = pages.Select(p => p.Id).ToHashSet();
        for (int i = Pages.Count - 1; i >= 0; i--)
        {
            if (ids.Contains(Pages[i].Id)) continue;
            Pages[i].Thumbnail?.Dispose();
            Pages.RemoveAt(i);
        }
        for (int i = 0; i < pages.Count; i++)
        {
            var page = pages[i];
            if (!byId.TryGetValue(page.Id, out var item))
            {
                item = new PageItemViewModel(page.Id);
                Pages.Insert(i, item);
            }
            else
            {
                int at = Pages.IndexOf(item);
                if (at != i) Pages.Move(at, i);
            }
            item.Name = page.Name;
            item.Number = i + 1;
            item.IsCurrent = page.Id == Session.CurrentPageId;
        }
    }

    // ---------- selection ----------

    private bool FieldExists(TextFieldRef field)
    {
        var page = CurrentPage;
        return field.Kind switch
        {
            TextFieldKind.PageName or TextFieldKind.Subtitle => true,
            TextFieldKind.SectionName => page.FindSection(field.Id) != null,
            TextFieldKind.SlotLabel => page.FindSlot(field.Id) != null,
            _ => page.FindLore(field.Id) != null,
        };
    }

    private Selection Validate(Selection sel)
    {
        var page = CurrentPage;
        switch (sel.Kind)
        {
            case SelectionKind.Section:
                return page.FindSection(sel.SectionId) != null ? sel : Selection.None;
            case SelectionKind.Slot:
            {
                var found = page.FindSlot(sel.SlotId);
                return found == null ? Selection.None : sel with { SectionId = found.Value.Section.Id };
            }
            case SelectionKind.Part:
            {
                var found = page.FindPart(sel.PartId);
                if (found == null)
                {
                    var slot = page.FindSlot(sel.SlotId);
                    return slot == null ? Selection.None : new Selection(SelectionKind.Slot, slot.Value.Section.Id, sel.SlotId);
                }
                // A slot that is no longer split is selected as a whole.
                return found.Value.Slot.Parts.Count > 1
                    ? sel with { SectionId = found.Value.Section.Id, SlotId = found.Value.Slot.Id }
                    : new Selection(SelectionKind.Slot, found.Value.Section.Id, found.Value.Slot.Id);
            }
            case SelectionKind.Lore:
                return page.FindLore(sel.LoreId) != null ? sel : Selection.None;
            default:
                return Selection.None;
        }
    }

    public void Select(Selection selection) => Selection = Validate(selection);

    public void SelectSection(string sectionId) => Select(new Selection(SelectionKind.Section, sectionId));

    public void SelectSlot(string slotId) => Select(new Selection(SelectionKind.Slot, SlotId: slotId));

    /// <summary>Selects a part; in an unsplit slot that means the slot itself.</summary>
    public void SelectPart(string partId) => Select(new Selection(SelectionKind.Part, PartId: partId));

    public void SelectLore(string loreId) => Select(new Selection(SelectionKind.Lore, LoreId: loreId));

    /// <summary>Jumps to the first place an image is used and selects it.</summary>
    public void SelectFirstUsage(string imageId)
    {
        foreach (var page in Session.Project.Pages)
            foreach (var section in page.AllSections())
                foreach (var slot in section.Slots)
                    foreach (var part in slot.Parts)
                        if (part.ImageId == imageId)
                        {
                            Session.SelectPage(page.Id);
                            SelectPart(part.Id);
                            return;
                        }
        Main.Toasts.Show(Loc.T("This image is not used on any page."));
    }

    private void UpdateSelectionInfo()
    {
        var page = CurrentPage;
        var sel = Selection;
        string where = Loc.T("Page {0} of {1}", Session.Project.PageIndex(page.Id) + 1, Session.Project.Pages.Count);
        // Whole sentences per case, so a translation can order the words as its language needs.
        string what = sel.Kind switch
        {
            SelectionKind.Section => page.FindSection(sel.SectionId) is { } s ? (page.IsDetails(s.Id) ? Loc.T("Details row: {0}", s.Name) : Loc.T("Row: {0}", s.Name)) : "",
            SelectionKind.Slot => page.FindSlot(sel.SlotId) is { } f ? Loc.T("Slot: {0}", f.Slot.Label) : "",
            SelectionKind.Part => page.FindPart(sel.PartId) is { } f ? Loc.T("Slot: {0} · part {1} of {2}", f.Slot.Label, f.Index + 1, f.Slot.Parts.Count) : "",
            SelectionKind.Lore => page.FindLore(sel.LoreId) is { } l ? Loc.T("Lore section: {0}", l.Title) : "",
            _ => Loc.T("Sheet"),
        };
        SelectionInfo = Loc.T("{0}  ·  {1}", where, what);
    }

    // ---------- zoom / preview ----------

    public void SetZoom(double zoom, bool keepFit = false)
    {
        if (!keepFit) FitToWidth = false;
        Zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
    }

    [RelayCommand] private void ZoomIn() => SetZoom(Zoom * 1.2);
    [RelayCommand] private void ZoomOut() => SetZoom(Zoom / 1.2);
    [RelayCommand] private void ZoomFit() => FitToWidth = true;
    [RelayCommand] private void ZoomActual() => SetZoom(1);
    [RelayCommand] private void TogglePreview() => IsPreview = !IsPreview;

    // ---------- save state, autosave, recovery ----------

    public void UpdateSaveState()
    {
        if (IsSaving) SaveState = Loc.T("Saving…");
        else if (Session.IsDirty) SaveState = Session.FilePath == null ? Loc.T("Not saved yet") : Loc.T("Unsaved changes");
        else if (Session.IsLegacySource) SaveState = Loc.T("Opened from a prototype file — saving upgrades it");
        else if (_autosavedAt is { } t) SaveState = Loc.T("Autosaved {0}", t.ToString("HH:mm"));
        else SaveState = Session.FilePath == null ? Loc.T("New project") : Loc.T("Saved");
    }

    private void Tick()
    {
        if (_disposed) return;
        var now = DateTime.UtcNow;

        // Recovery autosave: JSON only, while dirty, every N seconds.
        if (!Session.IsDirty)
        {
            // Undone back to the saved state: the snapshot describes changes that no longer exist.
            if (_recoverySnapshotWritten)
            {
                _recoverySnapshotWritten = false;
                _recoveryRevision = -1;
                Session.Recovery.ClearSnapshot();
            }
        }
        else if (Settings.RecoveryEnabled && Session.Revision != _recoveryRevision &&
                 (now - _lastRecoveryWrite).TotalSeconds >= Settings.RecoveryIntervalSeconds)
        {
            WriteRecoverySnapshot();
        }

        // Autosave to the real file (incremental: unchanged images are copied as raw streams). Not while something
        // is being imported or typed: the save would capture a half-finished state.
        if (Settings.AutosaveToFile && Session.IsDirty && Session.FilePath != null && !IsSaving && !IsExporting && !IsBusy && EditingField == null &&
            (now - _lastSaveAttempt).TotalMinutes >= Settings.AutosaveMinutes)
        {
            _ = SaveToAsync(Session.FilePath, relocate: true, autosave: true);
        }
    }

    public void WriteRecoverySnapshot()
    {
        long revision = Session.Revision;
        _lastRecoveryWrite = DateTime.UtcNow; // also spaces out the retries after a failed write
        var snapshot = Session.CreateRecoverySnapshot();
        var recovery = Session.Recovery;
        _ = Task.Run(() =>
        {
            string? error = null;
            bool written = false;
            try { written = recovery.WriteSnapshot(snapshot); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { error = ex.Message; }

            Dispatcher.UIThread.Post(() =>
            {
                if (_disposed) return;
                if (written)
                {
                    // Recorded only now: a failed write is tried again at the next interval.
                    _recoveryRevision = Math.Max(_recoveryRevision, revision);
                    _recoverySnapshotWritten = true;
                    _recoveryErrorShown = false;
                }
                else if (error != null && !_recoveryErrorShown)
                {
                    _recoveryErrorShown = true; // said once, not every few seconds
                    Main.Toasts.Error(Loc.T("Recovery data could not be written: {0}", error));
                }
            });
        });
    }

    // ---------- background operations ----------

    /// <summary>
    /// Brackets an operation that imports images in the background and then places them: shows the "Working…" veil
    /// (which blocks input), keeps the imported images alive until the edit is in, and holds autosave back.
    /// </summary>
    private IDisposable BeginBusy()
    {
        _busyCount++;
        IsBusy = true;
        var hold = Session.HoldAssets();
        return new Scope(() =>
        {
            hold.Dispose();
            if (--_busyCount == 0) IsBusy = false;
        });
    }

    private sealed class Scope(Action onDispose) : IDisposable
    {
        private Action? _onDispose = onDispose;

        public void Dispose() => Interlocked.Exchange(ref _onDispose, null)?.Invoke();
    }

    /// <summary>Completes when no save and no export is running any more.</summary>
    public async Task WaitForBackgroundWorkAsync()
    {
        while (_saveTask is { } save) await save;
        if (_exportDone is { } export) await export.Task;
    }

    private static SKSamplingOptions ToQuality(PreviewQuality q) => q switch
    {
        PreviewQuality.Fast => new SKSamplingOptions(SKFilterMode.Linear),
        PreviewQuality.High => new SKSamplingOptions(SKCubicResampler.Mitchell),
        _ => new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear),
    };

    /// <summary>Applies changed app settings to the live editor.</summary>
    public void ApplySettings()
    {
        Previews.BudgetBytes = (long)Settings.PreviewCacheMb << 20;
        Previews.Quality = ToQuality(Settings.PreviewQuality);
        Relayout();
        RepaintRequested?.Invoke();
    }

    private void NotifyCommands()
    {
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
        DeleteSelectionCommand.NotifyCanExecuteChanged();
        DuplicateSelectionCommand.NotifyCanExecuteChanged();
        MoveSelectionBackCommand.NotifyCanExecuteChanged();
        MoveSelectionForwardCommand.NotifyCanExecuteChanged();
        RenameSelectionCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(UndoTip));
        OnPropertyChanged(nameof(RedoTip));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _exportCts?.Cancel();
        Session.Changed -= OnSessionChanged;
        Session.Assets.Changed -= OnAssetsChanged;
        Thumbnails.Dispose();
        Library.Dispose();
        Previews.Dispose();
        foreach (var page in Pages) page.Thumbnail?.Dispose();
    }
}
