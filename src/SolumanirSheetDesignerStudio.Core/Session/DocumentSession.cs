using SolumanirSheetDesignerStudio.Core.Assets;
using SolumanirSheetDesignerStudio.Core.Export;
using SolumanirSheetDesignerStudio.Core.IO;
using SolumanirSheetDesignerStudio.Core.Model;

namespace SolumanirSheetDesignerStudio.Core.Session;

public enum ChangeKind { Edit, Undo, Redo, PageSwitch, Saved, Assets }

public sealed record SessionChange(ChangeKind Kind, Project Before, Project After);

/// <summary>
/// One open project: the current immutable <see cref="Project"/> snapshot, unlimited undo/redo (snapshots share
/// structure, so history is cheap), the asset library and the recovery/working folder.
/// All members except the asset library must be used from one thread (the UI thread).
/// </summary>
public sealed class DocumentSession : IDisposable
{
    /// <summary>A state to return to, plus the page to show after undoing (<c>PageBefore</c>) or redoing (<c>PageAfter</c>) the edit.</summary>
    private sealed record HistoryEntry(Project State, string PageBefore, string PageAfter, string Label, string? Key);

    private readonly List<HistoryEntry> _undo = new();
    private readonly List<HistoryEntry> _redo = new();
    private Project _saved;
    private bool _forceDirty;
    private string? _lastKey;
    private int _assetHolds;
    private bool _disposed;

    private DocumentSession(Project project, string? currentPageId, RecoverySession recovery)
    {
        Project = project;
        _saved = project;
        Recovery = recovery;
        CurrentPageId = project.FindPage(currentPageId)?.Id ?? project.Pages[0].Id;
    }

    public AssetLibrary Assets { get; } = new();
    public RecoverySession Recovery { get; }
    public Project Project { get; private set; }
    public string CurrentPageId { get; private set; }
    public Page CurrentPage => Project.FindPage(CurrentPageId) ?? Project.Pages[0];
    public string? FilePath { get; private set; }
    /// <summary>True while the document came from a v1 prototype file and has not been saved as v2 yet.</summary>
    public bool IsLegacySource { get; private set; }
    public double? Zoom { get; set; }
    public ExportOptions? LastExport { get; set; }
    public Dictionary<string, byte[]> LoadedThumbs { get; } = new();
    public List<string> LoadWarnings { get; } = new();
    public long Revision { get; private set; }

    public bool IsDirty => _forceDirty || !ReferenceEquals(Project, _saved);
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public string? UndoLabel => _undo.Count > 0 ? _undo[^1].Label : null;
    public string? RedoLabel => _redo.Count > 0 ? _redo[^1].Label : null;

    public event Action<SessionChange>? Changed;

    // ---------- creation ----------

    public static DocumentSession CreateNew(Project project, string recoveryRoot) =>
        new(ProjectOps.Normalize(project), null, RecoveryStore.Create(recoveryRoot));

    /// <param name="probe">When given, images whose pixel size is missing from the file (old or hand-edited projects) are measured.</param>
    public static DocumentSession Open(string path, string recoveryRoot, IImageProbe? probe = null)
    {
        path = Path.GetFullPath(path);
        var loaded = ProjectFile.Load(path);
        var session = new DocumentSession(loaded.Project, loaded.CurrentPageId, RecoveryStore.Create(recoveryRoot))
        {
            FilePath = path,
            IsLegacySource = loaded.IsLegacy,
            Zoom = loaded.Zoom,
            LastExport = loaded.LastExport,
        };
        try
        {
            session.AdoptLoaded(loaded, path);
            if (probe != null) session.MeasureUnsizedImages(probe);
            return session;
        }
        catch
        {
            session.Close(keepRecovery: false);
            throw;
        }
    }

