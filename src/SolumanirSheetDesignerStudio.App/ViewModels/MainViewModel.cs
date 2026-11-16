using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SolumanirSheetDesignerStudio.App.Services;
using SolumanirSheetDesignerStudio.Core.IO;
using SolumanirSheetDesignerStudio.Core.Model;
using SolumanirSheetDesignerStudio.Core.Session;
using SolumanirSheetDesignerStudio.Core.Settings;
using SolumanirSheetDesignerStudio.Rendering;

namespace SolumanirSheetDesignerStudio.App.ViewModels;

/// <summary>Application shell: project lifecycle (new / open / save / close / recover), settings, recents and the window title.</summary>
public sealed partial class MainViewModel : ObservableObject
{
    private ProjectInstanceLock? _instanceLock;

    public MainViewModel(AppSettings settings, RenderServices render)
    {
        Settings = settings;
        Render = render;
        Recents = RecentProjects.Load();
        _leftPanelOpen = settings.Window.LeftPanelOpen;
        _rightPanelOpen = settings.Window.RightPanelOpen;
        _leftTab = settings.Window.LeftTab;
        Start = new StartViewModel(this);
        if (render.Fonts.MissingFonts.Count > 0)
            Toasts.Error(Loc.T("Bundled fonts are missing ({0}); the sheet uses system fonts instead. See assets/fonts/README.md.", string.Join(", ", render.Fonts.MissingFonts)));
    }

    public AppSettings Settings { get; private set; }
    public RenderServices Render { get; }
    public RecentProjects Recents { get; }
    public ToastService Toasts { get; } = new();
    public StartViewModel Start { get; }

    /// <summary>Set by the main window once it exists.</summary>
    public IDialogService Dialogs { get; set; } = null!;

    /// <summary>GPU strip renderer hosted by the main window (null when unavailable).</summary>
    public IGpuStripRenderer? GpuRenderer { get; set; }

    [ObservableProperty] private EditorViewModel? _editor;
    [ObservableProperty] private string _title = AppInfo.Name;
    [ObservableProperty] private bool _leftPanelOpen;
    [ObservableProperty] private bool _rightPanelOpen;
    [ObservableProperty] private int _leftTab;

    public bool HasProject => Editor != null;
    public bool ShowStart => Editor == null;

    /// <summary>Another instance asked this one to come to the front.</summary>
    public event Action? ActivateRequested;

    /// <summary>The Exit command was chosen.</summary>
    public event Action? ExitRequested;

    /// <summary>Settings changed (UI scale, language…).</summary>
    public event Action? SettingsApplied;

    partial void OnEditorChanged(EditorViewModel? value)
    {
        OnPropertyChanged(nameof(HasProject));
        OnPropertyChanged(nameof(ShowStart));
        UpdateTitle();
        SaveCommand.NotifyCanExecuteChanged();
        SaveAsCommand.NotifyCanExecuteChanged();
        SaveCopyCommand.NotifyCanExecuteChanged();
        RevertCommand.NotifyCanExecuteChanged();
        CloseProjectCommand.NotifyCanExecuteChanged();
    }

    partial void OnLeftPanelOpenChanged(bool value) => Settings.Window.LeftPanelOpen = value;
    partial void OnRightPanelOpenChanged(bool value) => Settings.Window.RightPanelOpen = value;
    partial void OnLeftTabChanged(int value) => Settings.Window.LeftTab = value;

    public void UpdateTitle()
    {
        if (Editor is not { } e)
        {
            Title = AppInfo.Name;
            return;
        }
        string name = string.IsNullOrWhiteSpace(e.Session.Project.Title) ? Loc.T("Untitled") : e.Session.Project.Title;
        Title = $"{name}{(e.Session.IsDirty ? " •" : "")} — {AppInfo.Name}";
        RevertCommand.NotifyCanExecuteChanged();
    }

    public void ShowLibrary()
    {
        LeftPanelOpen = true;
        LeftTab = 1;
    }

    // ---------- startup ----------

