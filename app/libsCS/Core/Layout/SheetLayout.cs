using SolumanirSheetDesignerStudio.Core.Model;

namespace SolumanirSheetDesignerStudio.Core.Layout;

public enum TextFieldKind { PageName, Subtitle, SectionName, SlotLabel, LoreTitle, LoreBody }

public readonly record struct TextFieldRef(TextFieldKind Kind, string? Id = null);

/// <summary>Where and how an editable text sits on the sheet, so an in-place editor can cover it exactly.</summary>
public sealed record TextFieldInfo(TextFieldRef Ref, RectD Box, FontSpec Font, TextAlign Align, ColorRgba Color, bool Uppercase, string Text, string Placeholder);

public enum SelectionKind { None, Section, Slot, Part, Lore }

public sealed record Selection(SelectionKind Kind, string? SectionId = null, string? SlotId = null, string? PartId = null, string? LoreId = null)
{
    public static readonly Selection None = new(SelectionKind.None);
}

public enum HitKind { None, Sheet, PageName, Subtitle, Section, SectionName, Slot, SlotLabel, Part, Lore, LoreTitle, LoreBody, AddSlot, AddRow, AddLore }

public readonly record struct Hit(HitKind Kind, string? SectionId = null, string? SlotId = null, string? PartId = null, string? LoreId = null);

public enum AffordanceKind { AddSlot, AddRow, AddLore }

/// <summary>A small on-sheet button shown in edit mode (never exported).</summary>
public sealed record Affordance(AffordanceKind Kind, RectD Rect, string Label, string? SectionId = null);

public sealed record LayoutOptions
{
    /// <summary>Edit mode: placeholders, hints, the empty details row and add-buttons are laid out.</summary>
    public bool EditChrome { get; init; }
    public bool ShowPlaceholders { get; init; } = true;
    /// <summary>Lore section whose body is replaced by the Markdown source editor.</summary>
    public string? EditingLoreId { get; init; }
    public double EditingLoreHeight { get; init; } = 180;
    /// <summary>Text currently covered by an in-place editor (not drawn).</summary>
    public TextFieldRef? HiddenField { get; init; }
    public bool TransparentBackground { get; init; }
}

public sealed class PartLayout
{
    public required Part Part { get; init; }
    /// <summary>The part's content box inside the slot's image area.</summary>
    public required RectD Box { get; init; }
    public ImagePlacement? Image { get; init; }
    /// <summary>The part references an image that is not in the library.</summary>
    public bool MissingImage { get; init; }
}

public sealed class SlotLayout
{
    public required Slot Slot { get; init; }
    public required RectD Box { get; init; }
    /// <summary>Image area including its 1-unit outline.</summary>
    public required RectD Area { get; init; }
    public required RectD LabelBox { get; init; }
    public List<PartLayout> Parts { get; } = new();
}

public sealed class SectionLayout
{
    public required Section Section { get; init; }
    public required bool IsDetails { get; init; }
    public required RectD Box { get; init; }
    public required RectD Banner { get; init; }
    public required RectD NameBox { get; init; }
    public required RectD SlotsArea { get; init; }
    public List<SlotLayout> Slots { get; } = new();
}

public sealed class LoreLayout
{
    public required LoreSection Section { get; init; }
    public required RectD Box { get; init; }
    public required RectD TitleBox { get; init; }
    public required RectD BodyBox { get; init; }
}

/// <summary>Result of laying out one page: geometry for hit-testing plus the display list.</summary>
public sealed class PageLayout
{
    public required Page Page { get; init; }
    public required SheetStyle Style { get; init; }
    public required LayoutOptions Options { get; init; }
    public double Width => SheetLayoutEngine.SheetWidth;
    public required double Height { get; init; }
    public required RectD NameBox { get; init; }
    public required RectD SubtitleBox { get; init; }
    public required RectD LeftColumn { get; init; }
    public required RectD LoreBox { get; init; }
    public List<SectionLayout> Rows { get; } = new();
    public SectionLayout? Details { get; init; }
    public List<LoreLayout> Lore { get; } = new();
    /// <summary>Everything that is exported, back to front.</summary>
    public required IReadOnlyList<SceneItem> Content { get; init; }
    /// <summary>Edit-only decorations (placeholders, hints) drawn above the content.</summary>
    public required IReadOnlyList<SceneItem> Chrome { get; init; }
    public required IReadOnlyList<ImagePlacement> Images { get; init; }
    public required IReadOnlyList<Affordance> Affordances { get; init; }
    public required IReadOnlyDictionary<TextFieldRef, TextFieldInfo> Fields { get; init; }

