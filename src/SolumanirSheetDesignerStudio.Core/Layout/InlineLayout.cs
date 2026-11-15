using System.Text;

namespace SolumanirSheetDesignerStudio.Core.Layout;

/// <summary>A run of text with one style, or a forced line break.</summary>
internal readonly record struct InlineSpan(string Text, FontSpec Font, ColorRgba Color, bool Underline = false, bool Strike = false, bool CodeBox = false, bool IsBreak = false)
{
    public static InlineSpan Break(FontSpec font) => new("\n", font, default, IsBreak: true);
}

internal readonly record struct LineRange(int Start, int End);

internal readonly record struct LineBox(double Height, double Baseline);

internal static class TextMetrics
{
    private static double RoundHalfUp(double v) => Math.Floor(v + 0.5);

    public static double Ascent(ITextShaper shaper, FontSpec font) => RoundHalfUp(shaper.GetMetrics(font).Ascent);
    public static double Descent(ITextShaper shaper, FontSpec font) => RoundHalfUp(shaper.GetMetrics(font).Descent);

    /// <summary>
    /// CSS line box: ascent and descent are rounded to whole units and the top half-leading is floored
    /// (this is what Chromium does, and what keeps baselines within a unit of the prototype).
    /// </summary>
    public static LineBox Box(ITextShaper shaper, FontSpec font, double lineHeight)
    {
        double a = Ascent(shaper, font), d = Descent(shaper, font);
        return new LineBox(lineHeight, Math.Floor((lineHeight - (a + d)) / 2) + a);
    }
}

/// <summary>
/// Inline formatting context: shapes styled runs, finds break opportunities, breaks lines greedily (with
/// overflow-wrap: break-word and an approximation of text-wrap: pretty) and emits positioned glyph runs.
/// </summary>
internal sealed class InlineBox
{
    private struct Cluster
    {
        public int Span, G0, G1;
        public double Advance, PadL, PadR;
        public bool Space, BreakAfter, Newline;
        public double Width => Advance + PadL + PadR;
    }

    private const double CodePad = 5;

    private readonly ITextShaper _shaper;
    private readonly IReadOnlyList<InlineSpan> _spans;
    private readonly ShapedText[] _shaped;
    private readonly List<Cluster> _cl = new();

    public InlineBox(ITextShaper shaper, IReadOnlyList<InlineSpan> spans)
    {
        _shaper = shaper;
        _spans = spans;
        _shaped = new ShapedText[spans.Count];

        var all = new StringBuilder();
        var starts = new int[spans.Count];
        for (int i = 0; i < spans.Count; i++)
        {
            starts[i] = all.Length;
            all.Append(spans[i].IsBreak ? "\n" : spans[i].Text);
        }
        string text = all.ToString();

        for (int si = 0; si < spans.Count; si++)
        {
            var span = spans[si];
            if (span.IsBreak)
            {
                _shaped[si] = ShapedText.Empty;
                _cl.Add(new Cluster { Span = si, Newline = true });
                continue;
            }

            var sh = _shaped[si] = span.Text.Length == 0 ? ShapedText.Empty : shaper.Shape(span.Text, span.Font);
            int first = _cl.Count;
            int g = 0;
            while (g < sh.Count)
            {
                int c0 = sh.Clusters[g], g1 = g + 1;
                while (g1 < sh.Count && sh.Clusters[g1] == c0) g1++;
                double adv = span.Font.LetterSpacing;
                for (int k = g; k < g1; k++) adv += sh.Advances[k];

                // The cluster covers the characters up to the next cluster start (clusters are monotonic for LTR text).
                int cEnd = span.Text.Length;
                for (int k = g1; k < sh.Count; k++)
                    if (sh.Clusters[k] > c0) { cEnd = sh.Clusters[k]; break; }
                int lastChar = starts[si] + Math.Max(c0, cEnd - 1);
                char ch = c0 < span.Text.Length ? span.Text[c0] : ' ';

                _cl.Add(new Cluster
                {
                    Span = si, G0 = g, G1 = g1, Advance = adv,
                    Space = cEnd - c0 == 1 && (ch == ' ' || ch == '\t'),
                    BreakAfter = BreakRules.CanBreakAfter(text, lastChar),
                });
                g = g1;
            }

            if (span.CodeBox && _cl.Count > first)
            {
                var a = _cl[first]; a.PadL = CodePad; _cl[first] = a;
                var b = _cl[^1]; b.PadR = CodePad; _cl[^1] = b;
            }
        }
    }