    /// <summary>Opens the file given on the command line (or by the OS), or the last project when that setting is on.</summary>
    public async Task StartupAsync(string? pathArgument)
    {
        if (!string.IsNullOrWhiteSpace(pathArgument))
        {
            string path = FullPathOrSame(pathArgument);
            // Unsaved work from a crash of this very project comes before its saved version.
            var recovery = Start.Recoveries.FirstOrDefault(r => r.Candidate.Info.ProjectPath is { Length: > 0 } saved && PathUtil.Same(saved, path));
            if (recovery != null && await Dialogs.ConfirmAsync(Loc.T("Recover unsaved work"),
                    Loc.T("\"{0}\" was not closed properly. Restore its unsaved work from {1}?", Path.GetFileName(path), recovery.Candidate.Info.SavedUtc.ToLocalTime().ToString("g")),
                    Loc.T("Restore")))
            {
                await RecoverAsync(recovery.Candidate);
                return;
            }

            var result = await OpenPathAsync(path);
            if (result == OpenResult.OpenElsewhere) ExitRequested?.Invoke(); // that window was brought to the front; this one has nothing to show
            else if (Editor != null && Start.HasRecoveries) Toasts.Show(Loc.T("Unsaved work from an earlier session is waiting on the start screen (File → Close Project)."));
            return;
        }
        // Unsaved work from a crash takes precedence over silently reopening the last project.
        if (Start.HasRecoveries) return;
        if (Settings.ReopenLastProject && Settings.LastProjectPath is { Length: > 0 } last && File.Exists(last))
            await OpenPathAsync(last);
    }

    private static string FullPathOrSame(string path)
    {
        try { return Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return path; } // File.Exists reports it
    }

    // ---------- new / open / recover ----------

    public enum OpenResult { Opened, AlreadyCurrent, Cancelled, Failed, OpenElsewhere }

    private bool _lifecycleBusy;

    /// <summary>
    /// New, open, recover, revert and close all end in "replace the editor", and all of them wait for dialogs and
    /// disk in between. Only one runs at a time; a second request made meanwhile is ignored.
    /// </summary>
    private async Task<T> ExclusiveAsync<T>(T whenBusy, Func<Task<T>> operation)
    {
        if (_lifecycleBusy) return whenBusy;
        _lifecycleBusy = true;
        try { return await operation(); }
        finally { _lifecycleBusy = false; }
    }

    private static bool IsLoadFailure(Exception ex) =>
        ex is InvalidProjectException or IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException;

    [RelayCommand]
    private Task NewProjectAsync() => ExclusiveAsync(false, async () =>
    {
        if (!await ConfirmCloseAsync()) return false;
        Page? first = string.IsNullOrWhiteSpace(Settings.DefaultPageTemplate) ? null : EditorViewModel.LoadTemplate(Settings.DefaultPageTemplate!)?.CloneWithNewIds();
        DocumentSession session;
        try
        {
            session = DocumentSession.CreateNew(Factory.NewProject(Settings.NewProjectStyle, Settings.DefaultRowHeight, first), Settings.EffectiveRecoveryFolder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await Dialogs.MessageAsync(Loc.T("New project"), Loc.T("The working folder could not be created:\n{0}", ex.Message));
            return false;
        }
        CloseEditor();
        Attach(session);
        return true;
    });

    [RelayCommand]
    private async Task OpenProjectAsync()
    {
        string? path = await Dialogs.OpenProjectAsync();
        if (path != null) await OpenPathAsync(path);
    }

    public Task<OpenResult> OpenPathAsync(string path) => ExclusiveAsync(OpenResult.Cancelled, () => OpenPathCoreAsync(path));

    private async Task<OpenResult> OpenPathCoreAsync(string path)
    {
        path = FullPathOrSame(path);
        if (!File.Exists(path))
        {
            bool known = Recents.Items.Any(i => PathUtil.Same(i.Path, path));
            if (known && await Dialogs.ConfirmAsync(Loc.T("File not found"), Loc.T("\"{0}\" no longer exists. Remove it from the recent projects?", path), Loc.T("Remove")))
            {
                Recents.Remove(path);
                Start.Refresh();
            }
            else if (!known)
            {
                await Dialogs.MessageAsync(Loc.T("Open project"), Loc.T("The file \"{0}\" does not exist.", path));
            }
            return OpenResult.Failed;
        }

        if (Editor?.Session.FilePath is { } current && PathUtil.Same(current, path)) return OpenResult.AlreadyCurrent;
        if (!await ConfirmCloseAsync()) return OpenResult.Cancelled;

        var instance = ProjectInstanceLock.TryAcquire(path, RequestActivation);
        if (instance == null)
        {
            Toasts.Show(Loc.T("\"{0}\" is already open in another window.", Path.GetFileName(path)));
            return OpenResult.OpenElsewhere;
        }

        // The open project is only closed once the new one is known to load, so a damaged file leaves it untouched.
        bool attached = false;
        try
        {
            DocumentSession session;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                string recoveryRoot = Settings.EffectiveRecoveryFolder;
                var probe = Render.Probe;
                session = await Task.Run(() => DocumentSession.Open(path, recoveryRoot, probe));
            }
            catch (Exception ex) when (IsLoadFailure(ex))
            {
                await Dialogs.MessageAsync(Loc.T("Open project"), Loc.T("\"{0}\" could not be opened:\n{1}", Path.GetFileName(path), ex.Message));
                return OpenResult.Failed;
            }

            long loadMs = clock.ElapsedMilliseconds;
            CloseEditor();
            _instanceLock = instance;
            attached = true;
            Attach(session);
            Trace.Log($"opened project: {session.Project.Pages.Count} pages, {session.Assets.Count} images, {Format.Bytes(session.Assets.TotalBytes())}; load {loadMs} ms, editor ready {clock.ElapsedMilliseconds} ms");
            RememberProject(path, session.Project.Title);
            if (!File.Exists(RecentProjects.ThumbPath(path)) && session.LoadedThumbs.TryGetValue(session.Project.Pages[0].Id, out var thumb))
                WriteStartScreenThumbnail(path, thumb);
            if (session.IsLegacySource) Toasts.Show(Loc.T("Opened a prototype (v1) project. Saving converts it to the new format."));
            return OpenResult.Opened;
        }
        finally
        {
            if (!attached) instance.Dispose();
        }
    }

