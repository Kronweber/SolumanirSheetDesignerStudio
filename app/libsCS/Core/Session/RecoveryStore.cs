using System.Text.Json;
using SolumanirSheetDesignerStudio.Core.IO;

namespace SolumanirSheetDesignerStudio.Core.Session;

public sealed record RecoveryInfo
{
    public string SessionId { get; init; } = "";
    public string? ProjectPath { get; init; }
    public string Title { get; init; } = "";
    public DateTime SavedUtc { get; init; }
    public int PageCount { get; init; }
}

public sealed record RecoveryCandidate(string Dir, RecoveryInfo Info);

/// <summary>A snapshot built on the UI thread. <c>Epoch</c> ties it to the unsaved state it was taken from.</summary>
public sealed record RecoverySnapshot(ProjectDocument Doc, RecoveryInfo Info, int Epoch);

/// <summary>
/// A per-session working folder in app data. It stores newly added images (so they survive until the first save)
/// and, while the document is dirty, a periodic JSON snapshot used to offer recovery after a crash.
/// A lock file held for the lifetime of the session tells other instances the folder is in use.
/// </summary>
public sealed class RecoverySession : IDisposable
{
    internal const string SnapshotName = "project.json";
    internal const string InfoName = "session.json";
    internal const string LockName = "session.lock";

    private readonly object _sync = new();
    private FileStream? _lock;
    private int _epoch;

    internal RecoverySession(string dir, string id, FileStream lockFile)
    {
        Dir = dir;
        Id = id;
        _lock = lockFile;
    }

    public string Id { get; }
    public string Dir { get; }
    public string ImagesDir => Path.Combine(Dir, "images");
    public bool HasSnapshot => File.Exists(Path.Combine(Dir, SnapshotName));