    public bool IsEmpty => _cl.Count == 0;

    // ---------- measuring ----------

    public double LineWidth(LineRange line)
    {
        int end = TrimEnd(line);
        double w = 0;
        for (int i = line.Start; i < end; i++) w += _cl[i].Width;
        return w;
    }

    private int TrimEnd(LineRange line)
    {
        int end = line.End;
        while (end > line.Start && _cl[end - 1].Space) end--;
        return end;
    }

    /// <summary>Width of the widest line when nothing wraps (CSS max-content).</summary>
    public double MaxContentWidth()
    {
        double max = 0;
        foreach (var line in Break(double.PositiveInfinity, breakWord: false, pretty: false)) max = Math.Max(max, LineWidth(line));
        return max;
    }

    /// <summary>Width of the widest unbreakable piece (CSS min-content; break-word does not count).</summary>
    public double MinContentWidth()
    {
        double max = 0, cur = 0;
        foreach (var c in _cl)
        {
            if (c.Newline || c.Space) { max = Math.Max(max, cur); cur = 0; continue; }
            cur += c.Width;
            if (c.BreakAfter) { max = Math.Max(max, cur); cur = 0; }
        }
        return Math.Max(max, cur);
    }

    // ---------- line breaking ----------

    public List<LineRange> Break(double maxWidth, bool breakWord = true, bool pretty = false)
    {
        var lines = new List<LineRange>();
        int n = _cl.Count, i = 0, lineStart = 0, lastBreak = -1, paragraphFirstLine = 0;
        double x = 0;
        const double eps = 0.01;

        while (i < n)
        {
            var c = _cl[i];
            if (c.Newline)
            {
                lines.Add(new LineRange(lineStart, i));
                if (pretty) Prettify(lines, paragraphFirstLine, maxWidth);
                i++;
                lineStart = i;
                paragraphFirstLine = lines.Count;
                x = 0;
                lastBreak = -1;
                continue;
            }

            if (!c.Space && x + c.Width > maxWidth + eps && i > lineStart && (lastBreak >= lineStart || breakWord))
            {
                if (lastBreak >= lineStart)
                {
                    lines.Add(new LineRange(lineStart, lastBreak + 1));
                    i = lastBreak + 1;
                }
                else
                {
                    lines.Add(new LineRange(lineStart, i)); // overflow-wrap: break-word — split inside the word
                }
                while (i < n && _cl[i].Space) i++; // spaces at a soft wrap are consumed
                lineStart = i;
                x = 0;
                lastBreak = -1;
                continue;
            }

            x += c.Width;
            if (c.BreakAfter && !c.Space || c.Space) lastBreak = i;
            i++;
        }
        lines.Add(new LineRange(lineStart, n));
        if (pretty) Prettify(lines, paragraphFirstLine, maxWidth);
        return lines;
    }