    public IEnumerable<SectionLayout> AllSections => Details == null ? Rows : Rows.Append(Details);

    public SectionLayout? FindSection(string? id) => id == null ? null : AllSections.FirstOrDefault(s => s.Section.Id == id);

    public (SectionLayout Section, SlotLayout Slot)? FindSlot(string? slotId)
    {
        if (slotId == null) return null;
        foreach (var s in AllSections)
            foreach (var slot in s.Slots)
                if (slot.Slot.Id == slotId) return (s, slot);
        return null;
    }

    public PartLayout? FindPart(string? partId)
    {
        if (partId == null) return null;
        foreach (var s in AllSections)
            foreach (var slot in s.Slots)
                foreach (var part in slot.Parts)
                    if (part.Part.Id == partId) return part;
        return null;
    }

    /// <summary>Rectangle the selection outline is drawn around.</summary>
    public RectD? BoundsOf(Selection sel) => sel.Kind switch
    {
        SelectionKind.Section => FindSection(sel.SectionId)?.Box,
        SelectionKind.Slot => FindSlot(sel.SlotId)?.Slot.Box,
        SelectionKind.Part => FindPart(sel.PartId)?.Box ?? FindSlot(sel.SlotId)?.Slot.Box,
        SelectionKind.Lore => Lore.FirstOrDefault(l => l.Section.Id == sel.LoreId)?.Box.Inflate(4),
        _ => null,
    };

    public Hit HitTest(double x, double y)
    {
        if (x < 0 || y < 0 || x >= Width || y >= Height) return new Hit(HitKind.None);
        if (Options.EditChrome)
            foreach (var a in Affordances)
                if (a.Rect.Contains(x, y))
                    return new Hit(a.Kind switch { AffordanceKind.AddSlot => HitKind.AddSlot, AffordanceKind.AddRow => HitKind.AddRow, _ => HitKind.AddLore }, a.SectionId);

        foreach (var s in AllSections)
        {
            if (!s.Box.Contains(x, y)) continue;
            string id = s.Section.Id;
            foreach (var slot in s.Slots)
            {
                if (!slot.Box.Contains(x, y)) continue;
                if (slot.LabelBox.Contains(x, y)) return new Hit(HitKind.SlotLabel, id, slot.Slot.Id);
                foreach (var part in slot.Parts)
                    if (part.Box.Contains(x, y)) return new Hit(HitKind.Part, id, slot.Slot.Id, part.Part.Id);
                // The outline or a divider: treat as the nearest part.
                var nearest = slot.Parts.OrderBy(p => Math.Abs(p.Box.CenterY - y)).FirstOrDefault();
                return nearest != null ? new Hit(HitKind.Part, id, slot.Slot.Id, nearest.Part.Id) : new Hit(HitKind.Slot, id, slot.Slot.Id);
            }
            if (s.NameBox.Contains(x, y)) return new Hit(HitKind.SectionName, id);
            return new Hit(HitKind.Section, id);
        }

        if (LoreBox.Contains(x, y))
        {
            foreach (var l in Lore)
            {
                if (y < l.Box.Y || y >= l.Box.Bottom) continue;
                if (l.TitleBox.Contains(x, y)) return new Hit(HitKind.LoreTitle, LoreId: l.Section.Id);
                if (y >= l.BodyBox.Y) return new Hit(HitKind.LoreBody, LoreId: l.Section.Id);
                return new Hit(HitKind.Lore, LoreId: l.Section.Id);
            }
            return new Hit(HitKind.Sheet);
        }

        if (NameBox.Contains(x, y)) return new Hit(HitKind.PageName);
        if (SubtitleBox.Contains(x, y)) return new Hit(HitKind.Subtitle);
        return new Hit(HitKind.Sheet);
    }
}

/// <summary>
/// The single layout pipeline for the sheet (handoff section 4). Everything is computed in sheet units at scale 1;
/// the same result feeds the on-screen view and the PNG export, so they cannot disagree.
/// </summary>
public sealed class SheetLayoutEngine
{
    public const double SheetWidth = 1600;