    public Task RecoverAsync(RecoveryCandidate candidate) => ExclusiveAsync(false, async () =>
    {
        if (!await ConfirmCloseAsync()) return false;
        CloseEditor();

        // The recovered document keeps reading images from its project file, so that file must not be open elsewhere.
        ProjectInstanceLock? instance = null;
        if (candidate.Info.ProjectPath is { Length: > 0 } projectPath && File.Exists(projectPath))
        {
            instance = ProjectInstanceLock.TryAcquire(projectPath, RequestActivation);
            if (instance == null)
            {
                await Dialogs.MessageAsync(Loc.T("Recover unsaved work"),
                    Loc.T("\"{0}\" is open in another window. Close it there, then restore the unsaved work.", Path.GetFileName(projectPath)));
                return false;
            }
        }

        try
        {
            var session = await Task.Run(() => DocumentSession.Recover(candidate));
            if (session.FilePath == null) instance?.Dispose();
            else _instanceLock = instance;
            instance = null;
            Attach(session);
            Toasts.Show(Loc.T("Unsaved work restored. Save the project to keep it."));
            return true;
        }
        catch (Exception ex) when (IsLoadFailure(ex))
        {
            await Dialogs.MessageAsync(Loc.T("Recover unsaved work"), Loc.T("The recovery data could not be read:\n{0}", ex.Message));
            Start.Refresh();
            return false;
        }
        finally
        {
            instance?.Dispose();
        }
    });

    private void RequestActivation() => ActivateRequested?.Invoke();

    private void Attach(DocumentSession session)
    {
        Editor = new EditorViewModel(this, session);
        var warnings = session.LoadWarnings;
        if (warnings.Count <= 3)
        {
            foreach (string warning in warnings) Toasts.Error(warning);
        }
        else
        {
            // Too many for toasts (they would scroll away unread): one list the user can read at their own pace.
            const int shown = 25;
            string list = string.Join("\n", warnings.Take(shown)) + (warnings.Count > shown ? "\n" + Loc.T("… and {0} more.", warnings.Count - shown) : "");
            _ = Dialogs.MessageAsync(Loc.T("Problems found while opening the project"), list);
        }
    }

    private void RememberProject(string path, string title)
    {
        Recents.Touch(path, title);
        Settings.LastProjectPath = path;
    }

    // ---------- save ----------

    private bool HasEditor() => Editor != null;

    [RelayCommand(CanExecute = nameof(HasEditor))]
    private async Task SaveAsync() => await SaveCurrentAsync();

    /// <summary>Save; asks for a location when the project has never been saved. Returns false if cancelled or failed.</summary>
    public async Task<bool> SaveCurrentAsync()
    {
        if (Editor is not { } e) return false;
        if (e.Session.FilePath is { } path) return await e.SaveToAsync(path, relocate: true);
        return await SaveAsCoreAsync(e);
    }

    [RelayCommand(CanExecute = nameof(HasEditor))]
    private async Task SaveAsAsync()
    {
        if (Editor is { } e) await SaveAsCoreAsync(e);
    }

