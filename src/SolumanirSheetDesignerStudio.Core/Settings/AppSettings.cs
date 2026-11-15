using System.Text.Json;
using System.Text.Json.Serialization;
using SolumanirSheetDesignerStudio.Core.Export;
using SolumanirSheetDesignerStudio.Core.IO;
using SolumanirSheetDesignerStudio.Core.Model;

namespace SolumanirSheetDesignerStudio.Core.Settings;

public enum ZoomDefault { Fit, LastUsed }

public enum PreviewQuality { Fast, Balanced, High }

/// <summary>Per-user locations. Set SSDS_DATA_DIR to relocate everything (portable installs, tests).</summary>
public static class AppPaths
{
    public static string DataDir
    {
        get
        {
            string? env = Environment.GetEnvironmentVariable("SSDS_DATA_DIR");
            if (!string.IsNullOrWhiteSpace(env)) return Path.GetFullPath(env);
            string root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create);
            if (string.IsNullOrEmpty(root)) root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
            return Path.Combine(root, AppInfo.DataFolderName);
        }
    }

    /// <summary>Large, disposable data (recovery sessions, thumbnails).</summary>
    public static string CacheDir
    {
        get
        {
            string? env = Environment.GetEnvironmentVariable("SSDS_DATA_DIR");
            if (!string.IsNullOrWhiteSpace(env)) return Path.Combine(Path.GetFullPath(env), "cache");
            string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create);
            if (string.IsNullOrEmpty(root)) return Path.Combine(DataDir, "cache");
            return Path.Combine(root, AppInfo.DataFolderName);
        }
    }

    public static string SettingsFile => Path.Combine(DataDir, "settings.json");
    public static string RecentsFile => Path.Combine(DataDir, "recent.json");
    public static string TemplatesDir => Path.Combine(DataDir, "templates");
    public static string LanguagesDir => Path.Combine(DataDir, "lang");
    public static string DefaultRecoveryDir => Path.Combine(CacheDir, "recovery");
    public static string RecentThumbsDir => Path.Combine(CacheDir, "recent-thumbs");
    public static string LocksDir => Path.Combine(CacheDir, "locks");
}

public sealed class WindowSettings
{
    public double Width { get; set; } = 1500;
    public double Height { get; set; } = 950;
    public int? X { get; set; }
    public int? Y { get; set; }
    public bool Maximized { get; set; }
    public double LeftPanelWidth { get; set; } = 230;
    public double RightPanelWidth { get; set; } = 320;
    public bool LeftPanelOpen { get; set; } = true;
    public bool RightPanelOpen { get; set; } = true;
    public int LeftTab { get; set; }
}

/// <summary>App-wide settings (section 8.2), persisted as JSON in the per-user data folder.</summary>
public sealed class AppSettings
{
    // General
    public bool ReopenLastProject { get; set; } = true;
    public string Language { get; set; } = "en";
    public double UiScale { get; set; } = 1.0;
    public bool ConfirmDelete { get; set; } = true;

    // Autosave & recovery
    public bool RecoveryEnabled { get; set; } = true;
    public int RecoveryIntervalSeconds { get; set; } = 30;
    public bool AutosaveToFile { get; set; }
    public int AutosaveMinutes { get; set; } = 5;
    public string? RecoveryFolder { get; set; }

    // Export defaults
    public ExportOptions ExportDefaults { get; set; } = new();

    // Editor
    public ZoomDefault DefaultZoom { get; set; } = ZoomDefault.Fit;
    public double LastZoom { get; set; } = 1.0;
    public bool ShowSlotPlaceholders { get; set; } = true;
    public double DefaultRowHeight { get; set; } = Sizing.DefaultHeight;
    public string? DefaultPageTemplate { get; set; }

    // Performance
    public int PreviewCacheMb { get; set; } = 768;
    public PreviewQuality PreviewQuality { get; set; } = PreviewQuality.Balanced;
    /// <summary>0 = one per CPU core (capped).</summary>
    public int ExportThreads { get; set; }

    // Defaults for new projects
    public SheetStyle NewProjectStyle { get; set; } = SheetStyle.Default;

    // State
    public string? LastProjectPath { get; set; }
    public WindowSettings Window { get; set; } = new();

    /// <summary>Where session folders go. A folder picked by the user gets a dedicated sub-folder, so recovery data never mixes with what is already there.</summary>
    [JsonIgnore] public string EffectiveRecoveryFolder => string.IsNullOrWhiteSpace(RecoveryFolder)
        ? AppPaths.DefaultRecoveryDir
        : Path.Combine(RecoveryFolder!, AppInfo.DataFolderName + " recovery");

