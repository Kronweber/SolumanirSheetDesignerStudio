using System.Globalization;

namespace SolumanirSheetDesignerStudio.Core.Layout;

/// <summary>Rectangle in sheet units (1 unit = 1 px at 100 % zoom).</summary>
public readonly record struct RectD(double X, double Y, double W, double H)
{
    public double Right => X + W;
    public double Bottom => Y + H;
    public double CenterX => X + W / 2;
    public double CenterY => Y + H / 2;
    public bool IsEmpty => W <= 0 || H <= 0;

    public RectD Inset(double d) => new(X + d, Y + d, Math.Max(0, W - 2 * d), Math.Max(0, H - 2 * d));
    public RectD Inflate(double d) => new(X - d, Y - d, W + 2 * d, H + 2 * d);
    public bool Contains(double x, double y) => x >= X && x < Right && y >= Y && y < Bottom;
    public bool Intersects(RectD o) => X < o.Right && o.X < Right && Y < o.Bottom && o.Y < Bottom;

    public static RectD FromEdges(double left, double top, double right, double bottom) => new(left, top, right - left, bottom - top);
}

public readonly record struct ColorRgba(byte R, byte G, byte B, byte A = 255)
{
    public static readonly ColorRgba Transparent = new(0, 0, 0, 0);
    public static readonly ColorRgba White = new(255, 255, 255);

    public static ColorRgba FromRgba(int r, int g, int b, double alpha) =>
        new((byte)r, (byte)g, (byte)b, (byte)Math.Clamp((int)Math.Round(alpha * 255), 0, 255));

    public ColorRgba WithAlpha(double alpha) => new(R, G, B, (byte)Math.Clamp((int)Math.Round(alpha * 255), 0, 255));

    /// <summary>Parses #RGB, #RRGGBB or #RRGGBBAA; returns <paramref name="fallback"/> for anything else.</summary>
    public static ColorRgba Parse(string? hex, ColorRgba fallback)
    {
        if (string.IsNullOrWhiteSpace(hex)) return fallback;
        var s = hex.AsSpan().Trim();
        if (s.Length > 0 && s[0] == '#') s = s[1..];
        if (s.Length == 3 && uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint v3))
            return new((byte)(((v3 >> 8) & 0xF) * 17), (byte)(((v3 >> 4) & 0xF) * 17), (byte)((v3 & 0xF) * 17));
        if (s.Length == 6 && uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint v6))
            return new((byte)(v6 >> 16), (byte)(v6 >> 8), (byte)v6);
        if (s.Length == 8 && uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint v8))
            return new((byte)(v8 >> 24), (byte)(v8 >> 16), (byte)(v8 >> 8), (byte)v8);
        return fallback;
    }

    public static bool IsValidHex(string? hex) => Parse(hex, Transparent) != Transparent || Parse(hex, White) != White;

    public string ToHex() => A == 255 ? $"#{R:X2}{G:X2}{B:X2}" : $"#{R:X2}{G:X2}{B:X2}{A:X2}";
}

// =====================================================================================
// Text
// =====================================================================================

public enum FontFamilyId { Display, Body, Label, Mono }

public enum TextAlign { Left, Center, Right }

/// <summary>A font request in sheet units. Display = Cinzel, Body = Alegreya, Label = Alegreya Sans, Mono = system monospace.</summary>
public readonly record struct FontSpec(FontFamilyId Family, int Weight, bool Italic, double Size, double LetterSpacing = 0)
{
}

/// <summary>Font metrics at the requested size; all values positive, in sheet units.</summary>
public readonly record struct FontMetricsInfo(double Ascent, double Descent);

/// <summary>Shaped glyphs of one run, in logical order. Advances exclude letter-spacing (layout adds it per cluster).</summary>
public sealed class ShapedText
{
    public static readonly ShapedText Empty = new(Array.Empty<ushort>(), Array.Empty<int>(), Array.Empty<float>(), Array.Empty<float>(), Array.Empty<float>(), Array.Empty<int>());

    public ShapedText(ushort[] glyphs, int[] clusters, float[] advances, float[] offsetsX, float[] offsetsY, int[] faces)
    {
        Glyphs = glyphs;
        Clusters = clusters;
        Advances = advances;
        OffsetsX = offsetsX;
        OffsetsY = offsetsY;
        Faces = faces;
    }