    public static readonly FontSpec NameFont = new(FontFamilyId.Display, 700, false, 48, 48 * 0.07);
    public static readonly FontSpec SubtitleFont = new(FontFamilyId.Body, 400, true, 23);
    public static readonly FontSpec RowNameFont = new(FontFamilyId.Display, 700, false, 19, 19 * 0.14);
    public static readonly FontSpec LabelFont = new(FontFamilyId.Label, 500, false, 15, 15 * 0.14);
    public static readonly FontSpec LoreTitleFont = new(FontFamilyId.Display, 700, false, 21, 21 * 0.12);
    public static readonly FontSpec HintFont = new(FontFamilyId.Body, 400, true, 18);
    public static readonly FontSpec PlaceholderFont = new(FontFamilyId.Mono, 400, false, 12);

    private readonly ITextShaper _shaper;
    private readonly IMarkdownParser _markdown;
    private readonly Func<string, (int Width, int Height)?> _imageSize;

    public SheetLayoutEngine(ITextShaper shaper, IMarkdownParser markdown, Func<string, (int Width, int Height)?> imageSize)
    {
        _shaper = shaper;
        _markdown = markdown;
        _imageSize = imageSize;
    }


    public PageLayout Layout(Page page, SheetStyle projectStyle, LayoutOptions? options = null) =>
        new Builder(this, page, page.Style ?? projectStyle, options ?? new LayoutOptions()).Run();

    /// <summary>Contain-fit of an image inside a box: centred horizontally, bottom-aligned or centred vertically.</summary>
    public static RectD Fit(RectD box, int fileW, int fileH, bool alignBottom)
    {
        if (fileW <= 0 || fileH <= 0 || box.W <= 0 || box.H <= 0) return new RectD(box.CenterX, alignBottom ? box.Bottom : box.CenterY, 0, 0);
        double s = Math.Min(box.W / fileW, box.H / fileH);
        double w = fileW * s, h = fileH * s;
        return new RectD(box.X + (box.W - w) / 2, alignBottom ? box.Bottom - h : box.Y + (box.H - h) / 2, w, h);
    }

    private sealed class Builder
    {
        private readonly SheetLayoutEngine _e;
        private readonly Page _page;
        private readonly SheetStyle _style;
        private readonly LayoutOptions _o;
        private readonly ColorRgba _ink, _paper, _accent;
        private static readonly ColorRgba SectionFill = ColorRgba.FromRgba(255, 255, 255, 0.20);
        private static readonly ColorRgba SlotLine = ColorRgba.FromRgba(42, 33, 26, 0.50);
        private static readonly ColorRgba SlotFill = ColorRgba.FromRgba(60, 40, 20, 0.05);
        private static readonly ColorRgba StripeColor = ColorRgba.FromRgba(42, 33, 26, 0.05);
        private static readonly ColorRgba PlaceholderText = ColorRgba.FromRgba(42, 33, 26, 0.60);

        private readonly List<SceneItem> _background = new();
        private List<SceneItem> _items = new();
        private readonly List<SceneItem> _chrome = new();
        private readonly List<ImagePlacement> _images = new();
        private readonly List<Affordance> _affordances = new();
        private readonly Dictionary<TextFieldRef, TextFieldInfo> _fields = new();

        public Builder(SheetLayoutEngine engine, Page page, SheetStyle style, LayoutOptions options)
        {
            _e = engine;
            _page = page;
            _style = style;
            _o = options;
            _ink = ColorRgba.Parse(Palette.Ink, new ColorRgba(42, 33, 26));
            _paper = ColorRgba.Parse(style.Paper, ColorRgba.Parse(Palette.Parchment, ColorRgba.White));
            _accent = ColorRgba.Parse(style.Accent, ColorRgba.Parse(Palette.Oxblood, _ink));
        }

