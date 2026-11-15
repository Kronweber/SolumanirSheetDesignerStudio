using System.Runtime.CompilerServices;
using SkiaSharp;
using SolumanirSheetDesignerStudio.Core.Layout;

namespace SolumanirSheetDesignerStudio.Rendering;

/// <summary>Draws the artwork of one placement. The screen uses cached previews; the export uses streamed bands.</summary>
public interface IImagePainter
{
    /// <param name="canvas">Canvas with the sheet transform applied.</param>
    /// <param name="local">Destination in sheet units; maps exactly onto <paramref name="device"/>.</param>
    /// <param name="device">Destination in whole device pixels of this canvas.</param>
    void Draw(SKCanvas canvas, ImagePlacement placement, SKRect local, SKRectI device);
}

/// <summary>
/// The one paint pipeline (handoff 6.1): executes a sheet display list on any <see cref="SKCanvas"/> whose matrix is a
/// scale + translation. Text and ornaments are vectors, so they are re-rasterised at the canvas scale; axis-aligned
/// edges are snapped to the device pixel grid so hairlines stay crisp at every zoom and export size.
/// Thread-safe: several strips can be painted concurrently.
/// </summary>
public sealed class ScenePainter
{
    private readonly FontLibrary _fonts;
    private readonly ConditionalWeakTable<GlyphRunItem, SKTextBlob> _blobs = new();
    private readonly ConditionalWeakTable<GlyphRunItem, SKPath> _outlines = new(), _fineOutlines = new();

    /// <summary>Above this size in device pixels Skia stops using cached glyph images and fills the outlines instead.</summary>
    private const double OutlineTextPx = 200;
    private const double FineOutlineTextPx = 1200;

    public ScenePainter(FontLibrary fonts) => _fonts = fonts;

    private readonly struct Xf
    {
        public readonly double S, Tx, Ty;
        public readonly bool Snap;

        public Xf(SKMatrix m)
        {
            S = m.ScaleX;
            Tx = m.TransX;
            Ty = m.TransY;
            // Snapping needs a uniform, upright transform; anything else is drawn antialiased as-is.
            Snap = m.SkewX == 0 && m.SkewY == 0 && m.ScaleX > 0 && Math.Abs(m.ScaleX - m.ScaleY) < 1e-4 * m.ScaleX;
        }

        public double X(double v) => PixelGrid.Round(v * S + Tx);
        public double Y(double v) => PixelGrid.Round(v * S + Ty);
        public float LocalX(double device) => (float)((device - Tx) / S);
        public float LocalY(double device) => (float)((device - Ty) / S);
    }

    public void Paint(SKCanvas canvas, IReadOnlyList<SceneItem> items, IImagePainter? images)
    {
        var xf = new Xf(canvas.TotalMatrix);
        var visible = canvas.LocalClipBounds;
        if (visible.IsEmpty) return;
        var clip = new RectD(visible.Left - 1, visible.Top - 1, visible.Width + 2, visible.Height + 2);

        using var paint = new SKPaint();
        foreach (var item in items)
        {
            if (!item.Bounds.Intersects(clip) && item is not VignetteItem) continue;
            switch (item)
            {
                case FillRectItem f: FillRect(canvas, paint, xf, f.Rect, f.Color); break;
                case FrameItem f: Frame(canvas, paint, xf, f); break;
                case GlyphRunItem g: Glyphs(canvas, paint, xf, g); break;
                case ImageItem i: Image(canvas, xf, i.Placement, images); break;
                case DiamondItem d: Diamond(canvas, paint, d); break;
                case DashedLineItem d: Dashed(canvas, paint, xf, d); break;
                case EllipseItem e: Ellipse(canvas, paint, e); break;
                case VignetteItem v: Vignette(canvas, paint, xf, v); break;
                case StripesItem s: Stripes(canvas, paint, s); break;
            }
        }
    }

    private static SKColor Color(ColorRgba c) => new(c.R, c.G, c.B, c.A);

    private static void Solid(SKPaint paint, ColorRgba color, bool antialias)
    {
        paint.Reset();
        paint.Color = Color(color);
        paint.IsAntialias = antialias;
        paint.Style = SKPaintStyle.Fill;
    }