    public ushort[] Glyphs { get; }
    /// <summary>UTF-16 index of the first character of each glyph's cluster.</summary>
    public int[] Clusters { get; }
    public float[] Advances { get; }
    public float[] OffsetsX { get; }
    /// <summary>Positive = up (font convention).</summary>
    public float[] OffsetsY { get; }
    /// <summary>Opaque typeface handle per glyph (the requested face or a fallback), resolved by the painter.</summary>
    public int[] Faces { get; }
    public int Count => Glyphs.Length;
}

/// <summary>Text measurement and shaping (HarfBuzz in the rendering assembly; a fake in tests).</summary>
public interface ITextShaper
{
    FontMetricsInfo GetMetrics(FontSpec font);

    /// <summary>Shapes with kerning; optional ligatures are disabled when <see cref="FontSpec.LetterSpacing"/> is non-zero (as browsers do).</summary>
    ShapedText Shape(string text, FontSpec font);
}

// =====================================================================================
// Markdown document model (produced by Markdig in the rendering assembly)
// =====================================================================================

public abstract record MdBlock;
public sealed record MdParagraph(IReadOnlyList<MdInline> Inlines) : MdBlock;
public sealed record MdHeading(int Level, IReadOnlyList<MdInline> Inlines) : MdBlock;
public sealed record MdList(bool Ordered, int Start, bool Tight, IReadOnlyList<IReadOnlyList<MdBlock>> Items) : MdBlock;
public sealed record MdQuote(IReadOnlyList<MdBlock> Blocks) : MdBlock;
public sealed record MdRule : MdBlock;
public sealed record MdCodeBlock(string Text) : MdBlock;
public sealed record MdTable(IReadOnlyList<IReadOnlyList<MdInline>> Header, IReadOnlyList<IReadOnlyList<IReadOnlyList<MdInline>>> Rows) : MdBlock;

public abstract record MdInline;
public sealed record MdText(string Text) : MdInline;
public enum MdEmphasisKind { Italic, Bold, Strike }
public sealed record MdEmphasis(MdEmphasisKind Kind, IReadOnlyList<MdInline> Children) : MdInline;
public sealed record MdCode(string Text) : MdInline;
public sealed record MdLink(string Url, IReadOnlyList<MdInline> Children) : MdInline;
public sealed record MdBreak : MdInline;

public interface IMarkdownParser
{
    IReadOnlyList<MdBlock> Parse(string markdown);
}

// =====================================================================================
// Display list
// =====================================================================================

/// <summary>One drawing instruction in sheet units. Painted identically on screen and in the export.</summary>
public abstract class SceneItem
{
    public RectD Bounds { get; protected init; }
}

/// <summary>Axis-aligned fill; edges snap to device pixels and never collapse below one pixel.</summary>
public sealed class FillRectItem : SceneItem
{
    public FillRectItem(RectD rect, ColorRgba color) { Rect = rect; Color = color; Bounds = rect; }
    public RectD Rect { get; }
    public ColorRgba Color { get; }
}

/// <summary>A border band of the given thickness just inside <see cref="Outer"/>.</summary>
public sealed class FrameItem : SceneItem
{
    public FrameItem(RectD outer, double thickness, ColorRgba color) { Outer = outer; Thickness = thickness; Color = color; Bounds = outer; }
    public RectD Outer { get; }
    public double Thickness { get; }
    public ColorRgba Color { get; }
}

/// <summary>Horizontal dashed rule (split-slot divider). <see cref="Y"/> is its top edge.</summary>
public sealed class DashedLineItem : SceneItem
{
    public DashedLineItem(double x1, double x2, double y, double thickness, ColorRgba color)
    {
        X1 = x1; X2 = x2; Y = y; Thickness = thickness; Color = color;
        Bounds = new RectD(x1, y, x2 - x1, thickness);
    }
    public double X1 { get; }
    public double X2 { get; }
    public double Y { get; }
    public double Thickness { get; }
    public ColorRgba Color { get; }
}

