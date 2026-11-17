using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace SolumanirSheetDesignerStudio.Core.Assets;

/// <summary>Where an image's original bytes live: a byte range of a file (a whole file, a ZIP entry, or a v1 blob).</summary>
public sealed record AssetSource(string Path, long Offset, long Length);

/// <summary>An original image file stored once per project and referenced by id. Bytes are never re-encoded.</summary>
public sealed record ImageAsset
{
    public required string Id { get; init; }
    public string FileName { get; init; } = "";
    public string MimeType { get; init; } = ImageFormats.UnknownMime;
    public int PixelWidth { get; init; }
    public int PixelHeight { get; init; }
    public long ByteLength { get; init; }
    public string? Sha256 { get; init; }
    public required AssetSource Source { get; init; }

    public string Extension
    {
        get
        {
            var ext = System.IO.Path.GetExtension(FileName);
            if (!string.IsNullOrEmpty(ext) && ext.Length <= 6) return ext.ToLowerInvariant();
            return ImageFormats.ExtensionFor(MimeType) ?? ".bin";
        }
    }
}

public sealed record ImageProbeResult(int Width, int Height, string MimeType, string Extension);

/// <summary>Reads pixel size and format of an encoded image (implemented with SkiaSharp in the rendering assembly).</summary>
public interface IImageProbe
{
    ImageProbeResult? Probe(Stream stream);
}

/// <summary>
/// Readers/writer gate around the files that back assets. Readers hold it while a stream is open; the save's final
/// "replace the project file" step takes it exclusively so no handle blocks the rename (matters on Windows).
/// Not thread-affine: a stream may be opened on one thread and closed on another.
/// </summary>
public sealed class AssetIoGate
{
    private readonly object _lock = new();
    private int _readers;
    private int _writersWaiting;
    private bool _writer;

    public void EnterRead()
    {
        lock (_lock)
        {
            while (_writer || _writersWaiting > 0) Monitor.Wait(_lock);
            _readers++;
        }
    }

    public void ExitRead()
    {
        lock (_lock)
        {
            _readers--;
            if (_readers == 0) Monitor.PulseAll(_lock);
        }
    }

    public bool TryEnterWrite(TimeSpan timeout)
    {
        long deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        lock (_lock)
        {
            _writersWaiting++;
            try
            {
                while (_writer || _readers > 0)
                {
                    long left = deadline - Environment.TickCount64;
                    if (left <= 0 || !Monitor.Wait(_lock, TimeSpan.FromMilliseconds(left)))
                    {
                        if (!_writer && _readers == 0) break;
                        return false;
                    }
                }
                _writer = true;
                return true;
            }
            finally
            {
                _writersWaiting--;
                if (!_writer) Monitor.PulseAll(_lock);
            }
        }
    }

    public void ExitWrite()
    {
        lock (_lock)
        {
            _writer = false;
            Monitor.PulseAll(_lock);
        }
    }
}

/// <summary>Read-only, seekable window over a file range.</summary>
public sealed class RangeStream : Stream
{
    private readonly FileStream _inner;
    private readonly long _start;
    private readonly long _length;
    private long _pos;
    private Action? _onDispose;

    public RangeStream(FileStream inner, long start, long length, Action? onDispose = null)
    {
        _inner = inner;
        _start = start;
        _length = length;
        _onDispose = onDispose;
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _length;

    public override long Position
    {
        get => _pos;
        set => _pos = Math.Clamp(value, 0, _length);
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        long remaining = _length - _pos;
        if (remaining <= 0 || buffer.Length == 0) return 0;
        if (buffer.Length > remaining) buffer = buffer[..(int)remaining];
        long want = _start + _pos;
        if (_inner.Position != want) _inner.Position = want;
        int n = _inner.Read(buffer);
        _pos += n;
        return n;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _pos + offset,
            _ => _length + offset,
        };
        return _pos;
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) _inner.Dispose();
        Interlocked.Exchange(ref _onDispose, null)?.Invoke();
        base.Dispose(disposing);
    }
}

/// <summary>
/// The project's image library. Holds only metadata and lazy handles — image bytes stay on disk and are streamed
/// on demand, so multi-GB projects never sit in memory. Thread-safe.
/// </summary>
public sealed class AssetLibrary
{
    private const string ImportingSuffix = ".importing";

    private readonly ConcurrentDictionary<string, ImageAsset> _assets = new();
    /// <summary>Makes "move the finished file into the store and register it" atomic with respect to store clean-up.</summary>
    private readonly object _storeLock = new();

    public AssetIoGate Gate { get; } = new();

    /// <summary>Raised (on the calling thread) whenever an asset is added, replaced or removed.</summary>
    public event Action? Changed;

    public int Count => _assets.Count;
    public ICollection<ImageAsset> All => _assets.Values;
    public ImageAsset? Get(string? id) => id != null && _assets.TryGetValue(id, out var a) ? a : null;
    public bool Contains(string id) => _assets.ContainsKey(id);

    public void Put(ImageAsset asset, bool notify = true)
    {
        _assets[asset.Id] = asset;
        if (notify) Changed?.Invoke();
    }

    public bool Remove(string id, bool notify = true)
    {
        bool removed = _assets.TryRemove(id, out _);
        if (removed && notify) Changed?.Invoke();
        return removed;
    }

    public void NotifyChanged() => Changed?.Invoke();

    public ImageAsset? FindByHash(string sha256, long length) =>
        _assets.Values.FirstOrDefault(a => a.ByteLength == length && a.Sha256 == sha256);