    private async Task<bool> SaveAsCoreAsync(EditorViewModel e)
    {
        string? path = await PickSavePathAsync(e);
        if (path == null) return false;
        var previous = _instanceLock;
        ProjectInstanceLock? next = null;
        bool samePath = e.Session.FilePath != null && PathUtil.Same(e.Session.FilePath, path);
        if (!samePath)
        {
            next = ProjectInstanceLock.TryAcquire(path, RequestActivation);
            if (next == null)
            {
                Toasts.Error(Loc.T("\"{0}\" is open in another window and cannot be replaced.", Path.GetFileName(path)));
                return false;
            }
        }
        bool ok = false;
        try
        {
            ok = await e.SaveToAsync(path, relocate: true);
        }
        finally
        {
            if (!samePath)
            {
                if (ok) { previous?.Dispose(); _instanceLock = next; }
                else next?.Dispose();
            }
        }
        return ok;
    }

    [RelayCommand(CanExecute = nameof(HasEditor))]
    private async Task SaveCopyAsync()
    {
        if (Editor is not { } e) return;
        string? path = await PickSavePathAsync(e, Loc.T(" copy"));
        if (path == null) return;
        if (e.Session.FilePath != null && PathUtil.Same(e.Session.FilePath, path))
        {
            Toasts.Show(Loc.T("That is the project's own file — use Save instead."));
            return;
        }
        await e.SaveToAsync(path, relocate: false);
    }

    private async Task<string?> PickSavePathAsync(EditorViewModel e, string suffix = "")
    {
        string name = Core.Export.FileNaming.Sanitize(e.Session.Project.Title, Loc.T("Untitled")) + suffix + AppInfo.ProjectExtension;
        string? folder = e.Session.FilePath != null ? Path.GetDirectoryName(e.Session.FilePath) : null;
        string? path = await Dialogs.SaveProjectAsync(name, folder);
        if (path == null) return null;
        if (!path.EndsWith(AppInfo.ProjectExtension, StringComparison.OrdinalIgnoreCase)) path += AppInfo.ProjectExtension;
        return Path.GetFullPath(path);
    }

    /// <summary>Called after a successful Save / Save As.</summary>
    public void OnProjectSaved(EditorViewModel editor, string path)
    {
        RememberProject(path, editor.Session.Project.Title);
        if (editor.Thumbnails.PngOf(editor.Session.Project.Pages[0].Id) is { } png) WriteStartScreenThumbnail(path, png);
    }

