using Avalonia.Media.Imaging;
using Avalonia.Threading;
using SolumanirSheetDesignerStudio.Core.Layout;
using SolumanirSheetDesignerStudio.Core.Model;
using SolumanirSheetDesignerStudio.Rendering;

namespace SolumanirSheetDesignerStudio.App.Services;

/// <summary>
/// Page thumbnails for the pages panel and for <c>thumbs/&lt;pageId&gt;.png</c> in the project file. A thumbnail is
/// rendered (in the background, with the same painter as everything else) only when its page or style instance
/// changed — pages are immutable, so a reference comparison is enough.
/// </summary>
public sealed class ThumbnailService : IDisposable
{
    public const int ThumbWidth = 240;

    private sealed class Entry
    {
        public Page? Page;
        public SheetStyle? Style;
        public byte[]? Png;
    }

    private readonly ScenePainter _painter;
    private readonly SheetLayoutEngine _engine;
    private readonly PreviewImageCache _previews;
    private readonly Dictionary<string, Entry> _entries = new();
    private readonly object _lock = new();
    private readonly Action<string, Bitmap?> _onReady;
    private List<(Page Page, SheetStyle Style)> _queue = new();
    private bool _running, _disposed;

    /// <param name="onReady">Called on the UI thread with (pageId, bitmap).</param>
    public ThumbnailService(ScenePainter painter, SheetLayoutEngine engine, PreviewImageCache previews, Action<string, Bitmap?> onReady)
    {
        _painter = painter;
        _engine = engine;
        _previews = previews;
        _onReady = onReady;
    }

    /// <summary>Seeds thumbnails read from the project file; they count as current for the given page instances.</summary>
    public void Seed(IReadOnlyDictionary<string, byte[]> thumbs, Project project)
    {
        foreach (var page in project.Pages)
        {
            if (!thumbs.TryGetValue(page.Id, out var png)) continue;
            var bitmap = SkiaInterop.FromPng(png);
            if (bitmap == null) continue;
            lock (_lock) _entries[page.Id] = new Entry { Page = page, Style = page.Style ?? project.Style, Png = png };
            _onReady(page.Id, bitmap);
        }
    }

    /// <summary>Queues every page whose thumbnail is missing or out of date. Call on the UI thread after any change.</summary>
    public void Update(Project project)
    {
        if (_disposed) return;
        var stale = new List<(Page, SheetStyle)>();
        lock (_lock)
        {
            var ids = project.Pages.Select(p => p.Id).ToHashSet();
            foreach (string gone in _entries.Keys.Where(k => !ids.Contains(k)).ToList()) _entries.Remove(gone);
            foreach (var page in project.Pages)
            {
                var style = page.Style ?? project.Style;
                if (_entries.TryGetValue(page.Id, out var e) && ReferenceEquals(e.Page, page) && Equals(e.Style, style)) continue;
                stale.Add((page, style));
            }
            _queue = stale;
            if (_running || stale.Count == 0) return;
            _running = true;
        }
        // A short delay lets a burst of edits (typing) settle before anything is rendered.
        _ = Task.Delay(350).ContinueWith(_ => Work(), TaskScheduler.Default);
    }

    private void Work()
    {
        while (true)
        {
            (Page Page, SheetStyle Style) job;
            lock (_lock)
            {
                if (_queue.Count == 0 || _disposed)
                {
                    _running = false;
                    Trace.Log("page thumbnails up to date");
                    return;
                }
                job = _queue[0];
                _queue.RemoveAt(0);
            }
            try
            {
                var layout = _engine.Layout(job.Page, job.Style);
                byte[] png = PageRasterizer.RenderPng(_painter, layout, ThumbWidth / layout.Width, new PreviewImagePainter(_previews, blocking: true));
                var bitmap = SkiaInterop.FromPng(png);
                lock (_lock) _entries[job.Page.Id] = new Entry { Page = job.Page, Style = job.Style, Png = png };
                string id = job.Page.Id;
                // The receiver owns the bitmap (and disposes it when the editor is already gone).
                Dispatcher.UIThread.Post(() => _onReady(id, bitmap), DispatcherPriority.Background);
            }
            catch (Exception)
            {
                // A thumbnail that cannot be rendered is simply left as it was; mark it done so it is not retried forever.
                lock (_lock) _entries[job.Page.Id] = new Entry { Page = job.Page, Style = job.Style };
            }
        }
    }

    /// <summary>Latest PNG per page, for saving into the project file.</summary>
    public Dictionary<string, byte[]> Snapshot()
    {
        lock (_lock) return _entries.Where(kv => kv.Value.Png != null).ToDictionary(kv => kv.Key, kv => kv.Value.Png!);
    }

    public byte[]? PngOf(string pageId)
    {
        lock (_lock) return _entries.TryGetValue(pageId, out var e) ? e.Png : null;
    }

    public void Dispose() => _disposed = true;
}
