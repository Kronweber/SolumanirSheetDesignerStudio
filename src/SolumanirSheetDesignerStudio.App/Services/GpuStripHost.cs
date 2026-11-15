using System.Collections.Concurrent;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Threading;
using SkiaSharp;
using SolumanirSheetDesignerStudio.Rendering;

namespace SolumanirSheetDesignerStudio.App.Services;

/// <summary>
/// GPU strip rendering for the export (handoff 6.3). Avalonia only exposes its Skia GPU context while a frame is
/// being drawn, so strips are queued and rendered from this (tiny, always visible) control's draw operation on the
/// render thread, then read back. When there is no GPU context, or no frame arrives in time, the exporter falls
/// back to the CPU for that strip.
/// </summary>
public sealed class GpuStripHost : Control, IGpuStripRenderer
{
    private sealed class Job
    {
        public required SKImageInfo Info { get; init; }
        public required Action<SKCanvas> Draw { get; init; }
        public required IntPtr Pixels { get; init; }
        public required int RowBytes { get; init; }
        public readonly ManualResetEventSlim Done = new(false);
        public bool Completed, Success, Abandoned;
    }

    private readonly ConcurrentQueue<Job> _jobs = new();
    private volatile bool _unavailable;
    private readonly int _maxSize = 8192;
    private int _timeouts;

    public GpuStripHost()
    {
        Width = 1;
        Height = 1;
        IsHitTestVisible = false;
    }

    public int MaxSurfaceSize => _maxSize;

    /// <summary>False once it is known that no GPU context is available (software rendering).</summary>
    public bool IsAvailable => !_unavailable;

    public bool TryRender(SKImageInfo info, Action<SKCanvas> draw, IntPtr pixels, int rowBytes, CancellationToken ct)
    {
        if (_unavailable) return false;
        var job = new Job { Info = info, Draw = draw, Pixels = pixels, RowBytes = rowBytes };
        _jobs.Enqueue(job);
        Dispatcher.UIThread.Post(InvalidateVisual, DispatcherPriority.Render);

        bool signalled;
        try { signalled = job.Done.Wait(TimeSpan.FromSeconds(5), ct); }
        catch (OperationCanceledException) { signalled = false; }

        // The render thread holds this lock for the whole draw + read-back, so the pixel buffer can never be freed
        // underneath it: we either see the finished job or mark it abandoned before it starts.
        lock (job)
        {
            if (job.Completed)
            {
                if (job.Success) Interlocked.Exchange(ref _timeouts, 0);
                return job.Success;
            }
            job.Abandoned = true;
        }
        ct.ThrowIfCancellationRequested();
        if (!signalled && Interlocked.Increment(ref _timeouts) >= 2) _unavailable = true; // e.g. window minimised: no frames
        return false;
    }

    public override void Render(DrawingContext context)
    {
        context.Custom(new Operation(this, new Rect(0, 0, Math.Max(1, Bounds.Width), Math.Max(1, Bounds.Height))));
        if (!_jobs.IsEmpty) Dispatcher.UIThread.Post(InvalidateVisual, DispatcherPriority.Background);
    }

    private void Drain(GRContext? gr)
    {
        // SkiaSharp 2.88 does not expose the GPU's texture limit, so a conservative size is assumed (see _maxSize).
        if (gr == null) _unavailable = true;

        while (_jobs.TryDequeue(out var job))
        {
            lock (job)
            {
                if (job.Abandoned) continue;
                try
                {
                    if (gr != null)
                    {
                        using var surface = SKSurface.Create(gr, false, job.Info);
                        if (surface != null)
                        {
                            job.Draw(surface.Canvas);
                            surface.Canvas.Flush();
                            job.Success = surface.ReadPixels(job.Info, job.Pixels, job.RowBytes, 0, 0);
                        }
                    }
                }
                catch (Exception)
                {
                    job.Success = false;
                }
                job.Completed = true;
                job.Done.Set();
            }
        }
    }

    private sealed class Operation : ICustomDrawOperation
    {
        private readonly GpuStripHost _host;

        public Operation(GpuStripHost host, Rect bounds)
        {
            _host = host;
            Bounds = bounds;
        }

        public Rect Bounds { get; }
        public void Dispose() { }
        public bool Equals(ICustomDrawOperation? other) => false;
        public bool HitTest(Point p) => false;

        public void Render(ImmediateDrawingContext context)
        {
            var feature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
            if (feature == null)
            {
                _host.Drain(null);
                return;
            }
            using var lease = feature.Lease();
            _host.Drain(lease.GrContext);
        }
    }
}
