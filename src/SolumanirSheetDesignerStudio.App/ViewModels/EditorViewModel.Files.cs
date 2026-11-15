using System.Collections.Immutable;
using CommunityToolkit.Mvvm.Input;
using SolumanirSheetDesignerStudio.App.Services;
using SolumanirSheetDesignerStudio.Core.Assets;
using SolumanirSheetDesignerStudio.Core.Export;
using SolumanirSheetDesignerStudio.Core.IO;
using SolumanirSheetDesignerStudio.Core.Model;
using SolumanirSheetDesignerStudio.Core.Settings;
using SolumanirSheetDesignerStudio.Rendering;

namespace SolumanirSheetDesignerStudio.App.ViewModels;

public sealed partial class EditorViewModel
{
    private CancellationTokenSource? _exportCts;
    private TaskCompletionSource? _exportDone;
    private Task<bool>? _saveTask;

    public bool IsSaving => _saveTask != null;

    // ---------- save ----------

    /// <summary>
    /// Writes the project on a background thread. <paramref name="relocate"/> = Save / Save As (the session now lives
    /// in that file); false = "Save a Copy" (the session is untouched). One save runs at a time: a save asked for
    /// while another is writing waits for it and then saves whatever is still unsaved.
    /// </summary>
    public async Task<bool> SaveToAsync(string path, bool relocate, bool autosave = false)
    {
        if (_saveTask is { } running)
        {
            if (autosave) return false;
            bool landed = await running;
            // The save that was already running may have written exactly what was asked for.
            if (landed && relocate && !Session.IsDirty && Session.FilePath != null && PathUtil.Same(Session.FilePath, path)) return true;
            if (_disposed) return false;
            return await SaveToAsync(path, relocate);
        }
        if (IsExporting)
        {
            if (!autosave) Main.Toasts.Show(Loc.T("Saving is paused while an export is running."));
            return false;
        }
        if (!autosave) EndEdit();

        var task = SaveCoreAsync(path, relocate, autosave);
        _saveTask = task;
        UpdateSaveState();
        try
        {
            return await task;
        }
        finally
        {
            if (ReferenceEquals(_saveTask, task)) _saveTask = null;
            UpdateSaveState();
            Main.UpdateTitle();
        }
    }

    private async Task<bool> SaveCoreAsync(string path, bool relocate, bool autosave)
    {
        _lastSaveAttempt = DateTime.UtcNow;
        var request = Session.CreateSaveRequest(path, relocate, Thumbnails.Snapshot());
        try
        {
            var result = await Task.Run(() => ProjectFile.Save(request));
            if (relocate)
            {
                Session.CompleteSave(request);
                _autosavedAt = autosave ? DateTime.Now : null;
                Main.OnProjectSaved(this, request.Path);
            }
            foreach (string warning in result.Warnings) Main.Toasts.Error(warning);
            if (!autosave)
                Main.Toasts.Show(relocate
                    ? Loc.T("Saved {0} ({1})", Path.GetFileName(request.Path), Format.Bytes(result.FileBytes))
                    : Loc.T("Saved a copy as {0} ({1})", Path.GetFileName(request.Path), Format.Bytes(result.FileBytes)));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or InvalidDataException)
        {
            // The open project is untouched: the previous file is still in place and nothing was relocated.
            Main.Toasts.Error(Loc.T("Could not save the project: {0}", ex.Message));
            return false;
        }
    }

    // ---------- export ----------

    public ExportOptions CurrentExportOptions => Session.LastExport ?? Settings.ExportDefaults;