    /// <summary>
    /// Approximates CSS <c>text-wrap: pretty</c>: when a paragraph ends in a short orphan line, the last (up to) four
    /// lines are re-broken to minimise raggedness while avoiding the orphan.
    /// </summary>
    private void Prettify(List<LineRange> lines, int first, double maxWidth)
    {
        int count = lines.Count - first;
        if (count < 2 || double.IsInfinity(maxWidth) || maxWidth <= 0) return;
        double third = maxWidth / 3;
        if (LineWidth(lines[^1]) >= third) return;

        int window = Math.Min(4, count);
        int startLine = lines.Count - window;
        int start = lines[startLine].Start, end = lines[^1].End;

        // Candidate line starts: the window start plus every position after a break opportunity.
        var cand = new List<int> { start };
        for (int i = start; i < end - 1; i++)
        {
            if (!_cl[i].BreakAfter && !_cl[i].Space) continue;
            int next = i + 1;
            while (next < end && _cl[next].Space) next++;
            if (next < end && next > cand[^1]) cand.Add(next);
        }
        if (cand.Count < 2 || cand.Count > 400) return;
        // A line that starts inside a word (overflow-wrap split it) cannot be rebuilt from break opportunities:
        // re-breaking would put the whole word back on one over-wide line. Leave such paragraphs as they are.
        for (int l = startLine + 1; l < lines.Count; l++)
            if (!cand.Contains(lines[l].Start)) return;
        cand.Add(end);

        int m = cand.Count - 1; // segments between consecutive candidates
        var best = new double[m + 1];
        var prev = new int[m + 1];
        var lineCount = new int[m + 1];
        Array.Fill(best, double.PositiveInfinity);
        best[0] = 0;
        double linePenalty = maxWidth * maxWidth / 16;

        for (int j = 1; j <= m; j++)
        {
            for (int k = j - 1; k >= 0; k--)
            {
                if (double.IsInfinity(best[k])) continue;
                double w = LineWidth(new LineRange(cand[k], cand[j]));
                if (w > maxWidth + 0.01) break; // lines only get wider as k decreases
                double cost;
                if (j == m) cost = w < third ? 4 * (third - w) * (third - w) : 0; // last line: only orphans are penalised
                else { double slack = Math.Max(0, maxWidth - w); cost = slack * slack; }
                cost += best[k] + linePenalty;
                if (cost < best[j]) { best[j] = cost; prev[j] = k; lineCount[j] = lineCount[k] + 1; }
            }
        }
        if (double.IsInfinity(best[m]) || lineCount[m] > window + 1) return;

        var rebuilt = new List<LineRange>();
        for (int j = m; j > 0; j = prev[j]) rebuilt.Add(new LineRange(cand[prev[j]], cand[j]));
        rebuilt.Reverse();
        lines.RemoveRange(startLine, window);
        lines.AddRange(rebuilt);
    }

    // ---------- emission ----------

    /// <summary>Emits one line with its left edge at <paramref name="x"/> and its baseline at <paramref name="baseline"/>.</summary>
    public void Emit(List<SceneItem> items, LineRange line, double x, double baseline)
    {
        int end = TrimEnd(line);
        int k = line.Start;
        while (k < end)
        {
            int si = _cl[k].Span;
            int m = k;
            while (m < end && _cl[m].Span == si) m++;
            var span = _spans[si];
            var sh = _shaped[si];

            int glyphCount = 0;
            for (int c = k; c < m; c++) glyphCount += _cl[c].G1 - _cl[c].G0;
            var glyphs = new ushort[glyphCount];
            var faces = new int[glyphCount];
            var pos = new float[glyphCount * 2];
            double cx = 0;
            int gi = 0;
            for (int c = k; c < m; c++)
            {
                var cl = _cl[c];
                cx += cl.PadL;
                double gx = cx;
                for (int g = cl.G0; g < cl.G1; g++, gi++)
                {
                    glyphs[gi] = sh.Glyphs[g];
                    faces[gi] = sh.Faces[g];
                    pos[gi * 2] = (float)(gx + sh.OffsetsX[g]);
                    pos[gi * 2 + 1] = -sh.OffsetsY[g];
                    gx += sh.Advances[g];
                }
                cx += cl.Advance + cl.PadR;
            }
            double runWidth = cx;

            if (span.CodeBox)
            {
                double a = TextMetrics.Ascent(_shaper, span.Font), d = TextMetrics.Descent(_shaper, span.Font);
                items.Add(new FillRectItem(new RectD(x, baseline - a - 1, runWidth, a + d + 2), ColorRgba.FromRgba(60, 40, 20, 0.10)));
            }

            EmitGlyphs(items, span.Font, glyphs, faces, pos, x, baseline, runWidth, span.Color, null);

            if (span.Underline || span.Strike)
            {
                double thickness = Math.Max(1, span.Font.Size / 10);
                if (span.Underline)
                    items.Add(new FillRectItem(new RectD(x, baseline + Math.Max(1, Math.Ceiling(thickness / 2)), runWidth, thickness), span.Color));
                if (span.Strike)
                    items.Add(new FillRectItem(new RectD(x, baseline - TextMetrics.Ascent(_shaper, span.Font) / 3, runWidth, thickness), span.Color));
            }

            x += runWidth;
            k = m;
        }
    }