        public PageLayout Run()
        {
            const double pad = 14;
            double border = _style.PageBorder == BorderKind.Ornate ? 3 : 6;
            var frameOuterX = pad;
            double frameW = SheetWidth - 2 * pad;
            double cx = frameOuterX + border + 22, cw = frameW - 2 * border - 44;
            double y = pad + border + 20;

            // ----- header -----
            var nameBox = new RectD(cx, y, cw, 60);
            TextField(new TextFieldRef(TextFieldKind.PageName), _page.Name, NameFont, nameBox, TextAlign.Center, _accent, false, "Species name");

            double dy = y + 60 + 4, mid = dy + 6.5;
            double rule = Math.Max(0, (cw - 85) / 2);
            Fill(new RectD(cx, dy + 6, rule, 1), _ink);
            _items.Add(new DiamondItem(cx + rule + 14 + 4, mid, 8, _ink));
            _items.Add(new DiamondItem(cx + rule + 14 + 8 + 14 + 6.5, mid, 13, _accent));
            _items.Add(new DiamondItem(cx + rule + 14 + 8 + 14 + 13 + 14 + 4, mid, 8, _ink));
            Fill(new RectD(cx + cw - rule, dy + 6, rule, 1), _ink);

            var subtitleBox = new RectD(cx, dy + 13 + 6, cw, 34);
            TextField(new TextFieldRef(TextFieldKind.Subtitle), _page.Subtitle, SubtitleFont, subtitleBox, TextAlign.Center, _ink, false, "Epithet or one-line description");

            // ----- body: left column -----
            double bodyY = y + 117 + 10;
            double loreW = cw * _style.EffectiveLoreWidth / 100;
            double leftW = cw - 14 - loreW;
            double loreX = cx + leftW + 14;

            var rows = new List<SectionLayout>();
            double ly = bodyY;
            foreach (var row in _page.Rows)
            {
                var sl = LayoutSection(row, cx, ly, leftW, false, _style.RowBorder);
                rows.Add(sl);
                ly = sl.Box.Bottom + 14;
            }
            double rowsBottom = rows.Count > 0 ? rows[^1].Box.Bottom : bodyY;

            // The details row cannot be deleted; when empty it is only shown while editing.
            SectionLayout? details = null;
            if (_page.Details.Slots.Count > 0 || _o.EditChrome)
            {
                details = LayoutSection(_page.Details, cx, ly, leftW, true, _style.DetailsBorder);
                ly = details.Box.Bottom + 14;
            }
            double leftH = Math.Max(0, ly - 14 - bodyY);

            // ----- body: lore column (content first, the box needs the final height) -----
            var leftItems = _items;
            _items = new List<SceneItem>();
            var lore = new List<LoreLayout>();
            double loreNatural = LayoutLore(loreX, bodyY, loreW, lore);
            var loreItems = _items;
            _items = leftItems;

            double bodyH = Math.Max(leftH, loreNatural);
            var loreBox = new RectD(loreX, bodyY, loreW, bodyH);
            BoxDecor(loreBox, _style.LoreBorder);
            _items.AddRange(loreItems);

            // ----- page frame and paper -----
            double frameH = border + 20 + 117 + 10 + bodyH + 22 + border;
            double height = frameH + 2 * pad;
            var sheet = new RectD(0, 0, SheetWidth, height);
            var frame = new RectD(frameOuterX, pad, frameW, frameH);
            if (!_o.TransparentBackground)
            {
                _background.Add(new FillRectItem(sheet, _paper));
                _background.Add(new VignetteItem(sheet, ColorRgba.FromRgba(90, 60, 20, 0.13), 0.55));
            }
            PageFrame(frame);

            // ----- edit affordances -----
            if (_o.EditChrome)
            {
                double ax = cx + leftW / 2 - 55;
                double ay = rows.Count > 0 ? rowsBottom + 7 - 12 : bodyY + 4;
                _affordances.Add(new Affordance(AffordanceKind.AddRow, new RectD(ax, ay, 110, 24), "+ Add row"));

                double lastLore = lore.Count > 0 ? lore[^1].Box.Bottom : bodyY + 13;
                double by = Math.Min(lastLore + 8, loreBox.Bottom - 12);
                _affordances.Add(new Affordance(AffordanceKind.AddLore, new RectD(loreX + loreW / 2 - 65, by, 130, 24), "+ Add section"));
            }

            var content = new List<SceneItem>(_background.Count + _items.Count);
            content.AddRange(_background);
            content.AddRange(_items);

            var result = new PageLayout
            {
                Page = _page, Style = _style, Options = _o, Height = height,
                NameBox = nameBox, SubtitleBox = subtitleBox,
                LeftColumn = new RectD(cx, bodyY, leftW, bodyH), LoreBox = loreBox,
                Details = details,
                Content = content, Chrome = _chrome, Images = _images, Affordances = _affordances, Fields = _fields,
            };
            result.Rows.AddRange(rows);
            result.Lore.AddRange(lore);
            return result;
        }