    [RelayCommand]
    private async Task ExportAsync()
    {
        if (IsExporting) return;
        EndEdit();

        var dialog = new ExportDialogViewModel(this, Pages.Where(p => SelectedPageIds.Contains(p.Id)).Select(p => p.Id).ToList());
        var options = await Main.Dialogs.ShowExportAsync(dialog);
        if (options == null || _disposed || IsExporting) return;
        Session.LastExport = options;
        // A save that is still writing finishes first: the export reads the same image files it is about to replace.
        while (_saveTask is { } save) await save;
        if (_disposed || IsExporting) return;

        var project = Session.Project;
        var pages = ExportDialogViewModel.ResolvePages(project, Session.CurrentPageId, options);
        if (pages.Count == 0 || string.IsNullOrWhiteSpace(options.Folder))
        {
            Main.Toasts.Show(Loc.T("Nothing to export."));
            return;
        }

        var paths = FileNaming.ExpandAll(options.NamePattern, project, pages).Select(n => Path.Combine(options.Folder!, n)).ToList();
        int existing = paths.Count(File.Exists);
        if (existing > 0 && !await Main.Dialogs.ConfirmAsync(Loc.T("Replace files"),
                existing == 1 ? Loc.T("\"{0}\" already exists. Replace it?", Path.GetFileName(paths.First(File.Exists))) : Loc.T("{0} files already exist. Replace them?", existing),
                Loc.T("Replace")))
            return;

        _exportCts = new CancellationTokenSource();
        _exportDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        IsExporting = true;
        ExportProgress = 0;
        ExportStatus = Loc.T("Preparing export…");
        var progress = new Progress<ExportProgress>(p =>
        {
            ExportProgress = p.Overall;
            string name = string.IsNullOrWhiteSpace(p.PageName) ? Loc.T("Untitled") : p.PageName;
            ExportStatus = p.PageCount > 1
                ? Loc.T("Rendering {0} ({1}/{2}) {3}%", name, p.PageIndex + 1, p.PageCount, Math.Round(p.PageFraction * 100))
                : Loc.T("Rendering {0} {1}%", name, Math.Round(p.PageFraction * 100));
        });

        ExportResult result;
        try
        {
            var exporter = new SheetExporter(Engine, Render.Painter, Session.Assets);
            result = await exporter.ExportAsync(project, pages, paths, options, Settings.EffectiveExportThreads, Main.GpuRenderer, progress, _exportCts.Token);
        }
        catch (Exception ex)
        {
            result = new ExportResult { Error = ex };
        }
        finally
        {
            IsExporting = false;
            _exportCts?.Dispose();
            _exportCts = null;
            _exportDone.TrySetResult();
            _exportDone = null;
        }
        if (_disposed) return; // the project was closed while the export wound down

        foreach (string warning in result.Warnings) Main.Toasts.Error(warning);
        if (result.Cancelled)
        {
            Main.Toasts.Show(result.Pages.Count > 0
                ? Loc.T("Export cancelled. {0} page(s) were completed.", result.Pages.Count)
                : Loc.T("Export cancelled."));
        }
        else if (result.Error != null)
        {
            await Main.Dialogs.MessageAsync(Loc.T("Export failed"),
                Loc.T("\"{0}\" could not be exported:\n{1}", result.FailedPage ?? "", result.Error.Message));
        }
        else if (result.Pages.Count > 0)
        {
            var last = result.Pages[^1];
            string size = Format.Pixels(last.Width, last.Height);
            string capped = last.Capped ? " " + Loc.T("(capped at {0} px on the long edge)", options.MaxLongEdge) : "";
            double seconds = result.Pages.Sum(p => p.Elapsed.TotalSeconds);
            string took = seconds < 60 ? Loc.T("{0:0.0} s", seconds) : Loc.T("{0} min {1} s", (int)(seconds / 60), (int)(seconds % 60));
            Main.Toasts.Show(result.Pages.Count > 1
                ? Loc.T("Exported {0} pages in {4}, last {1}{2} · {3}", result.Pages.Count, size, capped, last.Renderer, took)
                : Loc.T("Exported {0}{1} in {4} · {2} · {3}", size, capped, Format.Bytes(last.FileBytes), last.Renderer, took));
            Trace.Log($"export: {result.Pages.Count} page(s) in {seconds:F1} s; " +
                      string.Join("; ", result.Pages.Select(p => $"{p.Width}x{p.Height} {p.Elapsed.TotalSeconds:F1}s {p.Renderer}")));
            if (options.OpenFolderAfter) Shell.Reveal(last.Path);
        }
        ExportStatus = "";
    }

    [RelayCommand]
    private void CancelExport() => _exportCts?.Cancel();

    /// <summary>Pages selected in the pages panel (kept in sync by the panel).</summary>
    public HashSet<string> SelectedPageIds { get; } = new();

    // ---------- import pages from another project ----------