    /// <summary>Opens the original bytes. The returned stream holds the I/O gate until disposed.</summary>
    public Stream OpenRead(ImageAsset asset)
    {
        Gate.EnterRead();
        try
        {
            // Re-read the record inside the gate: a save may just have relocated the asset into the new project file.
            var src = (Get(asset.Id) ?? asset).Source;
            var fs = new FileStream(src.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
            try
            {
                if (src.Offset < 0 || src.Offset + src.Length > fs.Length)
                    throw new IOException($"Image data for \"{asset.FileName}\" is missing or truncated.");
                return new RangeStream(fs, src.Offset, src.Length, Gate.ExitRead);
            }
            catch
            {
                fs.Dispose();
                throw;
            }
        }
        catch
        {
            Gate.ExitRead();
            throw;
        }
    }

    public (int Width, int Height)? SizeOf(string id) => Get(id) is { PixelWidth: > 0, PixelHeight: > 0 } a ? (a.PixelWidth, a.PixelHeight) : null;

    public long TotalBytes(IEnumerable<string>? ids = null) =>
        ids == null ? _assets.Values.Sum(a => a.ByteLength) : ids.Sum(id => Get(id)?.ByteLength ?? 0);

    /// <summary>
    /// Copies an image file into <paramref name="storeDir"/> (the session's working folder) and registers it.
    /// Identical content already in the library is reused instead of stored twice.
    /// </summary>
    public ImageAsset ImportFile(string sourcePath, string storeDir, IImageProbe probe, string? displayName = null)
    {
        using var src = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 1 << 16);
        return ImportStream(src, displayName ?? Path.GetFileName(sourcePath), storeDir, probe);
    }

    public ImageAsset ImportBytes(byte[] data, string displayName, string storeDir, IImageProbe probe)
    {
        using var ms = new MemoryStream(data, writable: false);
        return ImportStream(ms, displayName, storeDir, probe);
    }

    public ImageAsset ImportStream(Stream src, string displayName, string storeDir, IImageProbe probe)
    {
        Directory.CreateDirectory(storeDir);
        string id = Model.Ids.New();
        string tmp = Path.Combine(storeDir, id + ImportingSuffix);
        try
        {
            var (hash, length) = CopyAndHash(src, tmp);
            if (FindByHash(hash, length) is { } known)
            {
                File.Delete(tmp);
                return known;
            }

            ImageProbeResult? info;
            using (var check = new FileStream(tmp, FileMode.Open, FileAccess.Read, FileShare.Read))
                info = probe.Probe(check);
            if (info == null || info.Width <= 0 || info.Height <= 0)
                throw new InvalidDataException($"\"{displayName}\" is not a readable image.");

            ImageAsset asset;
            lock (_storeLock)
            {
                // Checked again under the lock: the same file may have been imported twice at the same time.
                if (FindByHash(hash, length) is { } existing)
                {
                    File.Delete(tmp);
                    return existing;
                }
                string finalPath = Path.Combine(storeDir, id + info.Extension);
                File.Move(tmp, finalPath, overwrite: true);
                asset = new ImageAsset
                {
                    Id = id,
                    FileName = string.IsNullOrWhiteSpace(displayName) ? "image" + info.Extension : displayName,
                    MimeType = info.MimeType,
                    PixelWidth = info.Width,
                    PixelHeight = info.Height,
                    ByteLength = length,
                    Sha256 = hash,
                    Source = new AssetSource(finalPath, 0, length),
                };
                _assets[id] = asset;
            }
            Changed?.Invoke();
            return asset;
        }
        catch
        {
            IO.PathUtil.TryDelete(tmp);
            throw;
        }
    }

    private static (string Hash, long Length) CopyAndHash(Stream src, string targetPath)
    {
        using var dst = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1 << 16];
        int n;
        while ((n = src.Read(buffer, 0, buffer.Length)) > 0)
        {
            sha.AppendData(buffer, 0, n);
            dst.Write(buffer, 0, n);
        }
        return (Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant(), dst.Length);
    }

    /// <summary>
    /// Copies an asset's bytes to a file of its own in <paramref name="storeDir"/> and re-points the asset at it.
    /// Used before the file an asset lives in is replaced.
    /// </summary>
    public void DetachToStore(ImageAsset asset, string storeDir)
    {
        Directory.CreateDirectory(storeDir);
        string tmp = Path.Combine(storeDir, asset.Id + ImportingSuffix);
        try
        {
            ExportOriginal(asset, tmp);
            lock (_storeLock)
            {
                string finalPath = Path.Combine(storeDir, asset.Id + asset.Extension);
                File.Move(tmp, finalPath, overwrite: true);
                if (_assets.TryGetValue(asset.Id, out var current))
                    _assets[asset.Id] = current with { Source = new AssetSource(finalPath, 0, new FileInfo(finalPath).Length) };
            }
        }
        catch
        {
            IO.PathUtil.TryDelete(tmp);
            throw;
        }
    }

    /// <summary>Deletes the files in <paramref name="storeDir"/> that no asset points at. Imports in progress are left alone.</summary>
    public void DeleteUnusedStoreFiles(string storeDir)
    {
        lock (_storeLock)
        {
            if (!Directory.Exists(storeDir)) return;
            var inUse = new HashSet<string>(_assets.Values.Select(a => Path.GetFullPath(a.Source.Path)), IO.PathUtil.Comparer);
            foreach (string file in Directory.EnumerateFiles(storeDir))
                if (!file.EndsWith(ImportingSuffix, StringComparison.Ordinal) && !inUse.Contains(Path.GetFullPath(file)))
                    IO.PathUtil.TryDelete(file);
        }
    }

    /// <summary>Copies an asset's bytes to a standalone file (e.g. "export original").</summary>
    public void ExportOriginal(ImageAsset asset, string targetPath)
    {
        using var src = OpenRead(asset);
        using var dst = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        src.CopyTo(dst, 1 << 20);
    }
}
