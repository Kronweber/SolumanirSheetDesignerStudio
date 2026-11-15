using System.Security.Cryptography;
using System.Text;

namespace SolumanirSheetDesignerStudio.Core.IO;

/// <summary>The one place that decides how file paths are compared and how small files are replaced safely.</summary>
public static class PathUtil
{
    /// <summary>File names are treated as case-sensitive on Linux only.</summary>
    public static StringComparison Comparison => OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    public static StringComparer Comparer => OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

    public static bool Same(string a, string b) => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), Comparison);

    /// <summary>Short stable key for a path (lock names, cache file names): equal for paths that <see cref="Same"/> treats as equal.</summary>
    public static string Key(string path)
    {
        string full = Path.GetFullPath(path);
        if (!OperatingSystem.IsLinux()) full = full.ToUpperInvariant();
        return Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(full)))[..20];
    }

    /// <summary>Writes next to the target and renames, so a crash leaves either the old or the new file, never half of one.</summary>
    public static void WriteAtomic(string path, byte[] bytes, bool flushToDisk = false)
    {
        string tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            fs.Write(bytes, 0, bytes.Length);
            fs.Flush(flushToDisk);
        }
        File.Move(tmp, path, overwrite: true);
    }

    public static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best effort */ }
    }
}