    /// <summary>Caches the first page's thumbnail for the start screen.</summary>
    private static void WriteStartScreenThumbnail(string projectPath, byte[] png)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.RecentThumbsDir);
            File.WriteAllBytes(RecentProjects.ThumbPath(projectPath), png);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* only a thumbnail */ }
    }

    private bool CanRevert() => Editor is { Session: { FilePath: not null, IsDirty: true } };

    [RelayCommand(CanExecute = nameof(CanRevert))]
    private Task RevertAsync() => ExclusiveAsync(false, async () =>
    {
        if (Editor is not { Session.FilePath: { } path } e) return false;
        if (!await Dialogs.ConfirmAsync(Loc.T("Revert to saved"), Loc.T("Discard every change made since the project was last saved?"), Loc.T("Revert"))) return false;
        if (!await StopBackgroundWorkAsync(e)) return false;

        DocumentSession session;
        try
        {
            string recoveryRoot = Settings.EffectiveRecoveryFolder;
            var probe = Render.Probe;
            session = await Task.Run(() => DocumentSession.Open(path, recoveryRoot, probe));
        }
        catch (Exception ex) when (IsLoadFailure(ex))
        {
            await Dialogs.MessageAsync(Loc.T("Revert to saved"), Loc.T("The saved file could not be read, so nothing was discarded:\n{0}", ex.Message));
            return false;
        }
        CloseEditor(keepInstanceLock: true);
        Attach(session);
        return true;
    });

    // ---------- close ----------

    [RelayCommand(CanExecute = nameof(HasEditor))]
    private async Task CloseProjectAsync() => await TryCloseProjectAsync();

    /// <summary>Closes the open project, prompting to save unsaved changes. Returns false when the user cancels.</summary>
    public Task<bool> TryCloseProjectAsync() => ExclusiveAsync(false, async () =>
    {
        if (!await ConfirmCloseAsync()) return false;
        CloseEditor();
        return true;
    });

    /// <summary>Ends an in-place edit, cancels a running export (after asking) and waits for a running save to land.</summary>
    private async Task<bool> StopBackgroundWorkAsync(EditorViewModel e)
    {
        if (e.IsExporting)
        {
            if (!await Dialogs.ConfirmAsync(Loc.T("Export in progress"), Loc.T("An export is still running. Cancel it and close the project?"), Loc.T("Cancel export"))) return false;
            e.CancelExportCommand.Execute(null);
        }
        e.EndEdit();
        await e.WaitForBackgroundWorkAsync();
        return true;
    }

    /// <summary>
    /// Gets the open project ready to be replaced: stops background work and asks what to do with unsaved changes.
    /// Returns false when the user cancels. Does not close anything itself.
    /// </summary>
    private async Task<bool> ConfirmCloseAsync()
    {
        if (Editor is not { } e) return true;
        if (!await StopBackgroundWorkAsync(e)) return false;
        if (!e.Session.IsDirty) return true;

        string name = string.IsNullOrWhiteSpace(e.Session.Project.Title) ? Loc.T("Untitled") : e.Session.Project.Title;
        int choice = await Dialogs.AskAsync(Loc.T("Unsaved changes"), Loc.T("Save the changes to \"{0}\"?", name), Loc.T("Save"), Loc.T("Don't Save"), Loc.T("Cancel"));
        if (choice == 0) return await SaveCurrentAsync();
        return choice == 1;
    }

    private void CloseEditor(bool keepInstanceLock = false, bool keepRecovery = false)
    {
        if (Editor is not { } e) return;
        Editor = null;
        e.Dispose();
        e.Session.Close(keepRecovery);
        if (!keepInstanceLock)
        {
            _instanceLock?.Dispose();
            _instanceLock = null;
        }
        Start.Refresh();
    }

    [RelayCommand]
    private void Exit() => ExitRequested?.Invoke();

    /// <summary>
    /// Final cleanup when the window has closed. Normally the project is closed by then (the user answered the
    /// "save changes?" question); if the window was taken away without asking (logout, shutdown), unsaved work is
    /// left behind as recovery data instead of being deleted.
    /// </summary>
    public void Shutdown()
    {
        if (Editor is { Session.IsDirty: true })
        {
            EmergencySnapshot();
            CloseEditor(keepRecovery: true);
        }
        else
        {
            CloseEditor();
        }
        SettingsStore.Save(Settings);
    }

    /// <summary>Last-ditch attempt to keep unsaved work when the app is going down unexpectedly.</summary>
    public void EmergencySnapshot()
    {
        try
        {
            if (Editor is { Session.IsDirty: true } e) e.Session.Recovery.WriteSnapshot(e.Session.CreateRecoverySnapshot());
        }
        catch (Exception)
        {
            // Called from crash handlers: nothing more can be done, and nothing may escape from here.
        }
    }

    // ---------- settings / view ----------

    [RelayCommand]
    private async Task ShowSettingsAsync()
    {
        var vm = new SettingsDialogViewModel(Settings, Dialogs);
        if (!await Dialogs.ShowSettingsAsync(vm)) return;
        var updated = vm.Result();
        updated.Window = Settings.Window;
        updated.LastProjectPath = Settings.LastProjectPath;
        bool languageChanged = !string.Equals(updated.Language, Settings.Language, StringComparison.OrdinalIgnoreCase);
        Settings = updated;
        OnPropertyChanged(nameof(Settings));
        SettingsStore.Save(Settings);
        if (languageChanged) Toasts.Show(Loc.T("The language changes the next time the app starts."));
        Editor?.ApplySettings();
        SettingsApplied?.Invoke();
    }

    [RelayCommand] private void ToggleLeftPanel() => LeftPanelOpen = !LeftPanelOpen;
    [RelayCommand] private void ToggleRightPanel() => RightPanelOpen = !RightPanelOpen;

    [RelayCommand]
    private Task AboutAsync() => Dialogs.MessageAsync(Loc.T("About"),
        $"{AppInfo.Name} {typeof(MainViewModel).Assembly.GetName().Version?.ToString(3)}\n" +
        Loc.T("Author: {0}", AppInfo.Author) + "\n\n" +
        Loc.T("Compose species reference sheets and export them as very high-resolution PNGs.") + "\n\n" +
        Loc.T("Fonts: Cinzel, Alegreya, Alegreya Sans and Cascadia Mono (SIL Open Font License)."));
}