    /// <summary>Restores an orphaned recovery snapshot. Images come from the recovery folder and, if it still exists, the original project file.</summary>
    public static DocumentSession Recover(RecoveryCandidate candidate)
    {
        var (doc, recovery) = RecoveryStore.Adopt(candidate);
        var session = new DocumentSession(ProjectJson.ToProject(doc.Project), doc.Project.CurrentPageId, recovery)
        {
            Zoom = doc.Project.Zoom,
            LastExport = doc.Project.LastExport,
            _forceDirty = true,
        };
        try
        {
            var fromFile = new Dictionary<string, ImageAsset>();
            string? path = candidate.Info.ProjectPath;
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                try
                {
                    var loaded = ProjectFile.Load(path);
                    session.FilePath = Path.GetFullPath(path);
                    session.IsLegacySource = loaded.IsLegacy;
                    foreach (var a in loaded.Assets) fromFile[a.Id] = a;
                    foreach (var (meta, entry) in loaded.CompressedImages)
                        fromFile[meta.Id] = ProjectFile.ExtractCompressedImage(path, meta, entry, recovery.ImagesDir);
                    foreach (var kv in loaded.Thumbs) session.LoadedThumbs[kv.Key] = kv.Value;
                }
                catch (Exception ex) when (ex is IOException or InvalidProjectException or UnauthorizedAccessException)
                {
                    session.LoadWarnings.Add("The original project file could not be read: " + ex.Message);
                }
            }

            foreach (var meta in doc.Images)
            {
                string? local = meta.RecoveryFile == null ? null : Path.Combine(recovery.ImagesDir, Path.GetFileName(meta.RecoveryFile));
                if (local != null && File.Exists(local))
                    session.Assets.Put(meta.ToAsset(new AssetSource(local, 0, new FileInfo(local).Length)), notify: false);
                else if (fromFile.TryGetValue(meta.Id, out var a))
                    session.Assets.Put(a, notify: false);
                else
                    session.LoadWarnings.Add($"Image \"{meta.FileName}\" could not be recovered.");
            }
            return session;
        }
        catch
        {
            session.Close(keepRecovery: true);
            throw;
        }
    }

    private void AdoptLoaded(LoadedProject loaded, string path)
    {
        foreach (var a in loaded.Assets) Assets.Put(a, notify: false);
        foreach (var (meta, entry) in loaded.CompressedImages)
        {
            try { Assets.Put(ProjectFile.ExtractCompressedImage(path, meta, entry, Recovery.ImagesDir), notify: false); }
            catch (Exception ex) when (ex is IOException or InvalidDataException) { LoadWarnings.Add($"Image \"{meta.FileName}\" could not be read: {ex.Message}"); }
        }
        foreach (var kv in loaded.Thumbs) LoadedThumbs[kv.Key] = kv.Value;
        LoadWarnings.AddRange(loaded.Warnings);
    }

    private void MeasureUnsizedImages(IImageProbe probe)
    {
        foreach (var a in Assets.All.Where(a => a.PixelWidth <= 0 || a.PixelHeight <= 0).ToList())
        {
            try
            {
                using var stream = Assets.OpenRead(a);
                if (probe.Probe(stream) is { Width: > 0, Height: > 0 } info)
                    Assets.Put(a with { PixelWidth = info.Width, PixelHeight = info.Height, MimeType = info.MimeType }, notify: false);
                else
                    LoadWarnings.Add($"Image \"{a.FileName}\" is not a readable image.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LoadWarnings.Add($"Image \"{a.FileName}\" could not be read: {ex.Message}");
            }
        }
    }

    // ---------- editing ----------

    /// <summary>
    /// Applies a pure edit. Consecutive edits with the same non-null <paramref name="coalesceKey"/> collapse into one
    /// undo step (typing in a text field). Returns false when the edit changed nothing.
    /// </summary>
    public bool Apply(string label, Func<Project, Project> edit, string? coalesceKey = null, string? selectPageId = null)
    {
        var before = Project;
        var after = edit(before);
        if (after.Pages.Count == 0) after = after with { Pages = after.Pages.Add(Factory.NewPage()) };
        bool changed = !ReferenceEquals(after, before) && !after.Equals(before);
        string pageBefore = CurrentPageId;
        string pageAfter = after.FindPage(selectPageId ?? pageBefore)?.Id ?? NearestPage(before, after, pageBefore);
        if (changed)
        {
            bool coalesce = coalesceKey != null && coalesceKey == _lastKey && _undo.Count > 0 && _undo[^1].Key == coalesceKey;
            if (!coalesce) _undo.Add(new HistoryEntry(before, pageBefore, pageAfter, label, coalesceKey));
            _redo.Clear();
            _lastKey = coalesceKey;
            Project = after;
            Revision++;
        }
        CurrentPageId = pageAfter;
        if (changed) Changed?.Invoke(new SessionChange(ChangeKind.Edit, before, after));
        else if (pageBefore != CurrentPageId) Changed?.Invoke(new SessionChange(ChangeKind.PageSwitch, before, after));
        return changed;
    }

    public bool ApplyToPage(string label, Func<Page, Page> edit, string? coalesceKey = null)
    {
        string pageId = CurrentPageId;
        return Apply(label, p => p.UpdatePage(pageId, edit), coalesceKey);
    }

    private static string NearestPage(Project before, Project after, string oldId)
    {
        if (after.FindPage(oldId) != null) return oldId;
        int i = Math.Max(0, before.PageIndex(oldId) - 1);
        return after.Pages[Math.Min(i, after.Pages.Count - 1)].Id;
    }

    /// <summary>Ends the current run of coalesced edits (call when a text field loses focus).</summary>
    public void BreakCoalescing() => _lastKey = null;

    public void SelectPage(string pageId)
    {
        if (pageId == CurrentPageId || Project.FindPage(pageId) == null) return;
        CurrentPageId = pageId;
        _lastKey = null;
        Changed?.Invoke(new SessionChange(ChangeKind.PageSwitch, Project, Project));
    }

    public bool Undo() => Step(_undo, _redo, ChangeKind.Undo);
    public bool Redo() => Step(_redo, _undo, ChangeKind.Redo);

    private bool Step(List<HistoryEntry> from, List<HistoryEntry> to, ChangeKind kind)
    {
        if (from.Count == 0) return false;
        var entry = from[^1];
        from.RemoveAt(from.Count - 1);
        to.Add(entry with { State = Project, Key = null });
        var before = Project;
        Project = entry.State;
        // Show the page the change happened on (undo) or led to (redo), wherever the user has navigated since.
        string wanted = kind == ChangeKind.Undo ? entry.PageBefore : entry.PageAfter;
        CurrentPageId = Project.FindPage(wanted)?.Id ?? NearestPage(before, Project, CurrentPageId);
        _lastKey = null;
        Revision++;
        Changed?.Invoke(new SessionChange(kind, before, Project));
        return true;
    }

    public void ClearHistory()
    {
        _undo.Clear();
        _redo.Clear();
        _lastKey = null;
    }

    // ---------- assets ----------

    public ImageAsset ImportImage(string path, IImageProbe probe) => Assets.ImportFile(path, Recovery.ImagesDir, probe);

    public ImageAsset ImportImage(byte[] data, string name, IImageProbe probe) => Assets.ImportBytes(data, name, Recovery.ImagesDir, probe);

    /// <summary>Image ids used by the current state or any undo/redo state.</summary>
    public HashSet<string> ReachableImageIds()
    {
        var set = Project.ReferencedImageIds();
        var seen = new HashSet<Page>(ReferenceEqualityComparer.Instance);
        foreach (var page in Project.Pages) seen.Add(page);
        foreach (var entry in _undo.Concat(_redo))
            foreach (var page in entry.State.Pages)
                if (seen.Add(page))
                    foreach (string id in page.ImageIds()) set.Add(id);
        return set;
    }

    /// <summary>
    /// Keeps freshly imported images alive until the edit that places them has been applied: while a hold is open,
    /// nothing is pruned from the library. Dispose the returned scope on the UI thread once the edit is in.
    /// </summary>
    public IDisposable HoldAssets()
    {
        Interlocked.Increment(ref _assetHolds);
        return new AssetHold(this);
    }

    private sealed class AssetHold(DocumentSession owner) : IDisposable
    {
        private DocumentSession? _owner = owner;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _owner, null) is { } s) Interlocked.Decrement(ref s._assetHolds);
        }
    }

    public bool HasAssetHolds => Volatile.Read(ref _assetHolds) > 0;

    /// <summary>Drops library entries nothing can reach any more. Returns how many were removed (none while imports are held).</summary>
    public int PruneUnreachableAssets()
    {
        if (HasAssetHolds) return 0;
        var reachable = ReachableImageIds();
        int removed = 0;
        foreach (var a in Assets.All.ToList())
            if (!reachable.Contains(a.Id) && Assets.Remove(a.Id, notify: false)) removed++;
        if (removed > 0)
        {
            CleanWorkingCopies();
            Assets.NotifyChanged();
        }
        return removed;
    }

    /// <summary>"Remove unused": drops every asset the current state does not use. Clears undo history, which may still refer to them.</summary>
    public int RemoveUnusedAssets()
    {
        ClearHistory();
        int removed = PruneUnreachableAssets();
        Changed?.Invoke(new SessionChange(ChangeKind.Assets, Project, Project));
        return removed;
    }

    /// <summary>Deletes working copies no asset points at any more (e.g. after they were saved into the project file).</summary>
    private void CleanWorkingCopies() => Assets.DeleteUnusedStoreFiles(Recovery.ImagesDir);

    // ---------- persistence ----------

    /// <summary>Captures everything a background save needs (call on the UI thread). Has no side effects.</summary>
    public SaveRequest CreateSaveRequest(string path, bool relocate, IReadOnlyDictionary<string, byte[]>? thumbs)
    {
        return new SaveRequest
        {
            Path = Path.GetFullPath(path),
            Project = Project,
            CurrentPageId = CurrentPageId,
            Zoom = Zoom,
            LastExport = LastExport,
            Assets = Assets,
            Thumbs = thumbs,
            Relocate = relocate,
            KeepAliveDir = Recovery.ImagesDir,
        };
    }

    /// <summary>Marks the snapshot that was written as the saved state (call on the UI thread after a successful Save / Save As).</summary>
    public void CompleteSave(SaveRequest request)
    {
        _saved = request.Project;
        _forceDirty = false;
        FilePath = request.Path;
        IsLegacySource = false;
        _lastKey = null; // typing after a save starts a new undo step, so the saved state stays reachable
        if (!IsDirty) Recovery.ClearSnapshot();
        // Images nothing uses any more were left out of the file; drop them from the library unless undo can bring them back.
        if (PruneUnreachableAssets() == 0) CleanWorkingCopies();
        Changed?.Invoke(new SessionChange(ChangeKind.Saved, Project, Project));
    }

    /// <summary>Builds the recovery snapshot (UI thread); write it with <see cref="RecoverySession.WriteSnapshot"/> on any thread.</summary>
    public RecoverySnapshot CreateRecoverySnapshot()
    {
        var used = Project.ReferencedImageIds();
        var doc = ProjectJson.BuildDocument(Project, CurrentPageId, Zoom, LastExport, Assets.All.Where(a => used.Contains(a.Id)));
        foreach (var meta in doc.Images)
        {
            var a = Assets.Get(meta.Id);
            if (a != null && Recovery.OwnsFile(a.Source.Path)) meta.RecoveryFile = Path.GetFileName(a.Source.Path);
        }
        var info = new RecoveryInfo
        {
            SessionId = Recovery.Id,
            ProjectPath = FilePath,
            Title = Project.Title,
            SavedUtc = DateTime.UtcNow,
            PageCount = Project.Pages.Count,
        };
        return new RecoverySnapshot(doc, info, Recovery.Epoch);
    }

    /// <summary>Closes the session. Recovery data is kept only when asked (i.e. on an abnormal shutdown).</summary>
    public void Close(bool keepRecovery)
    {
        if (_disposed) return;
        _disposed = true;
        Recovery.Close(delete: !keepRecovery);
    }

    public void Dispose() => Close(keepRecovery: false);
}
