using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Rendering;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using CommunityToolkit.Mvvm.Input;
using SkiaSharp;
using SolumanirSheetDesignerStudio.App.Services;
using SolumanirSheetDesignerStudio.App.ViewModels;
using SolumanirSheetDesignerStudio.Core.Layout;
using SolumanirSheetDesignerStudio.Core.Model;
using SolumanirSheetDesignerStudio.Rendering;

namespace SolumanirSheetDesignerStudio.App.Controls;

/// <summary>
/// The sheet on its dark desk. Draws the page with the shared Skia painter (so the screen matches the export),
/// and turns pointer input into selection, in-place editing, drag-and-drop reordering and drops of image files and
/// library images. (Resizing by dragging is in SheetCanvas.Resize.cs.)
/// </summary>
public sealed partial class SheetCanvas : Control, ICustomHitTest
{
    /// <summary>Desk padding around the sheet, in screen units.</summary>
    public const double DeskMargin = 28;

    public static readonly StyledProperty<EditorViewModel?> EditorProperty =
        AvaloniaProperty.Register<SheetCanvas, EditorViewModel?>(nameof(Editor));

    private static readonly ColorRgba Gold = new(0xC9, 0xA1, 0x54);
    private static readonly ColorRgba Ink = new(0x2A, 0x21, 0x1A);
    private static readonly Cursor HandCursor = new(StandardCursorType.Hand);
    private static readonly Cursor TextCursor = new(StandardCursorType.Ibeam);
    private static readonly Cursor MoveCursor = new(StandardCursorType.SizeAll);

    private enum DragKind { None, Slot, Part, Row, Lore, Image }

    private sealed class Press
    {
        public Hit Hit;
        public Point Start;
        public int Clicks;
        public DragKind Kind;
        public bool Active;
        public string? TargetSectionId;
        public int InsertIndex = -1;
        public RectD? Indicator;
        /// <summary>Image drags (Alt or Ctrl held): the part the image would go to, and whether it is copied there.</summary>
        public string? TargetPartId;
        public bool Copy;
        public RectD? Highlight;
    }

    private EditorViewModel? _editor;
    private Press? _press;
    private Hit _hover;
    private RectD? _hoverRect;
    private RectD? _dropHighlight, _dropBar;

    static SheetCanvas()
    {
        FocusableProperty.OverrideDefaultValue<SheetCanvas>(true);
        ClipToBoundsProperty.OverrideDefaultValue<SheetCanvas>(true);
    }

    public SheetCanvas()
    {
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, (_, _) => SetDropFeedback(null, null));
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    public EditorViewModel? Editor
    {
        get => GetValue(EditorProperty);
        set => SetValue(EditorProperty, value);
    }

    public bool HitTest(Point point) => true;

