using System.Globalization;
using System.Text;

namespace SolumanirSheetDesignerStudio.Core.Layout;

/// <summary>
/// Lays out the Markdown block model with the sheet's typography (handoff 4.8), including CSS margin collapsing,
/// so block positions match the browser prototype.
/// </summary>
internal sealed class MarkdownLayouter
{
    public const double BodySize = 20;
    public const double LineHeightFactor = 1.45;
    private static readonly double[] HeadingSizes = { 25, 22, 19, 17, 16, 16 };
    private static readonly ColorRgba CellBorder = ColorRgba.FromRgba(42, 33, 26, 0.55);
    private static readonly ColorRgba HeaderFill = ColorRgba.FromRgba(60, 40, 20, 0.08);
    private static readonly ColorRgba CodeFill = ColorRgba.FromRgba(60, 40, 20, 0.10);

    private readonly ITextShaper _shaper;
    private readonly ColorRgba _ink, _accent;
    private readonly List<SceneItem> _items;

    public MarkdownLayouter(ITextShaper shaper, ColorRgba ink, ColorRgba accent, List<SceneItem> items)
    {
        _shaper = shaper;
        _ink = ink;
        _accent = accent;
        _items = items;
    }

    public readonly record struct Ctx(double X, double Width, bool Italic = false, int ListDepth = 0, bool Tight = false);

    private FontSpec BodyFont(Ctx ctx) => new(FontFamilyId.Body, 400, ctx.Italic, BodySize);

    // ---------- margins (CSS collapsing: a container's margin merges with its first/last child's) ----------

    public (double Top, double Bottom) Margins(MdBlock block, Ctx ctx)
    {
        switch (block)
        {
            case MdParagraph: return ctx.Tight ? (0, 0) : (0, 10);
            case MdHeading: return (14, 4);
            case MdRule: return (14, 14);
            case MdTable: return (10, 10);
            case MdCodeBlock: return (10, 10);
            case MdQuote q:
            {
                var inner = ctx with { Tight = false };
                double top = 10, bottom = 10;
                if (q.Blocks.Count > 0)
                {
                    top = Math.Max(top, Margins(q.Blocks[0], inner).Top);
                    bottom = Math.Max(bottom, Margins(q.Blocks[^1], inner).Bottom);
                }
                return (top, bottom);
            }
            case MdList l:
            {
                double own = ctx.ListDepth > 0 ? 0 : 8; // nested lists have no margin of their own (UA stylesheet)
                double top = own, bottom = own;
                if (l.Items.Count > 0)
                {
                    top = Math.Max(top, ItemMargins(l, l.Items[0], ctx).Top);
                    bottom = Math.Max(bottom, ItemMargins(l, l.Items[^1], ctx).Bottom);
                }
                return (top, bottom);
            }
            default: return (0, 0);
        }
    }

    private (double Top, double Bottom) ItemMargins(MdList list, IReadOnlyList<MdBlock> item, Ctx ctx)
    {
        var inner = ctx with { ListDepth = ctx.ListDepth + 1, Tight = list.Tight };
        double top = 2, bottom = 2;
        if (item.Count > 0)
        {
            top = Math.Max(top, Margins(item[0], inner).Top);
            bottom = Math.Max(bottom, Margins(item[^1], inner).Bottom);
        }
        return (top, bottom);
    }

    // ---------- blocks ----------

    /// <summary>Stacks blocks from <paramref name="y"/> and returns the bottom edge (including the trailing margin unless suppressed).</summary>
    public double LayoutBlocks(IReadOnlyList<MdBlock> blocks, Ctx ctx, double y, bool suppressFirstMargin, bool suppressLastMargin)
    {
        double prevBottom = 0;
        for (int i = 0; i < blocks.Count; i++)
        {
            var (top, bottom) = Margins(blocks[i], ctx);
            if (i == 0) { if (!suppressFirstMargin) y += top; }
            else y += Math.Max(prevBottom, top);
            y = LayoutBlock(blocks[i], ctx, y);
            prevBottom = bottom;
        }
        if (blocks.Count > 0 && !suppressLastMargin) y += prevBottom;
        return y;
    }