/// <summary>A square of side <see cref="Side"/> rotated 45° about its centre.</summary>
public sealed class DiamondItem : SceneItem
{
    public DiamondItem(double cx, double cy, double side, ColorRgba color)
    {
        Cx = cx; Cy = cy; Side = side; Color = color;
        double r = side * Math.Sqrt(2) / 2;
        Bounds = new RectD(cx - r, cy - r, 2 * r, 2 * r);
    }
    public double Cx { get; }
    public double Cy { get; }
    public double Side { get; }
    public ColorRgba Color { get; }
}

public sealed class EllipseItem : SceneItem
{
    public EllipseItem(RectD rect, ColorRgba color, bool stroke) { Rect = rect; Color = color; Stroke = stroke; Bounds = rect; }
    public RectD Rect { get; }
    public ColorRgba Color { get; }
    /// <summary>True = 1-unit outline (list "circle" marker), false = filled.</summary>
    public bool Stroke { get; }
}

/// <summary>Paper vignette: radial gradient to the corners, transparent until <see cref="Start"/> then to <see cref="Edge"/>.</summary>
public sealed class VignetteItem : SceneItem
{
    public VignetteItem(RectD rect, ColorRgba edge, double start) { Rect = rect; Edge = edge; Start = start; Bounds = rect; }
    public RectD Rect { get; }
    public ColorRgba Edge { get; }
    public double Start { get; }
}

/// <summary>135° stripes of the empty-slot placeholder (edit chrome only).</summary>
public sealed class StripesItem : SceneItem
{
    public StripesItem(RectD rect, ColorRgba color, double band) { Rect = rect; Color = color; Band = band; Bounds = rect; }
    public RectD Rect { get; }
    public ColorRgba Color { get; }
    public double Band { get; }
}

/// <summary>Positioned glyphs of one typeface. Vector text: re-rasterised at whatever scale the canvas has.</summary>
public sealed class GlyphRunItem : SceneItem
{
    public GlyphRunItem(FontSpec font, int face, ushort[] glyphs, float[] positions, double x, double y, double width, ColorRgba color, RectD? clip)
    {
        Font = font; Face = face; Glyphs = glyphs; Positions = positions; X = x; Y = y; Color = color; Clip = clip;
        var b = new RectD(x - font.Size, y - font.Size * 1.6, width + 2 * font.Size, font.Size * 2.6);
        Bounds = clip is { } c ? RectD.FromEdges(Math.Max(b.X, c.X), Math.Max(b.Y, c.Y), Math.Min(b.Right, c.Right), Math.Min(b.Bottom, c.Bottom)) : b;
    }
    public FontSpec Font { get; }
    public int Face { get; }
    public ushort[] Glyphs { get; }
    /// <summary>x,y pairs relative to (<see cref="X"/>, <see cref="Y"/>), y down.</summary>
    public float[] Positions { get; }
    public double X { get; }
    /// <summary>Baseline.</summary>
    public double Y { get; }
    public ColorRgba Color { get; }
    public RectD? Clip { get; }
}

/// <summary>An artwork placement. The painter fits the image inside <see cref="Container"/> on the device pixel grid.</summary>
public sealed class ImagePlacement
{
    public required string ImageId { get; init; }
    public required string SectionId { get; init; }
    public required string SlotId { get; init; }
    public required string PartId { get; init; }
    /// <summary>The image box (CSS: the img element's box) in sheet units.</summary>
    public required RectD Container { get; init; }
    /// <summary>Fitted (contain) rectangle inside the container.</summary>
    public required RectD Drawn { get; init; }
    /// <summary>True: bottom-aligned (model rows share a ground line). False: centred (details).</summary>
    public required bool AlignBottom { get; init; }
    public required int FileWidth { get; init; }
    public required int FileHeight { get; init; }
    /// <summary>Pixel height this image should get in the export (0 = native resolution).</summary>
    public double ExportPx { get; init; }

    /// <summary>Export scale at which this image lands at exactly its native pixels (or its row's export height).</summary>
    public double Need => ExportPx > 0
        ? (Drawn.H > 0 ? ExportPx / Drawn.H : 0)
        : (Drawn.W > 0 ? FileWidth / Drawn.W : 0);
}

public sealed class ImageItem : SceneItem
{
    public ImageItem(ImagePlacement placement) { Placement = placement; Bounds = placement.Container; }
    public ImagePlacement Placement { get; }
}