        // ---------- decorations ----------

        private void Fill(RectD r, ColorRgba c) => _items.Add(new FillRectItem(r, c));

        private void PageFrame(RectD frame)
        {
            switch (_style.PageBorder)
            {
                case BorderKind.Plain: // 6-unit "double": two 2-unit lines, 2 apart
                    _background.Add(new FrameItem(frame, 2, _ink));
                    _background.Add(new FrameItem(frame.Inset(4), 2, _ink));
                    break;
                case BorderKind.Ornate:
                    _background.Add(new FrameItem(frame, 3, _ink));
                    _background.Add(new FrameItem(frame.Inset(3), 3, _paper));
                    _background.Add(new FrameItem(frame.Inset(6), 1, _ink));
                    _background.Add(new FrameItem(frame.Inset(7), 3, _paper));
                    _background.Add(new FrameItem(frame.Inset(10), 1, _accent));
                    foreach (var (cxp, cyp) in new[] { (frame.X, frame.Y), (frame.Right - 24, frame.Y), (frame.X, frame.Bottom - 24), (frame.Right - 24, frame.Bottom - 24) })
                    {
                        _background.Add(new FillRectItem(new RectD(cxp, cyp, 24, 24), _ink));
                        _background.Add(new DiamondItem(cxp + 12, cyp + 12, 9, _paper));
                    }
                    break;
            }
        }

        /// <summary>Border and fill of a row / details / lore box.</summary>
        private void BoxDecor(RectD box, BorderKind kind)
        {
            if (kind == BorderKind.None) return;
            Fill(box, SectionFill);
            if (kind == BorderKind.Ornate)
            {
                _items.Add(new FrameItem(box.Inset(1), 2, _paper));
                _items.Add(new FrameItem(box.Inset(3), 1, _accent));
            }
            _items.Add(new FrameItem(box, 1, _ink));
            if (kind == BorderKind.Ornate)
            {
                _items.Add(new DiamondItem(box.X + 0.5, box.Y + 0.5, 9, _ink));
                _items.Add(new DiamondItem(box.Right - 0.5, box.Y + 0.5, 9, _ink));
                _items.Add(new DiamondItem(box.X + 0.5, box.Bottom - 0.5, 9, _ink));
                _items.Add(new DiamondItem(box.Right - 0.5, box.Bottom - 0.5, 9, _ink));
            }
        }

        // ---------- text ----------

        private void TextField(TextFieldRef field, string text, FontSpec font, RectD box, TextAlign align, ColorRgba color, bool upper, string placeholder)
        {
            _fields[field] = new TextFieldInfo(field, box, font, align, color, upper, text, placeholder);
            if (_o.HiddenField is { } hidden && hidden == field) return;
            if (text.Length > 0) SingleLine(_items, upper ? TextCase.Upper(text) : text, font, box, align, color, clip: true);
            else if (_o.EditChrome) SingleLine(_chrome, upper ? TextCase.Upper(placeholder) : placeholder, font, box, align, color.WithAlpha(0.38 * color.A / 255.0), clip: true);
        }

        /// <summary>One line vertically centred in <paramref name="box"/> (a CSS line box of that height). Returns the text width.</summary>
        private double SingleLine(List<SceneItem> target, string text, FontSpec font, RectD box, TextAlign align, ColorRgba color, bool clip)
        {
            var shaped = _e._shaper.Shape(text, font);
            int n = shaped.Count;
            if (n == 0) return 0;
            var pos = new float[n * 2];
            double x = 0;
            for (int i = 0; i < n; i++)
            {
                pos[i * 2] = (float)(x + shaped.OffsetsX[i]);
                pos[i * 2 + 1] = -shaped.OffsetsY[i];
                x += shaped.Advances[i];
                if (i == n - 1 || shaped.Clusters[i + 1] != shaped.Clusters[i]) x += font.LetterSpacing; // CSS adds it after every cluster, the last included
            }
            double width = x;
            double ox = width >= box.W ? box.X : align switch
            {
                TextAlign.Center => box.X + (box.W - width) / 2,
                TextAlign.Right => box.Right - width,
                _ => box.X,
            };
            double baseline = box.Y + TextMetrics.Box(_e._shaper, font, box.H).Baseline;
            InlineBox.EmitGlyphs(target, font, shaped.Glyphs, shaped.Faces, pos, ox, baseline, width, color, clip ? box : null);
            return width;
        }

