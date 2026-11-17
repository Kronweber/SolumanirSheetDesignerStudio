using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SolumanirSheetDesignerStudio.App.Services;
using SolumanirSheetDesignerStudio.Core.Assets;
using SolumanirSheetDesignerStudio.Core.Model;
using SolumanirSheetDesignerStudio.Core.Session;
using SolumanirSheetDesignerStudio.Core.Settings;

namespace SolumanirSheetDesignerStudio.App.ViewModels;

// ---------- toasts ----------

public sealed class ToastViewModel
{
    public required string Message { get; init; }
    public bool IsError { get; init; }
}

/// <summary>Non-blocking notifications shown in a corner of the window.</summary>
public sealed class ToastService
{
    public ObservableCollection<ToastViewModel> Items { get; } = new();

    public void Show(string message, bool isError = false)
    {
        void Add()
        {
            var toast = new ToastViewModel { Message = message, IsError = isError };
            Items.Add(toast);
            while (Items.Count > 4) Items.RemoveAt(0);
            DispatcherTimer.RunOnce(() => Items.Remove(toast), TimeSpan.FromSeconds(isError ? 9 : 4.5));
        }

        if (Dispatcher.UIThread.CheckAccess()) Add();
        else Dispatcher.UIThread.Post(Add);
    }

    public void Error(string message) => Show(message, isError: true);
}

// ---------- pages panel ----------

public sealed partial class PageItemViewModel : ObservableObject
{
    public PageItemViewModel(string id) => Id = id;

    public string Id { get; }

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private int _number;
    /// <summary>The page shown on the canvas (the list can have more pages selected than that one).</summary>
    [ObservableProperty] private bool _isCurrent;
    [ObservableProperty] private Bitmap? _thumbnail;

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Loc.T("Untitled") : Name;

    partial void OnNameChanged(string value) => OnPropertyChanged(nameof(DisplayName));
}

// ---------- image library panel ----------

public sealed partial class LibraryItemViewModel : ObservableObject
{
    public LibraryItemViewModel(ImageAsset asset) => _asset = asset;

    [ObservableProperty] private ImageAsset _asset;
    [ObservableProperty] private int _usage;
    [ObservableProperty] private Bitmap? _thumbnail;

    public string Id => Asset.Id;
    public string Name => Asset.FileName;
    public string Details => $"{Format.Pixels(Asset.PixelWidth, Asset.PixelHeight)} · {Format.Bytes(Asset.ByteLength)}";
    public string UsageText => Usage == 0 ? Loc.T("unused — dropped on save") : Usage == 1 ? Loc.T("used once") : Loc.T("used {0} times", Usage);
    public bool IsUnused => Usage == 0;

    partial void OnAssetChanged(ImageAsset value)
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Details));
    }

    partial void OnUsageChanged(int value)
    {
        OnPropertyChanged(nameof(UsageText));
        OnPropertyChanged(nameof(IsUnused));
    }
}

/// <summary>Every asset of the project with thumbnail, pixel size, file size and usage count (handoff 8.1).</summary>
public sealed partial class LibraryViewModel : ObservableObject, IDisposable
{
    private readonly EditorViewModel _editor;
    private readonly HashSet<string> _thumbRequested = new();
    private readonly Queue<LibraryItemViewModel> _thumbQueue = new();
    private readonly CancellationTokenSource _cts = new();
    private bool _thumbWorkerRunning;

    public LibraryViewModel(EditorViewModel editor)
    {
        _editor = editor;
        Refresh();
    }

    public ObservableCollection<LibraryItemViewModel> Items { get; } = new();

    [ObservableProperty] private LibraryItemViewModel? _selected;
    [ObservableProperty] private string _summary = "";

    /// <summary>Set by the panel while it is visible; thumbnails are only decoded when someone can see them.</summary>
    public bool IsVisible { get; set; }

    public void Refresh()
    {
        var session = _editor.Session;
        var usage = session.Project.ImageUsage();
        var assets = session.Assets.All.OrderBy(a => a.FileName, StringComparer.OrdinalIgnoreCase).ThenBy(a => a.Id, StringComparer.Ordinal).ToList();

        var byId = Items.ToDictionary(i => i.Id);
        var wanted = new HashSet<string>(assets.Select(a => a.Id));
        for (int i = Items.Count - 1; i >= 0; i--)
        {
            if (wanted.Contains(Items[i].Id)) continue;
            Items[i].Thumbnail?.Dispose();
            _thumbRequested.Remove(Items[i].Id);
            Items.RemoveAt(i);
        }
        for (int i = 0; i < assets.Count; i++)
        {
            var a = assets[i];
            if (!byId.TryGetValue(a.Id, out var item))
            {
                item = new LibraryItemViewModel(a);
                Items.Insert(Math.Min(i, Items.Count), item);
            }
            else
            {
                if (!ReferenceEquals(item.Asset, a)) item.Asset = a;
                int at = Items.IndexOf(item);
                if (at != i && i < Items.Count) Items.Move(at, i);
            }
            item.Usage = usage.GetValueOrDefault(a.Id);
        }

        long used = session.Assets.TotalBytes(usage.Keys);
        int unused = assets.Count(a => !usage.ContainsKey(a.Id));
        Summary = unused == 0
            ? Loc.T("{0} images · {1}", assets.Count, Format.Bytes(used))
            : Loc.T("{0} images · {1} · {2} unused", assets.Count, Format.Bytes(used), unused);
        RemoveUnusedCommand.NotifyCanExecuteChanged();
        if (IsVisible) LoadThumbnails();
    }

