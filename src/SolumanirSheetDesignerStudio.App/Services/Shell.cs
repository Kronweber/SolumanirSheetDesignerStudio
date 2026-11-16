using System.Diagnostics;

namespace SolumanirSheetDesignerStudio.App.Services;

public static class Shell
{
    /// <summary>Opens a folder (or the folder containing a file) in the system file manager.</summary>
    public static void Reveal(string path)
    {
        try
        {
            string dir = Directory.Exists(path) ? path : Path.GetDirectoryName(path) ?? path;
            if (OperatingSystem.IsWindows())
                Process.Start(new ProcessStartInfo("explorer.exe", File.Exists(path) ? $"/select,\"{path}\"" : $"\"{dir}\"") { UseShellExecute = false });
            else if (OperatingSystem.IsMacOS())
                Process.Start("open", File.Exists(path) ? new[] { "-R", path } : new[] { dir });
            else
                Process.Start("xdg-open", new[] { dir });
        }
        catch (Exception)
        {
            // no file manager available: nothing to do
        }
    }
}