    /// <summary>Device-pixel edges of a rectangle: each edge rounds on its own, and a visible rect never collapses.</summary>
    private static bool SnapRect(in Xf xf, RectD r, out double l, out double t, out double rr, out double b)
    {
        l = xf.X(r.X); t = xf.Y(r.Y); rr = xf.X(r.Right); b = xf.Y(r.Bottom);
        if (r.W <= 0 || r.H <= 0) return false;
        if (rr <= l) rr = l + 1;
        if (b <= t) b = t + 1;
        return true;
    }

    private static void FillRect(SKCanvas canvas, SKPaint paint, in Xf xf, RectD r, ColorRgba color)
    {
        if (color.A == 0) return;
        if (!xf.Snap)
        {
            Solid(paint, color, true);
            canvas.DrawRect(new SKRect((float)r.X, (float)r.Y, (float)r.Right, (float)r.Bottom), paint);
            return;
        }
        if (!SnapRect(xf, r, out double l, out double t, out double rr, out double b)) return;
        Solid(paint, color, false);
        canvas.DrawRect(new SKRect(xf.LocalX(l), xf.LocalY(t), xf.LocalX(rr), xf.LocalY(b)), paint);
    }

    private static void Frame(SKCanvas canvas, SKPaint paint, in Xf xf, FrameItem f)
    {
        if (f.Color.A == 0 || f.Outer.W <= 0 || f.Outer.H <= 0) return;
        var o = f.Outer;
        if (!xf.Snap)
        {
            paint.Reset();
            paint.Color = Color(f.Color);
            paint.IsAntialias = true;
            paint.Style = SKPaintStyle.Stroke;
            paint.StrokeWidth = (float)f.Thickness;
            float h = (float)f.Thickness / 2;
            canvas.DrawRect(new SKRect((float)o.X + h, (float)o.Y + h, (float)o.Right - h, (float)o.Bottom - h), paint);
            return;
        }

        // Inner edges are the snapped positions of (outer ± thickness): whatever sits inside the frame snaps to the
        // same pixels, so artwork abuts the outline without gaps or overlap.
        double l = xf.X(o.X), t = xf.Y(o.Y), r = xf.X(o.Right), b = xf.Y(o.Bottom);
        double l2 = Math.Max(l + 1, xf.X(o.X + f.Thickness)), t2 = Math.Max(t + 1, xf.Y(o.Y + f.Thickness));
        double r2 = Math.Min(r - 1, xf.X(o.Right - f.Thickness)), b2 = Math.Min(b - 1, xf.Y(o.Bottom - f.Thickness));
        Solid(paint, f.Color, false);
        if (r2 <= l2 || b2 <= t2)
        {
            canvas.DrawRect(new SKRect(xf.LocalX(l), xf.LocalY(t), xf.LocalX(Math.Max(r, l + 1)), xf.LocalY(Math.Max(b, t + 1))), paint);
            return;
        }
        // Four bands that never overlap (the colour may be translucent).
        canvas.DrawRect(new SKRect(xf.LocalX(l), xf.LocalY(t), xf.LocalX(r), xf.LocalY(t2)), paint);
        canvas.DrawRect(new SKRect(xf.LocalX(l), xf.LocalY(b2), xf.LocalX(r), xf.LocalY(b)), paint);
        canvas.DrawRect(new SKRect(xf.LocalX(l), xf.LocalY(t2), xf.LocalX(l2), xf.LocalY(b2)), paint);
        canvas.DrawRect(new SKRect(xf.LocalX(r2), xf.LocalY(t2), xf.LocalX(r), xf.LocalY(b2)), paint);
    }

