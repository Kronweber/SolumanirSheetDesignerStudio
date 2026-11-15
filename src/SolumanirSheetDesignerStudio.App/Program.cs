using Avalonia;
using Avalonia.Media;
using SolumanirSheetDesignerStudio.Core.Export;
using SolumanirSheetDesignerStudio.Core.IO;
using SolumanirSheetDesignerStudio.Core.Model;
using SolumanirSheetDesignerStudio.Core.Session;
using SolumanirSheetDesignerStudio.Rendering;

namespace SolumanirSheetDesignerStudio.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--help") || args.Contains("-h")) return Help();
        if (args.Contains("--version"))
        {
            Console.WriteLine($"{AppInfo.Name} {typeof(Program).Assembly.GetName().Version?.ToString(3)}");
            return 0;
        }
        // A command anywhere on the line selects headless mode, and anything else that looks like an option is an
        // error. The window only ever opens for no arguments or one project file — a mistyped command line must
        // never put a window (and a "file not found" box) on someone's screen.
        int exportAt = Array.IndexOf(args, "--export"), convertAt = Array.IndexOf(args, "--convert");
        if (exportAt >= 0) return ExportHeadless(args, exportAt);
        if (convertAt >= 0) return ConvertHeadless(args, convertAt);
        if (args.FirstOrDefault(a => a.StartsWith('-') && !a.StartsWith("-psn", StringComparison.Ordinal)) is { } unknown) // -psn…: added by macOS
        {
            Console.Error.WriteLine($"Unknown option \"{unknown}\". See --help.");
            return 2;
        }
        if (args.Count(a => !a.StartsWith('-')) > 1)
        {
            Console.Error.WriteLine("Expected at most one project file. See --help.");
            return 2;
        }

        // Unsaved work survives a crash: write a recovery snapshot before going down.
        AppDomain.CurrentDomain.UnhandledException += (_, _) => App.Main?.EmergencySnapshot();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => App.Main?.EmergencySnapshot(); // e.g. SIGTERM at logout
        TaskScheduler.UnobservedTaskException += (_, e) => e.SetObserved();

        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            App.Main?.EmergencySnapshot();
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
        // UI font: bundled Alegreya Sans (falls back to the platform default when the font files are not in the build).
        // (Checked through the renderer's embedded copy: Avalonia's asset loader is not available before setup.)
        if (typeof(FontLibrary).Assembly.GetManifestResourceNames().Contains("Fonts/AlegreyaSans-Regular.ttf"))
            builder = builder.With(new FontManagerOptions { DefaultFamilyName = "avares://SolumanirSheetDesignerStudio/Assets/Fonts#Alegreya Sans" });
        return builder;
    }

    private static int Help()
    {
        Console.WriteLine($"""
            {AppInfo.Name}

            Usage:
              SolumanirSheetDesignerStudio [project.speciesproj]      open the editor (optionally with a project)
              SolumanirSheetDesignerStudio --export <project> [options]   render pages to PNG without opening a window
              SolumanirSheetDesignerStudio --convert <project> <output>   re-save a project in the current format (upgrades v1 files)

            Export options:
              --out <folder>        destination folder (default: next to the project)
              --scale <auto|N>      auto (native / row export px) or a fixed factor such as 2, 4, 8 (default: auto)
              --width <pixels>      fixed output width instead of a scale
              --page <n>            only page n (1-based); default: all pages
              --max-edge <pixels>   cap on the long edge, 0 = unlimited (default: 30000)
              --compression <fast|balanced|small>
              --transparent         no paper background
              --threads <n>         worker threads (default: one per core)
            """);
        return 0;
    }

    /// <summary>Command-line export: the same layout, painter and tiled exporter as the app, with no UI.</summary>
    private static readonly string[] ExportValueOptions = { "--export", "--out", "--scale", "--width", "--page", "--max-edge", "--compression", "--threads" };

    private static int ExportHeadless(string[] args, int at)
    {
        string? Value(string name)
        {
            int i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        if (at + 1 >= args.Length || args[at + 1].StartsWith("--"))
        {
            Console.Error.WriteLine("--export needs a project file. See --help.");
            return 2;
        }
        for (int i = 0; i < args.Length; i++)
        {
            if (ExportValueOptions.Contains(args[i])) { i++; continue; } // skips the option's value
            if (args[i] == "--transparent") continue;
            Console.Error.WriteLine($"Unknown export option \"{args[i]}\". See --help.");
            return 2;
        }
        string projectPath = Path.GetFullPath(args[at + 1]);
        var options = new ExportOptions
        {
            Scope = ExportScope.AllPages,
            Folder = Path.GetFullPath(Value("--out") ?? Path.GetDirectoryName(projectPath) ?? "."),
            MaxLongEdge = int.TryParse(Value("--max-edge"), out int edge) ? edge : ExportOptions.DefaultMaxLongEdge,
            TransparentBackground = args.Contains("--transparent"),
            Compression = Value("--compression")?.ToLowerInvariant() switch { "fast" => PngCompression.Fast, "small" => PngCompression.Small, _ => PngCompression.Balanced },
        };
        string scale = Value("--scale") ?? "auto";
        if (int.TryParse(Value("--width"), out int width) && width > 0)
            options = options with { ScaleMode = ExportScaleMode.TargetWidth, TargetWidth = width };
        else if (double.TryParse(scale, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double factor) && factor > 0)
            options = options with { ScaleMode = ExportScaleMode.CustomScale, CustomScale = factor };
        int threads = int.TryParse(Value("--threads"), out int t) && t > 0 ? t : Math.Clamp(Environment.ProcessorCount, 1, 16);

        string work = Path.Combine(Path.GetTempPath(), "ssds-export-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            using var session = DocumentSession.Open(projectPath, work);
            foreach (string warning in session.LoadWarnings) Console.Error.WriteLine("warning: " + warning);
            using var render = new RenderServices();
            foreach (string missing in render.Fonts.MissingFonts) Console.Error.WriteLine($"warning: bundled font \"{missing}\" is missing; using a system font");

            var project = session.Project;
            IReadOnlyList<Page> pages = project.Pages;
            if (int.TryParse(Value("--page"), out int number))
            {
                if (number < 1 || number > project.Pages.Count)
                {
                    Console.Error.WriteLine($"The project has {project.Pages.Count} page(s).");
                    return 2;
                }
                pages = new[] { project.Pages[number - 1] };
            }
            var paths = FileNaming.ExpandAll(options.NamePattern, project, pages).Select(n => Path.Combine(options.Folder!, n)).ToList();

            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
            int lastPercent = -1;
            var progress = new SyncProgress(p =>
            {
                int percent = (int)(p.Overall * 100);
                if (percent == lastPercent) return;
                lastPercent = percent;
                Console.Error.Write($"\r{p.PageIndex + 1}/{p.PageCount} {p.PageName}: {percent}%   ");
            });

            var exporter = new SheetExporter(render.CreateEngine(session.Assets), render.Painter, session.Assets);
            var result = exporter.ExportAsync(project, pages, paths, options, threads, null, progress, cts.Token).GetAwaiter().GetResult();
            Console.Error.WriteLine();
            foreach (string warning in result.Warnings) Console.Error.WriteLine("warning: " + warning);
            foreach (var page in result.Pages)
                Console.WriteLine($"{page.Path}\t{page.Width}x{page.Height}\t{page.FileBytes} bytes\t{page.Elapsed.TotalSeconds:F1}s\t{page.Renderer}{(page.Capped ? "\tcapped" : "")}");
            if (result.Cancelled) { Console.Error.WriteLine("Cancelled."); return 130; }
            if (result.Error != null) { Console.Error.WriteLine($"Export of \"{result.FailedPage}\" failed: {result.Error.Message}"); return 1; }
            return 0;
        }
        catch (Exception ex) when (ex is IOException or InvalidProjectException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine("Error: " + ex.Message);
            return 1;
        }
        finally
        {
            try { if (Directory.Exists(work)) Directory.Delete(work, true); } catch (Exception) { /* temp folder */ }
        }
    }

    /// <summary>Opens a project (v1 or v2) and writes it as a v2 file; prints how long loading and saving took.</summary>
    private static int ConvertHeadless(string[] args, int at)
    {
        if (args.Length != 3 || at != 0)
        {
            Console.Error.WriteLine("--convert needs a project file and an output file, and nothing else. See --help.");
            return 2;
        }
        string input = Path.GetFullPath(args[1]), output = Path.GetFullPath(args[2]);
        if (PathUtil.Same(input, output))
        {
            Console.Error.WriteLine("The output must be a different file.");
            return 2;
        }
        string work = Path.Combine(Path.GetTempPath(), "ssds-convert-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            using var session = DocumentSession.Open(input, work);
            double loadSeconds = clock.Elapsed.TotalSeconds;
            foreach (string warning in session.LoadWarnings) Console.Error.WriteLine("warning: " + warning);

            clock.Restart();
            var request = session.CreateSaveRequest(output, relocate: false, session.LoadedThumbs);
            var result = ProjectFile.Save(request);
            foreach (string warning in result.Warnings) Console.Error.WriteLine("warning: " + warning);
            Console.WriteLine($"{output}\t{session.Project.Pages.Count} pages\t{result.ImageCount} images\t{result.FileBytes} bytes\tload {loadSeconds:F2}s\tsave {clock.Elapsed.TotalSeconds:F2}s");
            return 0;
        }
        catch (Exception ex) when (ex is IOException or InvalidProjectException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine("Error: " + ex.Message);
            return 1;
        }
        finally
        {
            try { if (Directory.Exists(work)) Directory.Delete(work, true); } catch (Exception) { /* temp folder */ }
        }
    }

    /// <summary>Progress that reports on the calling thread (there is no UI loop in headless mode).</summary>
    private sealed class SyncProgress : IProgress<ExportProgress>
    {
        private readonly Action<ExportProgress> _report;
        public SyncProgress(Action<ExportProgress> report) => _report = report;
        public void Report(ExportProgress value) { lock (this) _report(value); }
    }
}