    // ---------- wiring ----------

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == EditorProperty) Attach(change.GetNewValue<EditorViewModel?>());
    }

    private void Attach(EditorViewModel? editor)
    {
        if (_editor != null)
        {
            _editor.PropertyChanged -= OnEditorChanged;
            _editor.RepaintRequested -= InvalidateVisual;
        }
        _editor = editor;
        _press = null;
        _hover = default;
        _hoverRect = null;
        if (_editor != null)
        {
            _editor.PropertyChanged += OnEditorChanged;
            _editor.RepaintRequested += InvalidateVisual;
        }
        InvalidateMeasure();
        InvalidateVisual();
    }

    private void OnEditorChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(EditorViewModel.Layout):
            case nameof(EditorViewModel.Zoom):
                _hoverRect = null;
                _hover = default; // recomputed on the next pointer move (otherwise "same element as before" would skip it)
                InvalidateMeasure();
                InvalidateVisual();
                break;
            case nameof(EditorViewModel.Selection):
            case nameof(EditorViewModel.IsPreview):
            case nameof(EditorViewModel.EditingField):
                InvalidateVisual();
                break;
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (_editor == null) return default;
        var layout = _editor.Layout;
        return new Size(layout.Width * _editor.Zoom + 2 * DeskMargin, layout.Height * _editor.Zoom + 2 * DeskMargin);
    }

    private Point ToSheet(Point p) => _editor == null ? p : new Point((p.X - DeskMargin) / _editor.Zoom, (p.Y - DeskMargin) / _editor.Zoom);

    // ---------- rendering ----------

    public override void Render(DrawingContext context)
    {
        if (_editor == null) return;
        context.Custom(new DrawOperation(new Rect(Bounds.Size), _editor.Render.Painter, _editor.Layout, _editor.Zoom,
            !_editor.IsPreview, BuildOverlay(_editor), new PreviewImagePainter(_editor.Previews)));
    }

    /// <summary>Edit-only decorations in sheet units: hover, selection outline, add-buttons, drag indicators.</summary>
    private List<SceneItem> BuildOverlay(EditorViewModel editor)
    {
        var items = new List<SceneItem>();
        if (editor.IsPreview) return items;
        var layout = editor.Layout;

        if (_hoverRect is { } hover && _press is not { Active: true })
            items.Add(new FrameItem(hover, 1, Gold.WithAlpha(0.6)));

        if (layout.BoundsOf(editor.Selection) is { } selected)
            items.Add(new FrameItem(selected.Inflate(2), 2, Gold));

        foreach (var a in layout.Affordances)
        {
            bool hot = _hover.Kind == KindOf(a.Kind) && _hover.SectionId == a.SectionId;
            AddChip(editor, items, a.Rect, a.Label, hot);
        }

        if (_press is { Active: true, Indicator: { } indicator }) items.Add(new FillRectItem(indicator, Gold));
        if (_press is { Active: true, Highlight: { } target }) items.Add(new FrameItem(target, 3, Gold));
        if (_dropHighlight is { } drop) items.Add(new FrameItem(drop, 3, Gold));
        if (_dropBar is { } bar) items.Add(new FillRectItem(bar, Gold));

        // Resize handles: the corner of the selected image is always shown, edges only under the pointer.
        if (_resize == null && _press == null)
        {
            if (ScaleHandleOf(editor) is { } corner)
            {
                items.Add(new FillRectItem(corner.Mark, ColorRgba.FromRgba(245, 238, 222, 1)));
                items.Add(new FrameItem(corner.Mark, 1.5, Gold));
            }
            if (_handleHover.Kind is HandleKind.RowHeight or HandleKind.LoreWidth or HandleKind.SplitDivider)
                items.Add(new FillRectItem(_handleHover.Mark, Gold));
        }
        return items;
    }

    private static HitKind KindOf(AffordanceKind kind) => kind switch
    {
        AffordanceKind.AddSlot => HitKind.AddSlot,
        AffordanceKind.AddRow => HitKind.AddRow,
        _ => HitKind.AddLore,
    };

    /// <summary>On-sheet control: parchment fill, thin ink border, ink text; 55 % opacity until hovered (handoff section 9).</summary>
    private static void AddChip(EditorViewModel editor, List<SceneItem> items, RectD rect, string label, bool hot)
    {
        double opacity = hot ? 1 : 0.55;
        items.Add(new FillRectItem(rect, ColorRgba.FromRgba(245, 238, 222, 0.92 * opacity)));
        items.Add(new FrameItem(rect, 1, ColorRgba.FromRgba(42, 33, 26, 0.4 * opacity)));

        var font = new FontSpec(FontFamilyId.Label, 500, false, label.Length == 1 ? 19 : 14);
        var shaper = editor.Render.Fonts;
        var shaped = shaper.Shape(label, font);
        if (shaped.Count == 0) return;
        var positions = new float[shaped.Count * 2];
        double width = 0;
        for (int i = 0; i < shaped.Count; i++)
        {
            positions[i * 2] = (float)(width + shaped.OffsetsX[i]);
            positions[i * 2 + 1] = -shaped.OffsetsY[i];
            width += shaped.Advances[i];
        }
        var metrics = shaper.GetMetrics(font);
        double ascent = Math.Floor(metrics.Ascent + 0.5), descent = Math.Floor(metrics.Descent + 0.5);
        double baseline = rect.Y + Math.Floor((rect.H - (ascent + descent)) / 2) + ascent;
        // The chip is short text in one face; fallback faces are not expected here.
        items.Add(new GlyphRunItem(font, shaped.Faces[0], shaped.Glyphs, positions, rect.X + (rect.W - width) / 2, baseline, width, Ink.WithAlpha(opacity), rect));
    }

    private sealed class DrawOperation : ICustomDrawOperation
    {
        private readonly ScenePainter _painter;
        private readonly PageLayout _layout;
        private readonly double _zoom;
        private readonly bool _edit;
        private readonly List<SceneItem> _overlay;
        private readonly IImagePainter _images;

        public DrawOperation(Rect bounds, ScenePainter painter, PageLayout layout, double zoom, bool edit, List<SceneItem> overlay, IImagePainter images)
        {
            Bounds = bounds;
            _painter = painter;
            _layout = layout;
            _zoom = zoom;
            _edit = edit;
            _overlay = overlay;
            _images = images;
        }

        public Rect Bounds { get; }
        public void Dispose() { }
        public bool Equals(ICustomDrawOperation? other) => false;
        public bool HitTest(Point p) => true;

        public void Render(ImmediateDrawingContext context)
        {
            var feature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
            if (feature == null) return;
            using var lease = feature.Lease();
            var canvas = lease.SkCanvas;
            canvas.Save();
            try
            {
                float zoom = (float)_zoom, margin = (float)DeskMargin;
                float w = (float)(_layout.Width * _zoom), h = (float)(_layout.Height * _zoom);

                // Drop shadow 0 10 40 rgba(0,0,0,.5); it scales with the zoom like the sheet does.
                using (var blur = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, Math.Max(0.5f, 20 * zoom)))
                using (var shadow = new SKPaint { Color = new SKColor(0, 0, 0, 128), IsAntialias = true, MaskFilter = blur })
                    canvas.DrawRect(SKRect.Create(margin, margin + 10 * zoom, w, h), shadow);

                canvas.Translate(margin, margin);
                canvas.Scale(zoom);
                _painter.Paint(canvas, _layout.Content, _images);
                if (_edit) _painter.Paint(canvas, _layout.Chrome, null);
                if (_overlay.Count > 0) _painter.Paint(canvas, _overlay, null);
            }
            catch (Exception)
            {
                // A failed frame must never take the app down; the next invalidation repaints.
            }
            finally
            {
                canvas.Restore();
            }
        }
    }

    // ---------- pointer: hover, click, drag ----------

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_editor is not { } editor) return;
        var point = e.GetCurrentPoint(this);
        var pos = ToSheet(point.Position);
        // Hit-test what was on screen when the button went down. Taking the focus ends an in-place edit, which
        // collapses the Markdown editor and moves everything below it.
        var hit = editor.Layout.HitTest(pos.X, pos.Y);
        Focus();
        if (editor.IsPreview) return;

        if (point.Properties.IsRightButtonPressed)
        {
            SelectFor(editor, hit);
            ShowContextMenu(editor, hit);
            e.Handled = true;
            return;
        }
        if (!point.Properties.IsLeftButtonPressed) return;
        if (TryBeginResize(editor, pos, e)) return;

        // Alt (or Ctrl, to copy) turns a drag of a placed image into "put this image there" instead of reordering slots.
        bool alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt), copy = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        bool imageDrag = (alt || copy) && hit.Kind == HitKind.Part && editor.Layout.FindPart(hit.PartId)?.Image != null;
        _press = new Press
        {
            Hit = hit, Start = point.Position, Clicks = e.ClickCount,
            Kind = imageDrag ? DragKind.Image : DragKindFor(editor, hit, pos),
            Copy = imageDrag && copy && !alt,
        };
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_editor is not { } editor || editor.IsPreview) { Cursor = Cursor.Default; return; }
        var screen = e.GetPosition(this);
        var pos = ToSheet(screen);

        if (_resize is { } resize)
        {
            UpdateResize(editor, resize, pos);
            return;
        }

        if (_press is { } press)
        {
            if (!press.Active && press.Kind != DragKind.None &&
                (Math.Abs(screen.X - press.Start.X) > 6 || Math.Abs(screen.Y - press.Start.Y) > 6))
            {
                press.Active = true;
                Cursor = MoveCursor;
            }
            if (press.Active)
            {
                UpdateDrag(editor, press, pos);
                InvalidateVisual();
            }
            return;
        }

        // Resize handles sit on top of what they resize.
        var handle = FindHandle(editor, pos);
        if (handle.Kind != HandleKind.None)
        {
            if (handle != _handleHover)
            {
                _handleHover = handle;
                _hover = default;
                _hoverRect = null;
                Cursor = CursorFor(handle);
                editor.Hint = HintFor(handle);
                InvalidateVisual();
            }
            return;
        }
        bool leftHandle = _handleHover.Kind != HandleKind.None;
        _handleHover = default;

        var hit = editor.Layout.HitTest(pos.X, pos.Y);
        if (hit == _hover && !leftHandle) return;
        _hover = hit;
        editor.Hint = hit.Kind switch
        {
            HitKind.Part when editor.Layout.FindPart(hit.PartId)?.Image != null =>
                Loc.T("Alt+wheel: size · Alt+drag: swap with another slot · Ctrl+drag: copy there · drag: reorder"),
            HitKind.Part => Loc.T("Click or drop an image here"),
            HitKind.Slot or HitKind.SlotLabel => Loc.T("Drag to reorder"),
            HitKind.Section or HitKind.SectionName => Loc.T("Alt+wheel: row height · drop images to add slots"),
            _ => "",
        };
        var layout = editor.Layout;
        _hoverRect = hit.Kind switch
        {
            HitKind.Part => layout.FindPart(hit.PartId)?.Box,
            HitKind.SlotLabel => layout.FindSlot(hit.SlotId)?.Slot.LabelBox,
            HitKind.SectionName => layout.FindSection(hit.SectionId)?.NameBox,
            HitKind.LoreTitle => layout.Lore.FirstOrDefault(l => l.Section.Id == hit.LoreId)?.TitleBox,
            HitKind.LoreBody => layout.Lore.FirstOrDefault(l => l.Section.Id == hit.LoreId)?.BodyBox,
            HitKind.PageName => layout.NameBox,
            HitKind.Subtitle => layout.SubtitleBox,
            _ => null,
        };
        Cursor = hit.Kind switch
        {
            HitKind.Part or HitKind.AddSlot or HitKind.AddRow or HitKind.AddLore => HandCursor,
            HitKind.SlotLabel or HitKind.SectionName or HitKind.LoreTitle or HitKind.LoreBody or HitKind.PageName or HitKind.Subtitle => TextCursor,
            _ => Cursor.Default,
        };
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_press != null || _resize != null) return;
        _hover = default;
        _handleHover = default;
        _hoverRect = null;
        if (_editor != null) _editor.Hint = "";
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_resize != null)
        {
            _resize = null;
            _handleHover = default; // its highlight was placed for the old layout; the next pointer move finds it again
            _editor?.Session.BreakCoalescing(); // the next drag is a new undo step
            e.Pointer.Capture(null);
            Cursor = Cursor.Default;
            InvalidateVisual();
            e.Handled = true;
            return;
        }
        if (_press is not { } press) return;
        _press = null;
        e.Pointer.Capture(null);
        Cursor = Cursor.Default;
        if (_editor is not { } editor) return;

        if (press.Active)
        {
            CommitDrag(editor, press);
            InvalidateVisual();
        }
        else if (e.InitialPressMouseButton == MouseButton.Left)
        {
            Click(editor, press.Hit, press.Clicks);
        }
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (_press == null && _resize == null) return;
        _press = null;
        _resize = null;
        InvalidateVisual();
    }

    private static void SelectFor(EditorViewModel editor, Hit hit)
    {
        switch (hit.Kind)
        {
            case HitKind.Section or HitKind.SectionName or HitKind.AddSlot when hit.SectionId != null:
                editor.SelectSection(hit.SectionId);
                break;
            case HitKind.Slot or HitKind.SlotLabel when hit.SlotId != null:
                editor.SelectSlot(hit.SlotId);
                break;
            case HitKind.Part when hit.PartId != null:
                editor.SelectPart(hit.PartId);
                break;
            case HitKind.Lore or HitKind.LoreTitle or HitKind.LoreBody when hit.LoreId != null:
                editor.SelectLore(hit.LoreId);
                break;
            default:
                editor.Selection = Selection.None;
                break;
        }
    }

    private void Click(EditorViewModel editor, Hit hit, int clicks)
    {
        switch (hit.Kind)
        {
            case HitKind.AddSlot when hit.SectionId != null:
                editor.AddSlotTo(hit.SectionId);
                break;
            case HitKind.AddRow:
                editor.AddRowCommand.Execute(null);
                break;
            case HitKind.AddLore:
                editor.AddLoreCommand.Execute(null);
                break;
            case HitKind.PageName:
                editor.Selection = Selection.None;
                editor.BeginEdit(new TextFieldRef(TextFieldKind.PageName));
                break;
            case HitKind.Subtitle:
                editor.Selection = Selection.None;
                editor.BeginEdit(new TextFieldRef(TextFieldKind.Subtitle));
                break;
            case HitKind.SectionName:
                SelectFor(editor, hit);
                editor.BeginEdit(new TextFieldRef(TextFieldKind.SectionName, hit.SectionId));
                break;
            case HitKind.SlotLabel:
                SelectFor(editor, hit);
                editor.BeginEdit(new TextFieldRef(TextFieldKind.SlotLabel, hit.SlotId));
                break;
            case HitKind.LoreTitle:
                SelectFor(editor, hit);
                editor.BeginEdit(new TextFieldRef(TextFieldKind.LoreTitle, hit.LoreId));
                break;
            case HitKind.LoreBody:
                SelectFor(editor, hit);
                editor.BeginEdit(new TextFieldRef(TextFieldKind.LoreBody, hit.LoreId));
                break;
            case HitKind.Part when hit.PartId != null:
                SelectFor(editor, hit);
                // An empty part browses on a single click; a filled one is selected first and replaced on a double click.
                if (editor.Layout.FindPart(hit.PartId)?.Image == null || clicks >= 2) Run(editor.BrowseImageAsync(hit.PartId));
                break;
            default:
                SelectFor(editor, hit);
                break;
        }
    }

    private static async void Run(Task task)
    {
        try { await task; }
        catch (Exception) { /* the view model reports its own errors */ }
    }

    // ---------- drag-and-drop reordering ----------

    private static DragKind DragKindFor(EditorViewModel editor, Hit hit, Point pos)
    {
        var layout = editor.Layout;
        switch (hit.Kind)
        {
            case HitKind.Part:
                return layout.FindSlot(hit.SlotId) is { } s && s.Slot.Parts.Count > 1 ? DragKind.Part : DragKind.Slot;
            case HitKind.Slot or HitKind.SlotLabel:
                return DragKind.Slot;
            case HitKind.Section or HitKind.SectionName:
                var section = layout.FindSection(hit.SectionId);
                return section is { IsDetails: false } && section.Banner.Contains(pos.X, pos.Y) ? DragKind.Row : DragKind.None;
            case HitKind.Lore or HitKind.LoreTitle:
                return DragKind.Lore;
            default:
                return DragKind.None;
        }
    }

    private static void UpdateDrag(EditorViewModel editor, Press press, Point pos)
    {
        var layout = editor.Layout;
        const double bar = 3;
        switch (press.Kind)
        {
            case DragKind.Part:
            {
                var found = layout.FindSlot(press.Hit.SlotId);
                if (found == null) break;
                var slot = found.Value.Slot;
                if (!slot.Box.Inflate(30).Contains(pos.X, pos.Y))
                {
                    press.Kind = DragKind.Slot; // dragged well outside: move the whole slot instead
                    goto case DragKind.Slot;
                }
                int index = slot.Parts.Count(p => p.Box.CenterY < pos.Y);
                double y = index == 0 ? slot.Parts[0].Box.Y : index >= slot.Parts.Count ? slot.Parts[^1].Box.Bottom - bar : slot.Parts[index].Box.Y - 2;
                press.InsertIndex = index;
                press.Indicator = new RectD(slot.Area.X + 1, y, slot.Area.W - 2, bar);
                break;
            }
            case DragKind.Slot:
            {
                if (SlotInsertion(layout, pos) is not { } at) break;
                press.TargetSectionId = at.SectionId;
                press.InsertIndex = at.Index;
                press.Indicator = at.Bar;
                break;
            }
            case DragKind.Image:
            {
                var over = layout.HitTest(pos.X, pos.Y);
                bool valid = over.Kind == HitKind.Part && over.PartId != null && over.PartId != press.Hit.PartId;
                press.TargetPartId = valid ? over.PartId : null;
                press.Highlight = valid ? layout.FindPart(over.PartId)?.Box : null;
                break;
            }
            case DragKind.Row:
            {
                var rows = layout.Rows;
                if (rows.Count == 0) break;
                int index = rows.Count(r => r.Box.CenterY < pos.Y);
                double y = index == 0 ? rows[0].Box.Y - 8
                    : index >= rows.Count ? rows[^1].Box.Bottom + 5
                    : (rows[index - 1].Box.Bottom + rows[index].Box.Y) / 2 - bar / 2;
                press.InsertIndex = index;
                press.Indicator = new RectD(layout.LeftColumn.X, y, layout.LeftColumn.W, bar);
                break;
            }
            case DragKind.Lore:
            {
                var lore = layout.Lore;
                if (lore.Count == 0) break;
                int index = lore.Count(l => l.Box.CenterY < pos.Y);
                double y = index == 0 ? lore[0].Box.Y - 6
                    : index >= lore.Count ? lore[^1].Box.Bottom + 4
                    : (lore[index - 1].Box.Bottom + lore[index].Box.Y) / 2 - bar / 2;
                press.InsertIndex = index;
                press.Indicator = new RectD(lore[0].Box.X, y, lore[0].Box.W, bar);
                break;
            }
        }
    }

    /// <summary>Where a slot dropped at <paramref name="pos"/> would go: the row under (or nearest to) the pointer, the index in it, and the bar that shows it.</summary>
    private static (string SectionId, int Index, RectD Bar)? SlotInsertion(PageLayout layout, Point pos)
    {
        const double bar = 3;
        var sections = layout.AllSections.ToList();
        if (sections.Count == 0) return null;
        var target = sections.FirstOrDefault(s => pos.Y >= s.Box.Y && pos.Y < s.Box.Bottom)
                     ?? sections.OrderBy(s => Math.Min(Math.Abs(pos.Y - s.Box.Y), Math.Abs(pos.Y - s.Box.Bottom))).First();
        var slots = target.Slots;
        int index = slots.Count(s => s.Area.CenterX < pos.X);
        if (slots.Count == 0)
            return (target.Section.Id, 0, new RectD(target.SlotsArea.X, target.SlotsArea.Y, bar, Math.Max(40, target.SlotsArea.H)));
        double x = index == 0 ? slots[0].Area.X - 4 - bar / 2
            : index >= slots.Count ? slots[^1].Area.Right + 4 - bar / 2
            : (slots[index - 1].Area.Right + slots[index].Area.X) / 2 - bar / 2;
        return (target.Section.Id, index, new RectD(x, slots[0].Area.Y, bar, slots[0].Area.H));
    }

    private static void CommitDrag(EditorViewModel editor, Press press)
    {
        var hit = press.Hit;
        if (press.Kind == DragKind.Image)
        {
            if (hit.PartId != null && press.TargetPartId != null) editor.MoveImage(hit.PartId, press.TargetPartId, press.Copy);
            return;
        }
        if (press.InsertIndex < 0) return;
        switch (press.Kind)
        {
            case DragKind.Part when hit.PartId != null:
                editor.MovePart(hit.PartId, press.InsertIndex);
                break;
            case DragKind.Slot when hit.SlotId != null && press.TargetSectionId != null:
                editor.MoveSlot(hit.SlotId, press.TargetSectionId, press.InsertIndex);
                editor.SelectSlot(hit.SlotId);
                break;
            case DragKind.Row when hit.SectionId != null:
                editor.MoveRow(hit.SectionId, press.InsertIndex);
                editor.SelectSection(hit.SectionId);
                break;
            case DragKind.Lore when hit.LoreId != null:
                editor.MoveLore(hit.LoreId, press.InsertIndex);
                editor.SelectLore(hit.LoreId);
                break;
        }
    }

    // ---------- image files dropped from the OS, library images dragged from the panel ----------

    private void SetDropFeedback(RectD? frame, RectD? bar)
    {
        if (_dropHighlight == frame && _dropBar == bar) return;
        _dropHighlight = frame;
        _dropBar = bar;
        InvalidateVisual();
    }

    private static List<string> ImageFiles(DragEventArgs e) =>
        e.DataTransfer.TryGetFiles()?.Select(f => f.TryGetLocalPath()).Where(p => p != null && EditorViewModel.IsImageFile(p)).Select(p => p!).ToList() ?? new List<string>();

    /// <summary>
    /// Where a drop would land. One image over a part goes into that part; anything else becomes new slots at the
    /// position under the pointer, in the row under (or nearest to) it.
    /// </summary>
    private static (string? PartId, string? SectionId, int Index, RectD? Frame, RectD? Bar) DropTarget(PageLayout layout, Point pos, int imageCount)
    {
        var hit = layout.HitTest(pos.X, pos.Y);
        if (imageCount == 1 && hit.Kind == HitKind.Part && hit.PartId != null)
            return (hit.PartId, null, -1, layout.FindPart(hit.PartId)?.Box, null);
        if (pos.X > layout.LeftColumn.Right + 4) return default; // over the lore column: text only
        return SlotInsertion(layout, pos) is { } at ? (null, at.SectionId, at.Index, null, at.Bar) : default;
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        if (_editor is not { IsPreview: false } editor) return;
        string? assetId = e.DataTransfer.TryGetValue(AssetDrag.Format);
        int count = assetId != null ? 1 : e.DataTransfer.Contains(DataFormat.File) ? ImageFiles(e).Count : 0;
        if (count == 0) return; // e.g. a project file: the window handles it
        var target = DropTarget(editor.Layout, ToSheet(e.GetPosition(this)), count);
        SetDropFeedback(target.Frame, target.Bar);
        e.DragEffects = target.PartId != null || target.SectionId != null ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        SetDropFeedback(null, null);
        if (_editor is not { IsPreview: false } editor) return;
        string? assetId = e.DataTransfer.TryGetValue(AssetDrag.Format);
        var files = assetId != null ? new List<string>() : ImageFiles(e);
        if (assetId == null && files.Count == 0) return;
        e.Handled = true;
        var target = DropTarget(editor.Layout, ToSheet(e.GetPosition(this)), assetId != null ? 1 : files.Count);
        if (target.PartId != null)
        {
            if (assetId != null) editor.PlaceAsset(target.PartId, assetId);
            else Run(editor.SetPartImageAsync(target.PartId, files[0]));
        }
        else if (target.SectionId != null)
        {
            if (assetId != null) editor.InsertSlotWithAsset(target.SectionId, assetId, target.Index);
            else Run(editor.AppendSlotsAsync(target.SectionId, files, target.Index)); // one new slot per file, where they were dropped
        }
    }

    // ---------- context menus ----------

    private void ShowContextMenu(EditorViewModel editor, Hit hit)
    {
        var page = editor.CurrentPage;
        var menu = new ContextMenu();

        void Add(string header, Action action, bool enabled = true) =>
            menu.Items.Add(new MenuItem { Header = Loc.T(header), IsEnabled = enabled, Command = new RelayCommand(action) });
        void Separator() => menu.Items.Add(new Separator());

        switch (hit.Kind)
        {
            case HitKind.Part or HitKind.Slot or HitKind.SlotLabel when hit.SlotId != null:
            {
                var found = page.FindSlot(hit.SlotId);
                if (found == null) return;
                var (section, slot, index) = found.Value;
                var part = slot.Parts.FirstOrDefault(p => p.Id == hit.PartId) ?? slot.Parts[0];
                Add(part.ImageId == null ? "Choose image…" : "Replace image…", () => Run(editor.BrowseImageAsync(part.Id)));
                Add("Clear image", () => editor.ClearImage(part.Id), part.ImageId != null);
                Add("Paste image", () => editor.PasteImageCommand.Execute(null));
                Separator();
                Add("Rename label", () => editor.BeginEdit(new TextFieldRef(TextFieldKind.SlotLabel, slot.Id)));
                Add("Split slot", () => editor.SplitSlot(slot.Id));
                Add("Remove this part", () => editor.RemovePart(part.Id), slot.Parts.Count > 1);
                Separator();
                Add("Move left", () => editor.MoveSlot(slot.Id, section.Id, index - 1), index > 0);
                Add("Move right", () => editor.MoveSlot(slot.Id, section.Id, index + 2), index < section.Slots.Count - 1);
                var moveTo = new MenuItem { Header = Loc.T("Move to row") };
                foreach (var other in page.AllSections().Where(s => s.Id != section.Id))
                {
                    var target = other;
                    moveTo.Items.Add(new MenuItem
                    {
                        Header = string.IsNullOrWhiteSpace(target.Name) ? Loc.T("(unnamed)") : target.Name,
                        Command = new RelayCommand(() => editor.MoveSlot(slot.Id, target.Id, target.Slots.Count)),
                    });
                }
                moveTo.IsEnabled = moveTo.Items.Count > 0;
                menu.Items.Add(moveTo);
                Separator();
                Add("Duplicate slot", () => editor.DuplicateSelectionCommand.Execute(null));
                Add("Delete slot", () => { editor.SelectSlot(slot.Id); editor.DeleteSelectionCommand.Execute(null); });
                break;
            }
            case HitKind.Section or HitKind.SectionName or HitKind.AddSlot when hit.SectionId != null:
            {
                var section = page.FindSection(hit.SectionId);
                if (section == null) return;
                bool details = page.IsDetails(section.Id);
                int index = page.Rows.FindIndex(r => r.Id == section.Id);
                Add("Rename", () => editor.BeginEdit(new TextFieldRef(TextFieldKind.SectionName, section.Id)));
                Add("Add slot", () => editor.AddSlotTo(section.Id));
                Add("Paste image as new slot", () => editor.PasteImageCommand.Execute(null));
                if (!details)
                {
                    Separator();
                    Add("Insert row above", () => editor.InsertRow(section.Id, below: false));
                    Add("Insert row below", () => editor.InsertRow(section.Id, below: true));
                    Add("Move up", () => editor.MoveSelection(-1), index > 0);
                    Add("Move down", () => editor.MoveSelection(1), index < page.Rows.Count - 1);
                    Separator();
                    Add("Duplicate row", () => editor.DuplicateSelectionCommand.Execute(null));
                    Add("Delete row", () => editor.DeleteSelectionCommand.Execute(null));
                }
                break;
            }
            case HitKind.Lore or HitKind.LoreTitle or HitKind.LoreBody when hit.LoreId != null:
            {
                int index = page.Lore.FindIndex(l => l.Id == hit.LoreId);
                if (index < 0) return;
                string id = hit.LoreId;
                Add("Rename section", () => editor.BeginEdit(new TextFieldRef(TextFieldKind.LoreTitle, id)));
                Add("Edit text", () => editor.BeginEdit(new TextFieldRef(TextFieldKind.LoreBody, id)));
                Separator();
                Add("Add section below", () => editor.AddLoreCommand.Execute(null));
                Add("Move up", () => editor.MoveSelection(-1), index > 0);
                Add("Move down", () => editor.MoveSelection(1), index < page.Lore.Count - 1);
                Separator();
                Add("Duplicate section", () => editor.DuplicateSelectionCommand.Execute(null));
                Add("Delete section", () => editor.DeleteSelectionCommand.Execute(null));
                break;
            }
            default:
                Add("Add row", () => editor.AddRowCommand.Execute(null));
                Add("Add lore section", () => editor.AddLoreCommand.Execute(null));
                Separator();
                Add("Rename species", () => editor.BeginEdit(new TextFieldRef(TextFieldKind.PageName)));
                Add("Preview", () => editor.IsPreview = true);
                break;
        }
        menu.Open(this);
    }
}