        // ---------- rows ----------

        private struct PartMeasure
        {
            public (int Width, int Height)? Size;
            public double Scale;      // 0.05 – 1 (box scale)
            public double BoxW, BoxH; // image box (Auto mode)
            public double Natural;    // natural part height incl. its divider (Auto mode)
        }

        private SectionLayout LayoutSection(Section s, double x, double y, double w, bool isDetails, BorderKind kind)
        {
            bool ornate = kind == BorderKind.Ornate;
            double cx = x + 11, cy = y + 10, cw = w - 22;
            double bannerH = ornate ? 34 : 37;
            double slotsY = cy + bannerH + 8;
            var cfg = s.Sizing;
            bool auto = cfg.Mode == SizingMode.Auto;
            int n = s.Slots.Count;
            // The gap shrinks only when a (hand-edited) row has more slots than fit, so a slot never gets a negative width.
            double slotGap = n > 1 ? Math.Min(8, cw / (2.0 * n)) : 0;
            double slotW = n > 0 ? (cw - slotGap * (n - 1)) / n : 0;
            double innerW = Math.Max(0, slotW - 2);

            // ----- measure -----
            var measures = new PartMeasure[n][];
            double areaH = auto ? 90 : Math.Max(90, cfg.EffectiveHeight);
            for (int j = 0; j < n; j++)
            {
                var parts = s.Slots[j].Parts;
                var pm = measures[j] = new PartMeasure[parts.Count];
                double sum = 0;
                for (int q = 0; q < parts.Count; q++)
                {
                    var part = parts[q];
                    pm[q].Size = part.ImageId != null ? _e._imageSize(part.ImageId) : null;
                    pm[q].Scale = Math.Min(100, part.Weight) / 100;
                    if (!auto) continue;
                    double natural = 60; // empty parts take a minimum of 60
                    if (pm[q].Size is { Width: > 0, Height: > 0 } size)
                    {
                        // The row hugs its images: slot width × scale at the natural aspect ratio, optionally capped.
                        double bw = innerW * pm[q].Scale;
                        double bh = bw * size.Height / size.Width;
                        if (cfg.EffectiveMaxHeight > 0) bh = Math.Min(bh, Math.Floor(cfg.EffectiveMaxHeight * pm[q].Scale / parts.Count + 0.5));
                        pm[q].BoxW = bw;
                        pm[q].BoxH = bh;
                        natural = Math.Max(60, bh + (q > 0 ? 1 : 0));
                    }
                    pm[q].Natural = natural;
                    sum += natural;
                }
                if (auto) areaH = Math.Max(areaH, sum + 2);
            }

            bool emptyEdit = n == 0 && _o.EditChrome;
            double slotsH = n > 0 ? areaH + 28 : emptyEdit ? 90 : 0;
            var box = new RectD(x, y, w, 10 + bannerH + 8 + slotsH + 7);
            BoxDecor(box, kind);

            // ----- banner -----
            var banner = new RectD(cx, cy, cw, bannerH);
            ColorRgba fg;
            double textLeft;
            if (ornate)
            {
                Fill(banner, _accent);
                _items.Add(new FrameItem(banner.Inset(1), 1, _paper));
                fg = _paper;
                textLeft = cx + 10;
            }
            else
            {
                Fill(new RectD(cx, cy + 36, cw, 1), _ink);
                fg = _accent;
                textLeft = cx;
            }
            _items.Add(new DiamondItem(textLeft + 4.5, cy + 17, 9, fg));
            double nameRight = cx + cw - (ornate ? 10 : 0);
            var nameBox = new RectD(textLeft + 19, cy, Math.Max(0, nameRight - (textLeft + 19)), 34);
            TextField(new TextFieldRef(TextFieldKind.SectionName, s.Id), s.Name, RowNameFont, nameBox, TextAlign.Left, fg, true, isDetails ? "Details title" : "Row name");

            var layout = new SectionLayout
            {
                Section = s, IsDetails = isDetails, Box = box, Banner = banner, NameBox = nameBox,
                SlotsArea = new RectD(cx, slotsY, cw, slotsH),
            };

            if (_o.EditChrome)
                _affordances.Add(new Affordance(AffordanceKind.AddSlot, new RectD(nameRight - 30, cy + 5, 28, 24), "+", s.Id));

            if (emptyEdit && _o.ShowPlaceholders)
            {
                var empty = new RectD(cx, slotsY, cw, 90);
                _chrome.Add(new StripesItem(empty, StripeColor, 10));
                CenteredLines(empty, isDetails ? "no details yet" : "no slots yet", "drop images here or press +");
            }

            // ----- slots -----
            bool alignBottom = !isDetails;
            double exportPx = cfg.EffectiveExportPx;
            for (int j = 0; j < n; j++)
            {
                var slot = s.Slots[j];
                var pm = measures[j];
                int np = slot.Parts.Count;
                double sx = cx + j * (slotW + slotGap);
                var area = new RectD(sx, slotsY, slotW, areaH);
                if (_style.SlotFrames)
                {
                    Fill(area, SlotFill);
                    _items.Add(new FrameItem(area, 1, SlotLine));
                }
                var inner = area.Inset(1);
                var labelBox = new RectD(sx, slotsY + areaH + 2, slotW, 26);
                var slotLayout = new SlotLayout { Slot = slot, Box = new RectD(sx, slotsY, slotW, areaH + 28), Area = area, LabelBox = labelBox };

                bool splitFixed = np > 1 && !auto;
                double weightSum = 0, naturalSum = 0;
                for (int q = 0; q < np; q++) { weightSum += slot.Parts[q].Weight; naturalSum += pm[q].Natural; }
                double py = inner.Y;
                for (int q = 0; q < np; q++)
                {
                    var part = slot.Parts[q];
                    double divider = q > 0 ? 1 : 0;
                    double partH; // content height below the divider
                    if (auto) partH = pm[q].Natural + (inner.H - naturalSum) / np - divider;
                    else partH = np == 1 ? inner.H : (inner.H - (np - 1)) * part.Weight / weightSum;
                    partH = Math.Max(0, partH);

                    if (q > 0)
                    {
                        if (_style.SlotFrames) _items.Add(new DashedLineItem(inner.X, inner.Right, py, 1, SlotLine));
                        py += 1;
                    }
                    var partBox = new RectD(inner.X, py, inner.W, partH);

                    ImagePlacement? placement = null;
                    if (pm[q].Size is { Width: > 0, Height: > 0 } size)
                    {
                        RectD imgBox;
                        if (auto)
                        {
                            imgBox = Anchor(partBox, pm[q].BoxW, pm[q].BoxH, alignBottom);
                        }
                        else if (splitFixed)
                        {
                            imgBox = partBox; // the image fills its part; scale is the part's weight
                        }
                        else
                        {
                            imgBox = Anchor(partBox, partBox.W * pm[q].Scale, partBox.H * pm[q].Scale, alignBottom);
                        }

                        double partExport = exportPx <= 0 ? 0 : exportPx * (splitFixed ? part.Weight / weightSum : 1.0 / np);
                        placement = new ImagePlacement
                        {
                            ImageId = part.ImageId!, SectionId = s.Id, SlotId = slot.Id, PartId = part.Id,
                            Container = imgBox, Drawn = Fit(imgBox, size.Width, size.Height, alignBottom), AlignBottom = alignBottom,
                            FileWidth = size.Width, FileHeight = size.Height, ExportPx = partExport,
                        };
                        _images.Add(placement);
                        _items.Add(new ImageItem(placement));
                    }
                    else if (_o.EditChrome && _o.ShowPlaceholders)
                    {
                        _chrome.Add(new StripesItem(partBox, StripeColor, 10));
                        if (part.ImageId != null) CenteredLines(partBox, "image missing", "click to replace");
                        else CenteredLines(partBox, "drop image", "or click");
                    }

                    slotLayout.Parts.Add(new PartLayout { Part = part, Box = partBox, Image = placement, MissingImage = part.ImageId != null && placement == null });
                    py += partH;
                }

                TextField(new TextFieldRef(TextFieldKind.SlotLabel, slot.Id), slot.Label, LabelFont, labelBox, TextAlign.Center, _ink, true, "Label");
                layout.Slots.Add(slotLayout);
            }
            return layout;
        }