    private static void Dashed(SKCanvas canvas, SKPaint paint, in Xf xf, DashedLineItem d)
    {
        if (d.Color.A == 0) return;
        double top = xf.Snap ? xf.Y(d.Y) : d.Y * xf.S + xf.Ty;
        double thicknessPx = xf.Snap ? Math.Max(1, PixelGrid.Round(d.Thickness * xf.S)) : d.Thickness * xf.S;
        float y = xf.LocalY(top + thicknessPx / 2);
        paint.Reset();
        paint.Color = Color(d.Color);
        paint.IsAntialias = !xf.Snap;
        paint.Style = SKPaintStyle.Stroke;
        paint.StrokeWidth = (float)(thicknessPx / xf.S);
        float dash = (float)(3 * d.Thickness);
        using var effect = SKPathEffect.CreateDash(new[] { dash, dash }, 0);
        paint.PathEffect = effect;
        canvas.DrawLine((float)d.X1, y, (float)d.X2, y, paint);
        paint.PathEffect = null;
    }

    private static void Diamond(SKCanvas canvas, SKPaint paint, DiamondItem d)
    {
        if (d.Color.A == 0) return;
        float r = (float)(d.Side * Math.Sqrt(2) / 2), cx = (float)d.Cx, cy = (float)d.Cy;
        using var path = new SKPath();
        path.MoveTo(cx, cy - r);
        path.LineTo(cx + r, cy);
        path.LineTo(cx, cy + r);
        path.LineTo(cx - r, cy);
        path.Close();
        Solid(paint, d.Color, true);
        canvas.DrawPath(path, paint);
    }

    private static void Ellipse(SKCanvas canvas, SKPaint paint, EllipseItem e)
    {
        Solid(paint, e.Color, true);
        var r = new SKRect((float)e.Rect.X, (float)e.Rect.Y, (float)e.Rect.Right, (float)e.Rect.Bottom);
        if (e.Stroke)
        {
            paint.Style = SKPaintStyle.Stroke;
            paint.StrokeWidth = 1;
            r.Inflate(-0.5f, -0.5f);
        }
        canvas.DrawOval(r, paint);
    }

    private static void Vignette(SKCanvas canvas, SKPaint paint, in Xf xf, VignetteItem v)
    {
        var r = v.Rect;
        if (r.W <= 0 || r.H <= 0) return;
        // CSS "radial-gradient(ellipse at center, …)": the ellipse reaches the farthest corner.
        float rx = (float)(r.W / 2 * Math.Sqrt(2)), ry = (float)(r.H / 2 * Math.Sqrt(2));
        var edge = Color(v.Edge);
        using var shader = SKShader.CreateRadialGradient(
            new SKPoint(0, 0), 1f,
            new[] { edge.WithAlpha(0), edge },
            new[] { (float)v.Start, 1f },
            SKShaderTileMode.Clamp,
            SKMatrix.CreateScaleTranslation(rx, ry, (float)r.CenterX, (float)r.CenterY));
        paint.Reset();
        paint.Shader = shader;
        paint.IsAntialias = false;
        if (xf.Snap && SnapRect(xf, r, out double l, out double t, out double rr, out double b))
            canvas.DrawRect(new SKRect(xf.LocalX(l), xf.LocalY(t), xf.LocalX(rr), xf.LocalY(b)), paint);
        else
            canvas.DrawRect(new SKRect((float)r.X, (float)r.Y, (float)r.Right, (float)r.Bottom), paint);
        paint.Shader = null;
    }

    private static void Stripes(SKCanvas canvas, SKPaint paint, StripesItem s)
    {
        var r = s.Rect;
        if (r.W <= 0 || r.H <= 0) return;
        // CSS repeating-linear-gradient(135deg, colour 0 band, transparent band 2·band): stripes along x + y = const.
        double step = s.Band * Math.Sqrt(2);
        canvas.Save();
        canvas.ClipRect(new SKRect((float)r.X, (float)r.Y, (float)r.Right, (float)r.Bottom), SKClipOperation.Intersect, true);
        Solid(paint, s.Color, true);
        using var path = new SKPath();
        for (double c = 0; c < r.W + r.H; c += 2 * step)
        {
            float c1 = (float)c, c2 = (float)(c + step), x0 = (float)r.X, y0 = (float)r.Y;
            path.MoveTo(x0 + c1, y0);
            path.LineTo(x0 + c2, y0);
            path.LineTo(x0, y0 + c2);
            path.LineTo(x0, y0 + c1);
            path.Close();
        }
        canvas.DrawPath(path, paint);
        canvas.Restore();
    }