    /// <summary>Splits a glyph sequence into runs of one typeface (font fallback) and adds them to the scene.</summary>
    public static void EmitGlyphs(List<SceneItem> items, FontSpec font, ushort[] glyphs, int[] faces, float[] pos,
        double x, double baseline, double width, ColorRgba color, RectD? clip)
    {
        int n = glyphs.Length;
        if (n == 0) return;
        bool single = true;
        for (int i = 1; i < n; i++)
            if (faces[i] != faces[0]) { single = false; break; }
        if (single)
        {
            items.Add(new GlyphRunItem(font, faces[0], glyphs, pos, x, baseline, width, color, clip));
            return;
        }
        int s = 0;
        while (s < n)
        {
            int e = s + 1;
            while (e < n && faces[e] == faces[s]) e++;
            items.Add(new GlyphRunItem(font, faces[s], glyphs[s..e], pos[(s * 2)..(e * 2)], x, baseline, width, color, clip));
            s = e;
        }
    }
}

/// <summary>A pragmatic subset of UAX #14 covering Latin prose and CJK.</summary>
internal static class BreakRules
{
    // Written as escapes: these characters are invisible (or look like ASCII) in an editor.
    private const char ZeroWidthSpace = '\u200B', NoBreakSpace = '\u00A0', Hyphen = '\u2010';

    public static bool CanBreakAfter(string s, int i)
    {
        if (i < 0 || i + 1 >= s.Length) return false;
        char c = s[i], next = s[i + 1];
        if (next == '\n' || c == '\n') return false;
        if (c == ' ' || c == '\t') return next != ' ' && next != '\t';
        if (c == ZeroWidthSpace) return true;
        if (next == ' ' || next == '\t' || next == NoBreakSpace || c == NoBreakSpace) return false;
        switch (c)
        {
            case '-':
            case Hyphen:
                return i > 0 && char.IsLetterOrDigit(s[i - 1]) && !char.IsDigit(next) && char.IsLetter(next);
            case '–': // en dash
                return char.IsLetterOrDigit(next);
            case '—': // em dash: break allowed on either side, never between two
                return next != '—' && !IsClosing(next);
            case '/':
                return i > 0 && char.IsLetter(s[i - 1]) && char.IsLetter(next);
        }
        if (next == '—') return char.IsLetterOrDigit(c);
        if (IsCjk(c) || IsCjk(next)) return !IsClosing(next) && !IsOpening(c);
        return false;
    }

    private static bool IsCjk(char c) =>
        c is >= '⺀' and <= '鿿' or >= '豈' and <= '﫿' or >= '＀' and <= '￯' or >= '가' and <= '힯';

    private static bool IsClosing(char c) => ")]}>,.;:!?%、。，．）」』】’”»".Contains(c);

    private static bool IsOpening(char c) => "([{<〈《「『【（‘“«".Contains(c);
}