    private double LayoutBlock(MdBlock block, Ctx ctx, double y)
    {
        switch (block)
        {
            case MdParagraph p:
            {
                var font = BodyFont(ctx);
                return TextBlock(Flatten(p.Inlines, font, _ink, false), font, font.Size * LineHeightFactor, ctx.X, ctx.Width, y, pretty: true);
            }
            case MdHeading h:
            {
                double size = HeadingSizes[Math.Clamp(h.Level, 1, 6) - 1];
                var font = new FontSpec(FontFamilyId.Display, 700, false, size, size * 0.05);
                return TextBlock(Flatten(h.Inlines, font, _accent, false), font, size * LineHeightFactor, ctx.X, ctx.Width, y, pretty: false);
            }
            case MdRule:
                _items.Add(new FillRectItem(new RectD(ctx.X, y, ctx.Width, 1), _ink));
                return y + 1;
            case MdQuote q:
                return LayoutBlocks(q.Blocks, new Ctx(ctx.X + 26, Math.Max(20, ctx.Width - 26), true, ctx.ListDepth, false), y, true, true);
            case MdList l:
                return LayoutList(l, ctx, y);
            case MdCodeBlock c:
                return LayoutCode(c, ctx, y);
            case MdTable t:
                return LayoutTable(t, ctx, y);
            default:
                return y;
        }
    }

    private double TextBlock(List<InlineSpan> spans, FontSpec strut, double lineHeight, double x, double width, double y, bool pretty)
    {
        var box = new InlineBox(_shaper, spans);
        if (box.IsEmpty) return y;
        double baseline = TextMetrics.Box(_shaper, strut, lineHeight).Baseline;
        foreach (var line in box.Break(width, breakWord: true, pretty))
        {
            box.Emit(_items, line, x, y + baseline);
            y += lineHeight;
        }
        return y;
    }

    private double LayoutList(MdList list, Ctx ctx, double y)
    {
        const double indent = 28;
        var font = BodyFont(ctx);
        double lineHeight = font.Size * LineHeightFactor;
        var lineBox = TextMetrics.Box(_shaper, font, lineHeight);
        int ascent = (int)TextMetrics.Ascent(_shaper, font);
        var inner = new Ctx(ctx.X + indent, Math.Max(20, ctx.Width - indent), ctx.Italic, ctx.ListDepth + 1, list.Tight);

        double prevBottom = 0;
        for (int i = 0; i < list.Items.Count; i++)
        {
            var item = list.Items[i];
            var (top, bottom) = ItemMargins(list, item, ctx);
            if (i > 0) y += Math.Max(prevBottom, top);
            prevBottom = bottom;

            double baseline = y + lineBox.Baseline;
            if (list.Ordered)
            {
                // "1." right-aligned so that one space separates it from the text, like a browser's outside marker.
                string label = (list.Start + i).ToString(CultureInfo.InvariantCulture) + ".";
                var shaped = _shaper.Shape(label, font);
                double w = 0, space = 0;
                foreach (float a in shaped.Advances) w += a;
                foreach (float a in _shaper.Shape(" ", font).Advances) space += a;
                var pos = new float[shaped.Count * 2];
                double gx = 0;
                for (int g = 0; g < shaped.Count; g++)
                {
                    pos[g * 2] = (float)(gx + shaped.OffsetsX[g]);
                    pos[g * 2 + 1] = -shaped.OffsetsY[g];
                    gx += shaped.Advances[g];
                }
                InlineBox.EmitGlyphs(_items, font, shaped.Glyphs, shaped.Faces, pos, inner.X - space - w, baseline, w, _ink, null);
            }
            else
            {
                // Chromium's symbol marker geometry (integer arithmetic on the rounded ascent).
                int offset = ascent * 2 / 3;
                int size = (offset + 1) / 2;
                double mx = inner.X - offset - 7 + 1;
                double my = baseline - ascent + 3 * (ascent - offset) / 2;
                var rect = new RectD(mx, my, size, size);
                if (ctx.ListDepth == 0) _items.Add(new EllipseItem(rect, _ink, stroke: false));
                else if (ctx.ListDepth == 1) _items.Add(new EllipseItem(rect, _ink, stroke: true));
                else _items.Add(new FillRectItem(rect, _ink));
            }

            y = item.Count == 0 ? y + lineHeight : LayoutBlocks(item, inner, y, true, true);
        }
        return y;
    }