    private void Glyphs(SKCanvas canvas, SKPaint paint, in Xf xf, GlyphRunItem g)
    {
        if (g.Color.A == 0 || g.Glyphs.Length == 0) return;
        var blob = _blobs.GetValue(g, BuildBlob);
        Solid(paint, g.Color, true);

        // Skia rounds a horizontal baseline to a whole device pixel, from single-precision numbers that include the
        // canvas offset. In an export every strip has a different offset, so a baseline that sits near x.5 could
        // round up in one strip and down in the next: a one-pixel step in the middle of a line of text. Doing the
        // rounding here, from the page coordinate, puts the baseline on the same pixel row in every strip.
        float y = xf.Snap ? xf.LocalY(xf.Y(g.Y)) : (float)g.Y;

        // Very large text (high export scales) is filled from its outlines. Skia flattens a curve into at most 32
        // straight pieces and, when a curve crosses the edge of the surface, flattens only the visible part — so the
        // same letter came out up to 0.2 px different on the two sides of a strip boundary. Outlines whose curves
        // are pre-cut into short pieces leave nothing for that to act on (the error shrinks 256-fold or more).
        double devicePx = g.Font.Size * xf.S;
        SKPath? outline = !xf.Snap || devicePx < OutlineTextPx ? null
            : devicePx < FineOutlineTextPx ? _outlines.GetValue(g, run => BuildOutline(run, 4)) : _fineOutlines.GetValue(g, run => BuildOutline(run, 16));

        void Draw()
        {
            if (outline == null)
            {
                canvas.DrawText(blob, (float)g.X, y, paint);
                return;
            }
            canvas.Save();
            canvas.Translate((float)g.X, y);
            canvas.DrawPath(outline, paint);
            canvas.Restore();
        }

        if (g.Clip is { } clip)
        {
            canvas.Save();
            if (xf.Snap && SnapRect(xf, clip, out double l, out double t, out double r, out double bottom))
                canvas.ClipRect(new SKRect(xf.LocalX(l), xf.LocalY(t), xf.LocalX(r), xf.LocalY(bottom)), SKClipOperation.Intersect, false);
            else
                canvas.ClipRect(new SKRect((float)clip.X, (float)clip.Y, (float)clip.Right, (float)clip.Bottom), SKClipOperation.Intersect, true);
            Draw();
            canvas.Restore();
        }
        else
        {
            Draw();
        }
    }

    /// <summary>The run's glyph outlines as one path (origin at the run's pen position), every curve cut into <paramref name="pieces"/> shorter curves.</summary>
    private SKPath BuildOutline(GlyphRunItem g, int pieces)
    {
        using var font = _fonts.CreateSkFont(g.Font, g.Face);
        var path = new SKPath { FillType = SKPathFillType.Winding };
        var pts = new SKPoint[4];
        for (int i = 0; i < g.Glyphs.Length; i++)
        {
            using var glyph = font.GetGlyphPath(g.Glyphs[i]);
            if (glyph == null || glyph.IsEmpty) continue;
            float dx = g.Positions[i * 2], dy = g.Positions[i * 2 + 1];
            SKPoint At(SKPoint p) => new(p.X + dx, p.Y + dy);

            using var it = glyph.CreateRawIterator();
            SKPathVerb verb;
            while ((verb = it.Next(pts)) != SKPathVerb.Done)
            {
                switch (verb)
                {
                    case SKPathVerb.Move: path.MoveTo(At(pts[0])); break;
                    case SKPathVerb.Line: path.LineTo(At(pts[1])); break;
                    case SKPathVerb.Close: path.Close(); break;
                    case SKPathVerb.Conic: path.ConicTo(At(pts[1]), At(pts[2]), it.ConicWeight()); break; // not used by TrueType or CFF outlines
                    case SKPathVerb.Quad:
                        for (int k = 0; k < pieces; k++)
                        {
                            float t0 = k / (float)pieces, t1 = (k + 1) / (float)pieces;
                            // Sub-curve [t0, t1] of a quadratic: its end point, and the start point pushed along the start tangent.
                            var start = Quad(pts, t0);
                            var tangent = QuadTangent(pts, t0);
                            float h = (t1 - t0) / 2;
                            path.QuadTo(At(new SKPoint(start.X + tangent.X * h, start.Y + tangent.Y * h)), At(Quad(pts, t1)));
                        }
                        break;
                    case SKPathVerb.Cubic:
                        for (int k = 0; k < pieces; k++)
                        {
                            float t0 = k / (float)pieces, t1 = (k + 1) / (float)pieces, h = (t1 - t0) / 3;
                            SKPoint a = Cubic(pts, t0), b = Cubic(pts, t1), ta = CubicTangent(pts, t0), tb = CubicTangent(pts, t1);
                            path.CubicTo(At(new SKPoint(a.X + ta.X * h, a.Y + ta.Y * h)), At(new SKPoint(b.X - tb.X * h, b.Y - tb.Y * h)), At(b));
                        }
                        break;
                }
            }
        }
        return path;
    }