        private static RectD Anchor(RectD box, double w, double h, bool bottom) =>
            new(box.X + (box.W - w) / 2, bottom ? box.Bottom - h : box.Y + (box.H - h) / 2, w, h);

        private void CenteredLines(RectD box, string line1, string line2)
        {
            var font = PlaceholderFont;
            double lineH = TextMetrics.Ascent(_e._shaper, font) + TextMetrics.Descent(_e._shaper, font);
            if (box.H < 2 * lineH + 4 || box.W < 40) return;
            double top = box.CenterY - lineH;
            SingleLine(_chrome, line1, font, new RectD(box.X, top, box.W, lineH), TextAlign.Center, PlaceholderText, clip: true);
            SingleLine(_chrome, line2, font, new RectD(box.X, top + lineH, box.W, lineH), TextAlign.Center, PlaceholderText, clip: true);
        }

        // ---------- lore ----------

        /// <summary>Lays out the lore sections and returns the natural height of the lore box.</summary>
        private double LayoutLore(double x, double y, double w, List<LoreLayout> result)
        {
            double cx = x + 17, cw = Math.Max(40, w - 34);
            double cy = y + 13;
            var md = new MarkdownLayouter(_e._shaper, _ink, _accent, _items);
            var ctx = new MarkdownLayouter.Ctx(cx, cw);

            for (int i = 0; i < _page.Lore.Count; i++)
            {
                var sec = _page.Lore[i];
                if (i > 0) cy += 14;
                double top = cy;
                var titleBox = new RectD(cx, cy, cw, 34);
                TextField(new TextFieldRef(TextFieldKind.LoreTitle, sec.Id), sec.Title, LoreTitleFont, titleBox, TextAlign.Left, _accent, true, "Section title");
                Fill(new RectD(cx, cy + 38, cw, 1), _ink);
                double barBottom = cy + 39;

                RectD bodyBox;
                double end;
                if (_o.EditChrome && _o.EditingLoreId == sec.Id)
                {
                    double editorH = Math.Max(180, _o.EditingLoreHeight);
                    bodyBox = new RectD(cx, barBottom + 10, cw, editorH);
                    end = bodyBox.Bottom;
                }
                else
                {
                    var blocks = string.IsNullOrWhiteSpace(sec.Body) ? Array.Empty<MdBlock>() : _e._markdown.Parse(sec.Body);
                    if (blocks.Count == 0)
                    {
                        bodyBox = new RectD(cx, barBottom + 10, cw, 28);
                        end = bodyBox.Bottom;
                        if (_o.EditChrome)
                            SingleLine(_chrome, "Click to write — markdown supported.", HintFont, new RectD(cx, bodyBox.Y, cw, 25), TextAlign.Left, ColorRgba.FromRgba(42, 33, 26, 0.55), clip: true);
                    }
                    else
                    {
                        // The title bar's 10-unit bottom margin collapses with the first block's top margin.
                        double start = barBottom + Math.Max(10, md.Margins(blocks[0], ctx).Top);
                        end = md.LayoutBlocks(blocks, ctx, start, suppressFirstMargin: true, suppressLastMargin: false);
                        end = Math.Max(end, start + 28);
                        bodyBox = new RectD(cx, start, cw, end - start);
                    }
                }

                _fields[new TextFieldRef(TextFieldKind.LoreBody, sec.Id)] =
                    new TextFieldInfo(new TextFieldRef(TextFieldKind.LoreBody, sec.Id), bodyBox, PlaceholderFont with { Size = 17 }, TextAlign.Left, _ink, false, sec.Body, "Markdown…");
                result.Add(new LoreLayout { Section = sec, Box = new RectD(cx, top, cw, end - top), TitleBox = titleBox, BodyBox = bodyBox });
                cy = end;
            }
            return cy + 15 - y; // 14 bottom padding + 1 border
        }
    }
}
