using SkiaSharp;
using SolumanirSheetDesignerStudio.Core.Assets;

namespace SolumanirSheetDesignerStudio.Rendering;

/// <summary>
/// On-screen artwork: reduced-size decodes cached per size bucket (handoff 6.1). Decoding happens on background
/// threads (a few workers, newest request first, so what is on screen now is decoded before what was scrolled
/// past); until the right bucket is ready the nearest available one is drawn. Bounded by a memory budget (LRU).
/// Evicted images are released by the garbage collector, so a frame that is still drawing one never crashes.
/// </summary>
public sealed class PreviewImageCache : IDisposable
{
    private static readonly int[] Buckets = { 128, 256, 512, 1024, 2048, 4096, 8192, 16384 };

    private sealed class Entry
    {
        public required SKImage Image { get; init; }
        public required long Bytes { get; init; }
        public long LastUsed;
    }

    private readonly AssetLibrary _assets;
    private readonly object _lock = new();
    private readonly Dictionary<(string Id, int Bucket), Entry> _entries = new();
    private const int MaxPending = 256;
    private static readonly long RetryAfterMs = 5000;

    private readonly HashSet<(string, int)> _inFlight = new();
    private readonly List<(ImageAsset Asset, int Bucket)> _pending = new(); // used as a stack: the newest request is served first
    private readonly HashSet<string> _undecodable = new();                  // not an image Skia can read: never retried
    private readonly Dictionary<string, long> _unreadable = new();          // I/O or memory trouble: retried after a pause
    private readonly HashSet<string> _reported = new();
    private readonly int _maxWorkers;
    private int _workers;
    private long _bytes, _clock, _evictedSinceCollect;
    private volatile bool _disposed;

    public PreviewImageCache(AssetLibrary assets, long budgetBytes, int decodeThreads = 2)
    {
        _assets = assets;
        BudgetBytes = budgetBytes;
        _maxWorkers = Math.Max(1, decodeThreads);
    }

    public long BudgetBytes { get; set; }
    public SKFilterQuality Quality { get; set; } = SKFilterQuality.Medium;

    /// <summary>Raised on a worker thread when a better image became available; the view should repaint.</summary>
    public event Action? Updated;

    /// <summary>Raised on a worker thread when the last pending background decode has finished.</summary>
    public event Action? Idle;

    /// <summary>Raised (on the decoding thread) once per asset that cannot be decoded.</summary>
    public event Action<ImageAsset, Exception>? DecodeFailed;

    private static int BucketFor(int needed, int longestSide)
    {
        foreach (int b in Buckets)
            if (b >= needed) return Math.Min(b, longestSide);
        return longestSide;
    }

    /// <summary>
    /// Best image available right now for drawing at about <paramref name="neededMaxDim"/> device pixels, or null.
    /// Schedules a background decode of the matching bucket if it is not cached yet.
    /// </summary>
    public SKImage? Get(string assetId, int neededMaxDim)
    {
        var asset = _assets.Get(assetId);
        if (asset == null || _disposed) return null;
        int longest = Math.Max(1, Math.Max(asset.PixelWidth, asset.PixelHeight));
        int bucket = BucketFor(Math.Max(1, neededMaxDim), longest);

        lock (_lock)
        {
            long now = ++_clock;
            if (_entries.TryGetValue((assetId, bucket), out var exact))
            {
                exact.LastUsed = now;
                return exact.Image;
            }

            if (!HasFailed(assetId) && _inFlight.Add((assetId, bucket))) Enqueue(asset, bucket);

            // Meanwhile: the smallest larger bucket, otherwise the largest smaller one.
            Entry? best = null;
            int bestBucket = 0;
            foreach (var (key, entry) in _entries)
            {
                if (key.Id != assetId) continue;
                bool better = best == null ||
                              (key.Bucket > bucket ? bestBucket < bucket || key.Bucket < bestBucket : bestBucket < bucket && key.Bucket > bestBucket);
                if (better) { best = entry; bestBucket = key.Bucket; }
            }
            if (best != null) best.LastUsed = now;
            return best?.Image;
        }
    }

    /// <summary>Decodes on the calling thread if necessary (thumbnails, command-line rendering).</summary>
    public SKImage? GetBlocking(string assetId, int neededMaxDim)
    {
        var asset = _assets.Get(assetId);
        if (asset == null || _disposed) return null;
        int longest = Math.Max(1, Math.Max(asset.PixelWidth, asset.PixelHeight));
        int bucket = BucketFor(Math.Max(1, neededMaxDim), longest);
        lock (_lock)
        {
            if (_entries.TryGetValue((assetId, bucket), out var exact))
            {
                exact.LastUsed = ++_clock;
                return exact.Image;
            }
            if (HasFailed(assetId)) return null;
            // Any larger cached size will do for a thumbnail.
            foreach (var (key, entry) in _entries)
                if (key.Id == assetId && key.Bucket > bucket) { entry.LastUsed = ++_clock; return entry.Image; }
        }
        return TryDecode(asset, bucket);
    }