    private static SKPoint Quad(SKPoint[] p, float t)
    {
        float u = 1 - t;
        return new SKPoint(u * u * p[0].X + 2 * u * t * p[1].X + t * t * p[2].X, u * u * p[0].Y + 2 * u * t * p[1].Y + t * t * p[2].Y);
    }

    private static SKPoint QuadTangent(SKPoint[] p, float t)
    {
        float u = 1 - t;
        return new SKPoint(2 * (u * (p[1].X - p[0].X) + t * (p[2].X - p[1].X)), 2 * (u * (p[1].Y - p[0].Y) + t * (p[2].Y - p[1].Y)));
    }

    private static SKPoint Cubic(SKPoint[] p, float t)
    {
        float u = 1 - t, a = u * u * u, b = 3 * u * u * t, c = 3 * u * t * t, d = t * t * t;
        return new SKPoint(a * p[0].X + b * p[1].X + c * p[2].X + d * p[3].X, a * p[0].Y + b * p[1].Y + c * p[2].Y + d * p[3].Y);
    }

    private static SKPoint CubicTangent(SKPoint[] p, float t)
    {
        float u = 1 - t, a = 3 * u * u, b = 6 * u * t, c = 3 * t * t;
        return new SKPoint(a * (p[1].X - p[0].X) + b * (p[2].X - p[1].X) + c * (p[3].X - p[2].X), a * (p[1].Y - p[0].Y) + b * (p[2].Y - p[1].Y) + c * (p[3].Y - p[2].Y));
    }

    private SKTextBlob BuildBlob(GlyphRunItem g)
    {
        using var font = _fonts.CreateSkFont(g.Font, g.Face);
        var points = new SKPoint[g.Glyphs.Length];
        for (int i = 0; i < points.Length; i++) points[i] = new SKPoint(g.Positions[i * 2], g.Positions[i * 2 + 1]);
        using var builder = new SKTextBlobBuilder();
        builder.AddPositionedRun(g.Glyphs, font, points);
        return builder.Build();
    }

    // ---------- images ----------

    /// <summary>Device-pixel rectangle an image is drawn into (see <see cref="PixelGrid.PlaceImage"/>).</summary>
    public static SKRectI DeviceRect(ImagePlacement p, double scale, double tx, double ty)
    {
        var r = PixelGrid.PlaceImage(p, scale, tx, ty);
        return new SKRectI(r.X, r.Y, r.Right, r.Bottom);
    }

    private static void Image(SKCanvas canvas, in Xf xf, ImagePlacement p, IImagePainter? images)
    {
        if (images == null || p.FileWidth <= 0 || p.FileHeight <= 0 || p.Container.W <= 0 || p.Container.H <= 0) return;
        var device = DeviceRect(p, xf.S, xf.Tx, xf.Ty);
        var local = new SKRect(xf.LocalX(device.Left), xf.LocalY(device.Top), xf.LocalX(device.Right), xf.LocalY(device.Bottom));
        images.Draw(canvas, p, local, device);
    }
}