    [JsonIgnore] public int EffectiveExportThreads => ExportThreads > 0 ? Math.Min(ExportThreads, 64) : Math.Clamp(Environment.ProcessorCount, 1, 16);

    public AppSettings Clone() => JsonSerializer.Deserialize<AppSettings>(JsonSerializer.SerializeToUtf8Bytes(this, ProjectJson.Options), ProjectJson.Options)!;

    /// <summary>Clamps anything a hand-edited file could put out of range.</summary>
    public void Sanitize()
    {
        UiScale = Math.Clamp(double.IsFinite(UiScale) ? UiScale : 1, 0.75, 2.5);
        RecoveryIntervalSeconds = Math.Clamp(RecoveryIntervalSeconds, 5, 3600);
        AutosaveMinutes = Math.Clamp(AutosaveMinutes, 1, 240);
        DefaultRowHeight = Math.Clamp(double.IsFinite(DefaultRowHeight) ? DefaultRowHeight : Sizing.DefaultHeight, Sizing.MinHeight, 4000);
        PreviewCacheMb = Math.Clamp(PreviewCacheMb, 64, 16384);
        ExportThreads = Math.Clamp(ExportThreads, 0, 64);
        LastZoom = Math.Clamp(double.IsFinite(LastZoom) ? LastZoom : 1, 0.1, 3);
        ExportDefaults ??= new ExportOptions();
        NewProjectStyle ??= SheetStyle.Default;
        Window ??= new WindowSettings();
        Language = string.IsNullOrWhiteSpace(Language) ? "en" : Language;
    }
}

public static class SettingsStore
{
    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllBytes(AppPaths.SettingsFile), ProjectJson.Options) ?? new AppSettings();
                s.Sanitize();
                return s;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A broken settings file must never stop the app: fall back to defaults.
        }
        return new AppSettings();
    }

    public static bool Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            PathUtil.WriteAtomic(AppPaths.SettingsFile, JsonSerializer.SerializeToUtf8Bytes(settings, ProjectJson.Indented));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

public sealed class RecentProject
{
    public string Path { get; set; } = "";
    public string Title { get; set; } = "";
    public DateTime LastOpenedUtc { get; set; }
    public bool Pinned { get; set; }
}

/// <summary>Recent-projects list for the start screen (pinned first, then most recent).</summary>
public sealed class RecentProjects
{
    private const int MaxUnpinned = 20;

    public List<RecentProject> Items { get; set; } = new();

    public static RecentProjects Load()
    {
        try
        {
            if (File.Exists(AppPaths.RecentsFile))
                return JsonSerializer.Deserialize<RecentProjects>(File.ReadAllBytes(AppPaths.RecentsFile), ProjectJson.Options) ?? new RecentProjects();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        return new RecentProjects();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            PathUtil.WriteAtomic(AppPaths.RecentsFile, JsonSerializer.SerializeToUtf8Bytes(this, ProjectJson.Indented));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    [JsonIgnore] public IEnumerable<RecentProject> Ordered => Items.OrderByDescending(i => i.Pinned).ThenByDescending(i => i.LastOpenedUtc);

    public void Touch(string path, string title)
    {
        path = System.IO.Path.GetFullPath(path);
        var item = Find(path);
        if (item == null) Items.Add(item = new RecentProject { Path = path });
        item.Title = title;
        item.LastOpenedUtc = DateTime.UtcNow;
        foreach (var old in Items.Where(i => !i.Pinned).OrderByDescending(i => i.LastOpenedUtc).Skip(MaxUnpinned).ToList()) Items.Remove(old);
        Save();
    }

    public void Remove(string path)
    {
        if (Items.RemoveAll(i => PathUtil.Same(i.Path, path)) > 0) Save();
    }

    public void SetPinned(string path, bool pinned)
    {
        if (Find(path) is { } item) { item.Pinned = pinned; Save(); }
    }

    private RecentProject? Find(string path) => Items.FirstOrDefault(i => PathUtil.Same(i.Path, path));

    /// <summary>Stable file name for a project's cached start-screen thumbnail.</summary>
    public static string ThumbPath(string projectPath) => System.IO.Path.Combine(AppPaths.RecentThumbsDir, PathUtil.Key(projectPath) + ".png");
}