    [RelayCommand]
    private async Task ImportPagesAsync()
    {
        EndEdit();
        string? path = await Main.Dialogs.OpenProjectAsync(Loc.T("Import pages from another project"));
        if (path == null) return;

        LoadedProject loaded;
        try
        {
            loaded = await Task.Run(() => ProjectFile.Load(path));
        }
        catch (Exception ex) when (ex is IOException or InvalidProjectException or UnauthorizedAccessException)
        {
            await Main.Dialogs.MessageAsync(Loc.T("Import pages"), Loc.T("That file could not be opened:\n{0}", ex.Message));
            return;
        }

        var dialog = new ImportPagesViewModel(loaded, path);
        if (!await Main.Dialogs.ShowImportPagesAsync(dialog)) return;
        var chosen = dialog.Items.Where(i => i.IsChecked).Select(i => i.Page).ToList();
        if (chosen.Count == 0) return;

        using var busy = BeginBusy();
        // Copy the images those pages use into this project (identical files are stored once).
        var needed = chosen.SelectMany(p => p.ImageIds()).Distinct().ToList();
        var sources = loaded.Assets.ToDictionary(a => a.Id);
        var compressed = loaded.CompressedImages.ToDictionary(c => c.Meta.Id);
        var map = new Dictionary<string, string>();
        var failures = new List<string>();
        await Task.Run(() =>
        {
            var library = new AssetLibrary();
            foreach (var a in loaded.Assets) library.Put(a, notify: false);
            foreach (string id in needed)
            {
                try
                {
                    if (sources.TryGetValue(id, out var asset))
                    {
                        using var stream = library.OpenRead(asset);
                        map[id] = Session.Assets.ImportStream(stream, asset.FileName, Session.Recovery.ImagesDir, Render.Probe).Id;
                    }
                    else if (compressed.TryGetValue(id, out var c))
                    {
                        var extracted = ProjectFile.ExtractCompressedImage(path, c.Meta, c.EntryName, Path.Combine(Session.Recovery.Dir, "import"));
                        map[id] = Session.Assets.ImportFile(extracted.Source.Path, Session.Recovery.ImagesDir, Render.Probe, extracted.FileName).Id;
                        PathUtil.TryDelete(extracted.Source.Path);
                    }
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
                {
                    failures.Add(id);
                }
            }
        });
        if (failures.Count > 0) Main.Toasts.Error(Loc.T("{0} image(s) could not be imported.", failures.Count));

        if (_disposed) return;
        var pages = chosen.Select(p => RemapImages(p.CloneWithNewIds(), map)).ToList();
        Session.Apply(Loc.T("import pages"), p => p with { Pages = p.Pages.AddRange(pages) }, selectPageId: pages[0].Id);
        Main.Toasts.Show(Loc.T("Imported {0} page(s) from {1}.", pages.Count, Path.GetFileName(path)));
    }

    private static Page RemapImages(Page page, Dictionary<string, string> map)
    {
        Section Fix(Section s) => s with
        {
            Slots = s.Slots.Select(slot => slot with
            {
                Parts = slot.Parts.Select(x => x.ImageId == null ? x : x with { ImageId = map.GetValueOrDefault(x.ImageId) }).ToImmutableList(),
            }).ToImmutableList(),
        };
        return page with { Rows = page.Rows.Select(Fix).ToImmutableList(), Details = Fix(page.Details) };
    }

    // ---------- page templates ----------

    public static IReadOnlyList<string> ListTemplates()
    {
        try
        {
            if (!Directory.Exists(AppPaths.TemplatesDir)) return Array.Empty<string>();
            return Directory.EnumerateFiles(AppPaths.TemplatesDir, "*.json").Select(Path.GetFileNameWithoutExtension)
                .Where(n => n != null).Select(n => n!).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    public static Page? LoadTemplate(string name)
    {
        try
        {
            string file = Path.Combine(AppPaths.TemplatesDir, FileNaming.Sanitize(name) + ".json");
            return File.Exists(file) ? ProjectJson.DeserializePage(File.ReadAllBytes(file))?.WithoutImages() : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private Page? LoadDefaultTemplate() => string.IsNullOrWhiteSpace(Settings.DefaultPageTemplate) ? null : LoadTemplate(Settings.DefaultPageTemplate!);

    public void AddPageFromTemplate(string name)
    {
        var template = LoadTemplate(name);
        if (template == null) Main.Toasts.Error(Loc.T("The template \"{0}\" could not be read.", name));
        else AddPage(template);
    }

    /// <summary>Stores the page's structure and text (no images) as a reusable template in the user data folder.</summary>
    public async Task SavePageAsTemplateAsync(string pageId)
    {
        var page = Session.Project.FindPage(pageId);
        if (page == null) return;
        string? name = await Main.Dialogs.PromptAsync(Loc.T("Save page as template"), Loc.T("Template name"), page.Name);
        if (string.IsNullOrWhiteSpace(name)) return;
        try
        {
            Directory.CreateDirectory(AppPaths.TemplatesDir);
            string file = Path.Combine(AppPaths.TemplatesDir, FileNaming.Sanitize(name) + ".json");
            File.WriteAllBytes(file, ProjectJson.SerializePage(page.WithoutImages()));
            Main.Toasts.Show(Loc.T("Template \"{0}\" saved.", FileNaming.Sanitize(name)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Main.Toasts.Error(Loc.T("The template could not be saved: {0}", ex.Message));
        }
    }
}
