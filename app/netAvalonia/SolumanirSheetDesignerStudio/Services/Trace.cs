using System.Diagnostics;

namespace SolumanirSheetDesignerStudio.App.Services;

/// <summary>Timing lines on stderr when SSDS_TRACE=1 (open, thumbnails, export) — for performance checks.</summary>
public static class Trace
{
    private static readonly bool Enabled = Environment.GetEnvironmentVariable("SSDS_TRACE") == "1";
    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    public static void Log(string message)
    {
        if (Enabled) Console.Error.WriteLine($"[trace {Clock.Elapsed.TotalSeconds,8:F2}s] {message}");
    }
}