    private double LayoutCode(MdCodeBlock code, Ctx ctx, double y)
    {
        var font = new FontSpec(FontFamilyId.Mono, 400, false, BodySize * 0.82);
        double lineHeight = font.Size * LineHeightFactor;
        const double padX = 10, padY = 8;
        var spans = new List<InlineSpan>();
        string[] lines = code.Text.Replace("\r", "").TrimEnd('\n').Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            if (i > 0) spans.Add(InlineSpan.Break(font));
            spans.Add(new InlineSpan(lines[i].Replace("\t", "    "), font, _ink));
        }
        var box = new InlineBox(_shaper, spans);
        var broken = box.Break(Math.Max(20, ctx.Width - 2 * padX), breakWord: true, pretty: false);
        double height = broken.Count * lineHeight + 2 * padY;
        _items.Add(new FillRectItem(new RectD(ctx.X, y, ctx.Width, height), CodeFill));
        double baseline = TextMetrics.Box(_shaper, font, lineHeight).Baseline;
        double ly = y + padY;
        foreach (var line in broken)
        {
            box.Emit(_items, line, ctx.X + padX, ly + baseline);
            ly += lineHeight;
        }
        return y + height;
    }

    private double LayoutTable(MdTable table, Ctx ctx, double y)
    {
        int cols = Math.Max(table.Header.Count, table.Rows.Count == 0 ? 0 : table.Rows.Max(r => r.Count));
        if (cols == 0) return y;
        const double padX = 10, padY = 4, line = 1;

        double cellSize = BodySize * 0.92;
        var cellFont = new FontSpec(FontFamilyId.Body, 400, ctx.Italic, cellSize);
        double cellLine = cellSize * LineHeightFactor;
        var headFont = new FontSpec(FontFamilyId.Label, 700, false, 14, 14 * 0.12);
        double headLine = 14 * LineHeightFactor;

        // Build every cell's inline box once; rows[0] is the header.
        var rows = new List<InlineBox?[]>();
        var header = new InlineBox?[cols];
        for (int c = 0; c < cols; c++)
            header[c] = c < table.Header.Count ? new InlineBox(_shaper, Flatten(table.Header[c], headFont, _ink, true)) : null;
        rows.Add(header);
        foreach (var r in table.Rows)
        {
            var cells = new InlineBox?[cols];
            for (int c = 0; c < cols; c++)
                cells[c] = c < r.Count ? new InlineBox(_shaper, Flatten(r[c], cellFont, _ink, false)) : null;
            rows.Add(cells);
        }

        // Automatic table layout: columns sized between their min- and max-content widths.
        var min = new double[cols];
        var max = new double[cols];
        foreach (var r in rows)
            for (int c = 0; c < cols; c++)
            {
                if (r[c] is not { } b) continue;
                min[c] = Math.Max(min[c], b.MinContentWidth());
                max[c] = Math.Max(max[c], b.MaxContentWidth());
            }
        double avail = ctx.Width - (cols + 1) * line;
        double sumMin = 0, sumMax = 0;
        for (int c = 0; c < cols; c++) { min[c] += 2 * padX; max[c] += 2 * padX; sumMin += min[c]; sumMax += max[c]; }
        var width = new double[cols];
        for (int c = 0; c < cols; c++)
        {
            if (sumMax <= avail) width[c] = sumMax > 0 ? max[c] + (avail - sumMax) * max[c] / sumMax : avail / cols;
            else if (sumMin >= avail) width[c] = min[c] * avail / sumMin;
            else width[c] = min[c] + (max[c] - min[c]) * (avail - sumMin) / (sumMax - sumMin);
        }

        // Rows.
        double top = y;
        var rowTops = new List<double>();
        double cy = y + line;
        for (int r = 0; r < rows.Count; r++)
        {
            bool head = r == 0;
            double lineHeight = head ? headLine : cellLine;
            var strut = head ? headFont : cellFont;
            double baseline = TextMetrics.Box(_shaper, strut, lineHeight).Baseline;
            var broken = new List<LineRange>?[cols];
            double rowHeight = lineHeight;
            for (int c = 0; c < cols; c++)
            {
                if (rows[r][c] is not { IsEmpty: false } b) continue;
                broken[c] = b.Break(Math.Max(1, width[c] - 2 * padX), breakWord: true, pretty: false);
                rowHeight = Math.Max(rowHeight, broken[c]!.Count * lineHeight);
            }
            rowHeight += 2 * padY;
            rowTops.Add(cy);

            double cxp = ctx.X + line;
            for (int c = 0; c < cols; c++)
            {
                if (head) _items.Add(new FillRectItem(new RectD(cxp, cy, width[c], rowHeight), HeaderFill));
                if (broken[c] is { } lines)
                {
                    double ly = cy + padY;
                    foreach (var ln in lines)
                    {
                        rows[r][c]!.Emit(_items, ln, cxp + padX, ly + baseline);
                        ly += lineHeight;
                    }
                }
                cxp += width[c] + line;
            }
            cy += rowHeight + line;
        }
        double bottom = cy;

        // Grid: full-width horizontals, verticals only between them so translucent lines never overlap.
        double gx = ctx.X;
        rowTops.Add(bottom); // sentinel: top of the non-existent next row
        for (int r = 0; r < rowTops.Count; r++)
            _items.Add(new FillRectItem(new RectD(ctx.X, rowTops[r] - line, ctx.Width, line), CellBorder));
        for (int c = 0; c <= cols; c++)
        {
            for (int r = 0; r + 1 < rowTops.Count; r++)
                _items.Add(new FillRectItem(new RectD(gx, rowTops[r], line, rowTops[r + 1] - line - rowTops[r]), CellBorder));
            if (c < cols) gx += line + width[c];
        }
        return bottom;
    }

    // ---------- inline flattening ----------

    private static bool SameStyle(InlineSpan a, InlineSpan b) => (a with { Text = "" }).Equals(b with { Text = "" });

    /// <summary>
    /// Turns the inline tree into styled runs and applies CSS white-space: pre-line (collapse runs of spaces, strip
    /// them around line breaks, keep newlines as breaks).
    /// </summary>
    internal List<InlineSpan> Flatten(IReadOnlyList<MdInline> inlines, FontSpec font, ColorRgba color, bool upper)
    {
        var raw = new List<InlineSpan>();
        Walk(inlines, font, color, false, false, upper, raw);

        var result = new List<InlineSpan>(raw.Count);
        // A collapsed run of white space belongs to the run it started in, as in CSS: the space after "**bold**" is
        // regular, the space inside "**two words**" is bold.
        InlineSpan? pendingSpace = null;
        bool atLineStart = true;
        var sb = new StringBuilder();

        void AddBreak()
        {
            pendingSpace = null;
            atLineStart = true;
            result.Add(InlineSpan.Break(font));
        }

        foreach (var span in raw)
        {
            if (span.IsBreak) { AddBreak(); continue; }
            sb.Clear();
            foreach (char ch in span.Text)
            {
                if (ch == '\n')
                {
                    if (sb.Length > 0) { result.Add(span with { Text = sb.ToString() }); sb.Clear(); }
                    AddBreak();
                    continue;
                }
                if (ch is ' ' or '\t' or '\r')
                {
                    if (!atLineStart) pendingSpace ??= span;
                    continue;
                }
                if (pendingSpace is { } owner)
                {
                    pendingSpace = null;
                    if (SameStyle(owner, span)) sb.Append(' ');
                    else if (result.Count > 0 && !result[^1].IsBreak && SameStyle(result[^1], owner)) result[^1] = result[^1] with { Text = result[^1].Text + " " };
                    else result.Add(owner with { Text = " " });
                }
                sb.Append(ch);
                atLineStart = false;
            }
            if (sb.Length > 0) result.Add(span with { Text = sb.ToString() });
        }
        return result;
    }

    private void Walk(IReadOnlyList<MdInline> inlines, FontSpec font, ColorRgba color, bool underline, bool strike, bool upper, List<InlineSpan> output)
    {
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case MdText t:
                    if (t.Text.Length > 0) output.Add(new InlineSpan(upper ? TextCase.Upper(t.Text) : t.Text, font, color, underline, strike));
                    break;
                case MdEmphasis e:
                    var f = e.Kind switch
                    {
                        MdEmphasisKind.Italic => font with { Italic = true },
                        MdEmphasisKind.Bold => font with { Weight = 700 },
                        _ => font,
                    };
                    Walk(e.Children, f, color, underline, strike || e.Kind == MdEmphasisKind.Strike, upper, output);
                    break;
                case MdCode c:
                    if (c.Text.Length > 0)
                        output.Add(new InlineSpan(upper ? TextCase.Upper(c.Text) : c.Text,
                            font with { Family = FontFamilyId.Mono, Size = font.Size * 0.82 }, color, underline, strike, CodeBox: true));
                    break;
                case MdLink l:
                    Walk(l.Children, font, _accent, true, strike, upper, output);
                    break;
                case MdBreak:
                    output.Add(InlineSpan.Break(font));
                    break;
            }
        }
    }
}
