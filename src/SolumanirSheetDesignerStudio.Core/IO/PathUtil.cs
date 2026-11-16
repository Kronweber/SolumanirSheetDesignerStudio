using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace SolumanirSheetDesignerStudio.Core.IO;

/// <summary>The one place that decides how file paths are compared and how small files are replaced safely.</summary>
public static class PathUtil
{
    /// <summary>File names are treated as case-sensitive on Linux only.</summary>
    public static StringComparison Comparison => OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    public static StringComparer Comparer => OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

    /// <summary>True when both paths name one file, however each is spelled (a link, a junction, a substituted or mapped drive).</summary>
    public static bool Same(string a, string b)
    {
        string pathFullA = Path.GetFullPath(a), pathFullB = Path.GetFullPath(b);
        return string.Equals(pathFullA, pathFullB, Comparison) || string.Equals(GetPathCanonical(pathFullA), GetPathCanonical(pathFullB), Comparison);
    }

    /// <summary>Short stable key for a path (lock names, cache file names): equal for paths that <see cref="Same"/> treats as equal.</summary>
    public static string Key(string path)
    {
        string full = GetPathCanonical(Path.GetFullPath(path));
        if (!OperatingSystem.IsLinux()) full = full.ToUpperInvariant();
        return Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(full)))[..20];
    }

    /// <summary>
    /// The path with links, junctions and substituted or mapped drives resolved, so that every spelling of one file
    /// gives the same text. A file that does not exist yet is resolved through its folder. A path that cannot be
    /// resolved is returned as it is.
    /// </summary>
    private static string GetPathCanonical(string pathFull)
    {
        if (GetPathResolved(pathFull) is { } pathResolved) return pathResolved;
        string? pathFolder = Path.GetDirectoryName(pathFull);
        return pathFolder != null && GetPathResolved(pathFolder) is { } pathFolderResolved
            ? Path.Combine(pathFolderResolved, Path.GetFileName(pathFull))
            : pathFull;
    }

    /// <summary>The operating system's final name of an existing file or folder, or null when it gives none.</summary>
    private static string? GetPathResolved(string pathFull)
    {
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                IntPtr pathNative = realpath(pathFull, IntPtr.Zero);
                if (pathNative == IntPtr.Zero) return null;
                try { return Marshal.PtrToStringUTF8(pathNative); }
                finally { free(pathNative); }
            }

            // The handle asks for no access rights. It only identifies the file, so a file kept in the cloud is not fetched.
            using var handle = CreateFileW(pathFull, 0, FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero, FileMode.Open, FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
            if (handle.IsInvalid) return null;
            var buffer = new char[1024];
            uint length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, 0);
            if (length >= buffer.Length)
            {
                buffer = new char[length];
                length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, 0);
            }
            if (length == 0 || length >= buffer.Length) return null;
            string pathFinal = new(buffer, 0, (int)length);
            // The name comes back in its extended form, \\?\C:\folder or \\?\UNC\server\share.
            if (pathFinal.StartsWith(@"\\?\UNC\", StringComparison.Ordinal)) return @"\\" + pathFinal[8..];
            return pathFinal.StartsWith(@"\\?\", StringComparison.Ordinal) ? pathFinal[4..] : pathFinal;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null; // the system has no such call, so paths are compared as written
        }
    }

    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000; // lets a folder be opened

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string lpFileName, uint dwDesiredAccess, FileShare dwShareMode, IntPtr lpSecurityAttributes,
        FileMode dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle hFile, [Out] char[] lpszFilePath, uint cchFilePath, uint dwFlags);

    [DllImport("libc")]
    private static extern IntPtr realpath([MarshalAs(UnmanagedType.LPUTF8Str)] string path, IntPtr resolved_path);

    [DllImport("libc")]
    private static extern void free(IntPtr ptr);

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