    /// <summary>Queues a thumbnail for every item that has none yet. One worker decodes them, one after another.</summary>
    public void LoadThumbnails()
    {
        foreach (var item in Items)
            if (item.Thumbnail == null && _thumbRequested.Add(item.Id)) _thumbQueue.Enqueue(item);
        if (_thumbWorkerRunning || _thumbQueue.Count == 0) return;
        _thumbWorkerRunning = true;
        _ = LoadThumbnailsAsync();
    }

    private async Task LoadThumbnailsAsync()
    {
        var previews = _editor.Previews;
        var ct = _cts.Token;
        try
        {
            while (_thumbQueue.Count > 0 && !ct.IsCancellationRequested)
            {
                var item = _thumbQueue.Dequeue();
                string id = item.Id;
                Bitmap? bitmap = null;
                try
                {
                    bitmap = await Task.Run(() => SkiaInterop.ToBitmap(previews.GetBlocking(id, 128)), ct);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                if (ct.IsCancellationRequested || !Items.Contains(item)) bitmap?.Dispose();
                else if (bitmap != null) item.Thumbnail = bitmap;
                else _thumbRequested.Remove(id); // could not be decoded now: tried again the next time the panel refreshes
            }
        }
        finally
        {
            _thumbWorkerRunning = false;
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _thumbQueue.Clear();
        foreach (var item in Items) item.Thumbnail?.Dispose();
    }

    public void Select(string? assetId) => Selected = Items.FirstOrDefault(i => i.Id == assetId);

    [RelayCommand]
    private async Task ReplaceAsync(LibraryItemViewModel? item)
    {
        item ??= Selected;
        if (item == null) return;
        var files = await _editor.Main.Dialogs.OpenImagesAsync(multiple: false);
        if (files.Count == 0) return;
        await _editor.ReplaceAssetAsync(item.Id, files[0]);
    }

    [RelayCommand]
    private async Task ExportOriginalAsync(LibraryItemViewModel? item)
    {
        item ??= Selected;
        if (item == null) return;
        string? target = await _editor.Main.Dialogs.SaveFileAsync(Loc.T("Export original image"), item.Name, item.Asset.Extension);
        if (target == null) return;
        try
        {
            var asset = item.Asset;
            await Task.Run(() => _editor.Session.Assets.ExportOriginal(asset, target));
            _editor.Main.Toasts.Show(Loc.T("Exported {0}", Path.GetFileName(target)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _editor.Main.Toasts.Error(Loc.T("Could not export the image: {0}", ex.Message));
        }
    }

    [RelayCommand]
    private void ShowUsage(LibraryItemViewModel? item)
    {
        item ??= Selected;
        if (item != null) _editor.SelectFirstUsage(item.Id);
    }

    private bool HasUnused() => Items.Any(i => i.IsUnused);

    [RelayCommand(CanExecute = nameof(HasUnused))]
    private async Task RemoveUnusedAsync()
    {
        int count = Items.Count(i => i.IsUnused);
        if (count == 0) return;
        bool ok = await _editor.Main.Dialogs.ConfirmAsync(Loc.T("Remove unused images"),
            Loc.T("Remove {0} unused image(s) from the project? This also clears the undo history, which may still refer to them.", count), Loc.T("Remove"));
        if (!ok) return;
        if (_editor.IsSaving || _editor.IsBusy)
        {
            // Both are still reading or adding image files; removing some underneath them would break their work.
            _editor.Main.Toasts.Show(Loc.T("Images are still being saved or imported. Try again in a moment."));
            return;
        }
        int removed = _editor.Session.RemoveUnusedAssets();
        _editor.Main.Toasts.Show(Loc.T("Removed {0} unused image(s).", removed));
    }
}

// ---------- start screen ----------

public sealed partial class RecentItemViewModel : ObservableObject
{
    public RecentItemViewModel(RecentProject model)
    {
        Path = model.Path;
        Title = string.IsNullOrWhiteSpace(model.Title) ? System.IO.Path.GetFileNameWithoutExtension(model.Path) : model.Title;
        _pinned = model.Pinned;
    }

    public string Path { get; }
    public string Title { get; }

    [ObservableProperty] private string _modified = "";
    [ObservableProperty] private bool _missing;
    [ObservableProperty] private Bitmap? _thumbnail;
    [ObservableProperty] private bool _pinned;

    /// <summary>Looks at the file. A drive that does not answer takes minutes, so this runs off the UI thread.</summary>
    public (bool Missing, string Modified, Bitmap? Thumbnail) ReadDetails()
    {
        bool missing = true;
        string modified = Loc.T("File not found");
        Bitmap? thumbnail = null;
        try
        {
            var info = new FileInfo(Path);
            if (info.Exists)
            {
                missing = false;
                modified = info.LastWriteTime.ToString("g") + " · " + Format.Bytes(info.Length);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // shown as not found
        }
        try
        {
            string thumb = RecentProjects.ThumbPath(Path);
            if (File.Exists(thumb)) thumbnail = new Bitmap(thumb);
        }
        catch (Exception) { /* a damaged thumbnail is just not shown */ }
        return (missing, modified, thumbnail);
    }

    public string PinLabel => Pinned ? Loc.T("Unpin") : Loc.T("Pin");

    partial void OnPinnedChanged(bool value) => OnPropertyChanged(nameof(PinLabel));
}

public sealed class RecoveryItemViewModel
{
    public RecoveryItemViewModel(RecoveryCandidate candidate) => Candidate = candidate;

    public RecoveryCandidate Candidate { get; }
    public string Title => string.IsNullOrWhiteSpace(Candidate.Info.Title) ? Loc.T("Untitled project") : Candidate.Info.Title;
    public string Details => Loc.T("Unsaved work from {0}", Candidate.Info.SavedUtc.ToLocalTime().ToString("g")) +
                             (string.IsNullOrEmpty(Candidate.Info.ProjectPath) ? "" : " · " + Candidate.Info.ProjectPath);
}

/// <summary>Start screen: recent projects, New / Open and recovery of unsaved work (handoff 8.1).</summary>
public sealed partial class StartViewModel : ObservableObject
{
    private readonly MainViewModel _main;

    public StartViewModel(MainViewModel main)
    {
        _main = main;
        Refresh();
    }

    public ObservableCollection<RecentItemViewModel> Recents { get; } = new();
    public ObservableCollection<RecoveryItemViewModel> Recoveries { get; } = new();

    [ObservableProperty] private bool _hasRecents;
    [ObservableProperty] private bool _hasRecoveries;

    public void Refresh()
    {
        foreach (var old in Recents) old.Thumbnail?.Dispose();
        Recents.Clear();
        foreach (var r in _main.Recents.Ordered) Recents.Add(new RecentItemViewModel(r));
        HasRecents = Recents.Count > 0;
        foreach (var item in Recents)
        {
            _ = Task.Run(() =>
            {
                var details = item.ReadDetails();
                Dispatcher.UIThread.Post(() =>
                {
                    if (Recents.Contains(item)) (item.Missing, item.Modified, item.Thumbnail) = details;
                    else details.Thumbnail?.Dispose();
                });
            });
        }

        // Sessions also land in the default folder when the one picked in the settings cannot be used.
        Recoveries.Clear();
        foreach (string folder in new[] { _main.Settings.EffectiveRecoveryFolder, AppPaths.DefaultRecoveryDir }.Distinct())
        {
            try
            {
                foreach (var c in RecoveryStore.FindOrphans(folder)) Recoveries.Add(new RecoveryItemViewModel(c));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // recovery folder unavailable: nothing to offer
            }
        }
        HasRecoveries = Recoveries.Count > 0;
    }

    [RelayCommand]
    private Task OpenRecentAsync(RecentItemViewModel? item) => item == null ? Task.CompletedTask : _main.OpenPathAsync(item.Path);

    [RelayCommand]
    private async Task TogglePinAsync(RecentItemViewModel? item)
    {
        if (item == null) return;
        await Task.Run(() => _main.Recents.SetPinned(item.Path, !item.Pinned));
        Refresh();
    }

    [RelayCommand]
    private async Task RemoveRecentAsync(RecentItemViewModel? item)
    {
        if (item == null) return;
        await Task.Run(() => _main.Recents.Remove(item.Path));
        Refresh();
    }

    [RelayCommand]
    private void RevealRecent(RecentItemViewModel? item)
    {
        if (item != null) Shell.Reveal(item.Path);
    }

    [RelayCommand]
    private Task RecoverAsync(RecoveryItemViewModel? item) => item == null ? Task.CompletedTask : _main.RecoverAsync(item.Candidate);

    [RelayCommand]
    private async Task DiscardRecoveryAsync(RecoveryItemViewModel? item)
    {
        if (item == null) return;
        bool ok = await _main.Dialogs.ConfirmAsync(Loc.T("Discard unsaved work"),
            Loc.T("Permanently discard the unsaved work of \"{0}\"?", item.Title), Loc.T("Discard"));
        if (!ok) return;
        RecoveryStore.Discard(item.Candidate);
        Refresh();
    }
}