    public bool OwnsFile(string path) =>
        Path.GetFullPath(path).StartsWith(Path.GetFullPath(ImagesDir) + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    /// <summary>Changes whenever the snapshot is cleared (the document was saved), which outdates snapshots built before.</summary>
    public int Epoch => Volatile.Read(ref _epoch);

    /// <summary>
    /// Atomically replaces the snapshot (JSON only — image bytes are already in <see cref="ImagesDir"/> or the project file).
    /// May run on a background thread. A snapshot that was built before the last save, or that arrives after the session
    /// was closed, is dropped: a late write can neither outlive a save nor resurrect a deleted session folder.
    /// </summary>
    public bool WriteSnapshot(RecoverySnapshot snapshot)
    {
        byte[] json = ProjectJson.Serialize(snapshot.Doc);
        byte[] meta = JsonSerializer.SerializeToUtf8Bytes(snapshot.Info with { SessionId = Id }, ProjectJson.Indented);
        lock (_sync)
        {
            if (_lock == null || snapshot.Epoch != _epoch || !Directory.Exists(Dir)) return false;
            PathUtil.WriteAtomic(Path.Combine(Dir, SnapshotName), json, flushToDisk: true);
            PathUtil.WriteAtomic(Path.Combine(Dir, InfoName), meta, flushToDisk: true);
            return true;
        }
    }

    public void ClearSnapshot()
    {
        lock (_sync)
        {
            _epoch++;
            PathUtil.TryDelete(Path.Combine(Dir, SnapshotName));
            PathUtil.TryDelete(Path.Combine(Dir, InfoName));
        }
    }

    /// <summary>Ends the session. A clean close removes the whole folder; otherwise it is left for recovery.</summary>
    public void Close(bool delete)
    {
        lock (_sync)
        {
            _lock?.Dispose();
            _lock = null;
            if (!delete) return;
            try { if (Directory.Exists(Dir)) Directory.Delete(Dir, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* leftover folder is harmless */ }
        }
    }

    public void Dispose() => Close(delete: false);
}

/// <summary>
/// Creates and finds session folders under a root folder. The root can be a folder the user picked in the settings,
/// so the store never locks, lists or deletes anything there that is not provably one of its own session folders:
/// the name must have the session-id shape and the folder must already contain the lock file.
/// </summary>
public static class RecoveryStore
{
    private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(2);

    public static RecoverySession Create(string root)
    {
        Directory.CreateDirectory(root);
        string id = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture) + "-" + Model.Ids.New()[..6];
        string dir = Path.Combine(root, id);
        Directory.CreateDirectory(dir);
        return new RecoverySession(dir, id, AcquireLock(dir, create: true) ?? throw new IOException("Could not lock the recovery folder."));
    }

    /// <summary>True for names produced by <see cref="Create"/>: <c>yyyyMMdd-HHmmss-xxxxxx</c>.</summary>
    internal static bool IsSessionName(string name)
    {
        if (name.Length != 22 || name[8] != '-' || name[15] != '-') return false;
        for (int i = 0; i < name.Length; i++)
        {
            if (i is 8 or 15) continue;
            if (!(i < 15 ? char.IsAsciiDigit(name[i]) : char.IsAsciiHexDigitLower(name[i]))) return false;
        }
        return true;
    }

    private static bool IsSessionDir(string dir) =>
        IsSessionName(Path.GetFileName(dir)) && File.Exists(Path.Combine(dir, RecoverySession.LockName));

    private static FileStream? AcquireLock(string dir, bool create = false)
    {
        try
        {
            return new FileStream(Path.Combine(dir, RecoverySession.LockName), create ? FileMode.OpenOrCreate : FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null; // held by a running instance (or gone)
        }
    }

    /// <summary>
    /// Sessions left behind by a crash: not locked by a live instance and holding a snapshot.
    /// Stale session folders without a snapshot are cleaned up on the way; anything else in the root is left alone.
    /// </summary>
    public static List<RecoveryCandidate> FindOrphans(string root)
    {
        var result = new List<RecoveryCandidate>();
        if (!Directory.Exists(root)) return result;
        foreach (string dir in Directory.EnumerateDirectories(root))
        {
            if (!IsSessionDir(dir)) continue;
            using var probe = AcquireLock(dir);
            if (probe == null) continue;
            string snapshot = Path.Combine(dir, RecoverySession.SnapshotName);
            if (File.Exists(snapshot))
            {
                RecoveryInfo info;
                try
                {
                    info = JsonSerializer.Deserialize<RecoveryInfo>(File.ReadAllBytes(Path.Combine(dir, RecoverySession.InfoName)), ProjectJson.Options)
                           ?? new RecoveryInfo();
                }
                catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
                {
                    info = new RecoveryInfo { Title = "Unsaved project", SavedUtc = File.GetLastWriteTimeUtc(snapshot) };
                }
                result.Add(new RecoveryCandidate(dir, info with { SessionId = Path.GetFileName(dir) }));
            }
            else if (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(dir) > StaleAfter)
            {
                probe.Dispose();
                DeleteSessionDir(dir);
            }
        }
        return result.OrderByDescending(c => c.Info.SavedUtc).ToList();
    }

    private static void DeleteSessionDir(string dir)
    {
        try { Directory.Delete(dir, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* tried again on the next start */ }
    }

    /// <summary>Takes over an orphaned session folder and returns its snapshot.</summary>
    public static (ProjectDocument Doc, RecoverySession Session) Adopt(RecoveryCandidate candidate)
    {
        var lockFile = AcquireLock(candidate.Dir) ?? throw new IOException("This recovery data is in use by another window.");
        try
        {
            using var fs = File.OpenRead(Path.Combine(candidate.Dir, RecoverySession.SnapshotName));
            var doc = ProjectJson.Deserialize(fs);
            return (doc, new RecoverySession(candidate.Dir, Path.GetFileName(candidate.Dir), lockFile));
        }
        catch
        {
            lockFile.Dispose();
            throw;
        }
    }

    public static void Discard(RecoveryCandidate candidate)
    {
        if (!IsSessionDir(candidate.Dir)) return;
        using (var probe = AcquireLock(candidate.Dir))
            if (probe == null) return;
        DeleteSessionDir(candidate.Dir);
    }
}