    /// <summary>Call under the lock.</summary>
    private bool HasFailed(string assetId) =>
        _undecodable.Contains(assetId) || (_unreadable.TryGetValue(assetId, out long since) && Environment.TickCount64 - since < RetryAfterMs);

    /// <summary>Call under the lock.</summary>
    private void Enqueue(ImageAsset asset, int bucket)
    {
        _pending.Add((asset, bucket));
        if (_pending.Count > MaxPending)
        {
            // The oldest requests are for views the user has long left.
            int drop = _pending.Count - MaxPending;
            for (int i = 0; i < drop; i++) _inFlight.Remove((_pending[i].Asset.Id, _pending[i].Bucket));
            _pending.RemoveRange(0, drop);
        }
        if (_workers >= _maxWorkers) return;
        _workers++;
        _ = Task.Run(Work);
    }

    private void Work()
    {
        bool lastOut = false;
        try
        {
            while (true)
            {
                (ImageAsset Asset, int Bucket) job;
                lock (_lock)
                {
                    if (_disposed || _pending.Count == 0) break;
                    job = _pending[^1];
                    _pending.RemoveAt(_pending.Count - 1);
                }
                var image = TryDecode(job.Asset, job.Bucket);
                lock (_lock) _inFlight.Remove((job.Asset.Id, job.Bucket));
                if (image != null) Updated?.Invoke();
            }
        }
        finally
        {
            lock (_lock) lastOut = --_workers == 0 && _pending.Count == 0;
        }
        if (lastOut) Idle?.Invoke();
    }

    /// <summary>Decodes and caches one size. Failures are recorded and reported, never thrown.</summary>
    private SKImage? TryDecode(ImageAsset asset, int bucket)
    {
        SKImage image;
        try
        {
            image = Decode(asset, bucket);
        }
        catch (Exception ex)
        {
            // Decoding third-party files can fail in many ways; whatever it was, the slot just stays empty.
            // Only a file the decoder rejects is given up on; I/O trouble and a failed allocation can pass.
            bool transient = ex is not InvalidDataException;
            bool first;
            lock (_lock)
            {
                if (transient) _unreadable[asset.Id] = Environment.TickCount64;
                else _undecodable.Add(asset.Id);
                first = _reported.Add(asset.Id);
            }
            if (first) DecodeFailed?.Invoke(asset, ex);
            return null;
        }
        lock (_lock) _unreadable.Remove(asset.Id);
        return Store(asset, bucket, image);
    }

    private SKImage Decode(ImageAsset asset, int bucket) =>
        BandDecoder.DecodePreview(() => _assets.OpenRead(asset), asset.PixelWidth, asset.PixelHeight, bucket, Quality);

    private SKImage? Store(ImageAsset asset, int bucket, SKImage image)
    {
        long bytes = (long)image.Width * image.Height * 4;
        lock (_lock)
        {
            if (_disposed)
            {
                image.Dispose(); // nobody can have seen it yet
                return null;
            }
            if (_entries.TryGetValue((asset.Id, bucket), out var existing))
            {
                image.Dispose();
                return existing.Image;
            }
            _entries[(asset.Id, bucket)] = new Entry { Image = image, Bytes = bytes, LastUsed = ++_clock };
            _bytes += bytes;
            GC.AddMemoryPressure(bytes);
            Evict(keep: (asset.Id, bucket));
        }
        return image;
    }

    private void Evict((string, int)? keep)
    {
        while (_bytes > BudgetBytes && _entries.Count > 1)
        {
            (string, int)? oldest = null;
            long oldestUse = long.MaxValue;
            foreach (var (key, entry) in _entries)
            {
                if (keep is { } k && key == k) continue;
                if (entry.LastUsed < oldestUse) { oldestUse = entry.LastUsed; oldest = key; }
            }
            if (oldest == null) break;
            Remove(oldest.Value);
        }
        if (_evictedSinceCollect > 96L << 20)
        {
            _evictedSinceCollect = 0;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Optimized, blocking: false);
        }
    }

    private void Remove((string, int) key)
    {
        if (!_entries.Remove(key, out var entry)) return;
        _bytes -= entry.Bytes;
        _evictedSinceCollect += entry.Bytes;
        GC.RemoveMemoryPressure(entry.Bytes);
        // Not disposed here on purpose: a frame on the render thread may still be drawing it.
    }

    public void Clear()
    {
        lock (_lock)
        {
            foreach (var key in _entries.Keys.ToList()) Remove(key);
            foreach (var (asset, bucket) in _pending) _inFlight.Remove((asset.Id, bucket));
            _pending.Clear();
            _undecodable.Clear();
            _unreadable.Clear();
            _reported.Clear();
        }
    }

    public void Dispose()
    {
        _disposed = true;
        Clear();
    }
}
