using Avalonia;
using Avalonia.Input;
using SolumanirSheetDesignerStudio.App.Services;
using SolumanirSheetDesignerStudio.App.ViewModels;
using SolumanirSheetDesignerStudio.Core.Layout;
using SolumanirSheetDesignerStudio.Core.Model;

namespace SolumanirSheetDesignerStudio.App.Controls;

/// <summary>
/// Sizes changed on the sheet itself: drag the bottom edge of a row (height), the gap before the lore column
/// (its width), the line between the parts of a split slot (their shares) or the corner of the selected image
/// (its scale); Alt+wheel does the same in steps. Every drag is one undo step.
/// </summary>
public sealed partial class SheetCanvas
{
    private enum HandleKind { None, RowHeight, LoreWidth, SplitDivider, ImageScale }

    /// <param name="Mark">What is highlighted while the pointer is over the handle (sheet units).</param>
    private readonly record struct Handle(HandleKind Kind, RectD Mark, string? SectionId = null, string? PartId = null, string? NextPartId = null);

    private sealed class ResizeDrag
    {
        public Handle Handle;
        public Point Start;      // sheet units
        public double StartValue, StartOther, StartSpan, StartFirst;
        public Point Anchor;
        public bool AnchoredAtBottom;
    }

    private static readonly Cursor RowCursor = new(StandardCursorType.SizeNorthSouth);
    private static readonly Cursor ColumnCursor = new(StandardCursorType.SizeWestEast);
    private static readonly Cursor ScaleCursor = new(StandardCursorType.TopRightCorner);

    private Handle _handleHover;
    private ResizeDrag? _resize;

    /// <summary>The corner handle of the selected image, when its number is a scale in percent (not a split share).</summary>
    private static Handle? ScaleHandleOf(EditorViewModel editor)
    {
        var sel = editor.Selection;
        var layout = editor.Layout;
        string? partId = sel.Kind == SelectionKind.Part ? sel.PartId
            : sel.Kind == SelectionKind.Slot ? layout.FindSlot(sel.SlotId)?.Slot.Parts.FirstOrDefault()?.Part.Id
            : null;
        if (partId == null || layout.FindPart(partId)?.Image is not { } image || editor.IsWeighted(partId)) return null;
        double size = Math.Max(7, 9 / editor.Zoom);
        return new Handle(HandleKind.ImageScale, new RectD(image.Drawn.Right - size / 2, image.Drawn.Y - size / 2, size, size), PartId: partId);
    }

    private static Handle FindHandle(EditorViewModel editor, Point pos)
    {
        var layout = editor.Layout;
        double reach = Math.Max(2.5, 5 / editor.Zoom); // about five screen pixels either side

        if (ScaleHandleOf(editor) is { } scale && scale.Mark.Inflate(reach * 0.6).Contains(pos.X, pos.Y)) return scale;

        foreach (var section in layout.AllSections)
        {
            // Parts share the height by weight in Fixed rows only; in Auto rows each part hugs its image.
            if (section.Section.Sizing.Mode == SizingMode.Auto) continue;
            foreach (var slot in section.Slots)
            {
                if (slot.Parts.Count < 2 || pos.X < slot.Area.X || pos.X > slot.Area.Right) continue;
                for (int q = 0; q + 1 < slot.Parts.Count; q++)
                {
                    double y = (slot.Parts[q].Box.Bottom + slot.Parts[q + 1].Box.Y) / 2;
                    if (Math.Abs(pos.Y - y) <= reach)
                        return new Handle(HandleKind.SplitDivider, new RectD(slot.Area.X, y - 1.5, slot.Area.W, 3), section.Section.Id, slot.Parts[q].Part.Id, slot.Parts[q + 1].Part.Id);
                }
            }
        }

        foreach (var section in layout.AllSections)
        {
            if (section.Slots.Count == 0) continue;
            double bottom = section.Slots[0].Area.Bottom;
            // Mostly inside the image area: just below it the slot labels begin, and those are for clicking.
            if (pos.X >= section.SlotsArea.X && pos.X <= section.SlotsArea.Right && pos.Y >= bottom - reach && pos.Y <= bottom + Math.Min(reach, 1.5))
                return new Handle(HandleKind.RowHeight, new RectD(section.SlotsArea.X, bottom - 1.5, section.SlotsArea.W, 3), section.Section.Id);
        }

        if (layout.LoreBox.W > 0 && pos.X >= layout.LeftColumn.Right && pos.X <= layout.LoreBox.X &&
            pos.Y >= layout.LoreBox.Y && pos.Y <= Math.Max(layout.LoreBox.Bottom, layout.LeftColumn.Bottom))
        {
            double x = (layout.LeftColumn.Right + layout.LoreBox.X) / 2;
            return new Handle(HandleKind.LoreWidth, new RectD(x - 1.5, layout.LoreBox.Y, 3, Math.Max(layout.LoreBox.Bottom, layout.LeftColumn.Bottom) - layout.LoreBox.Y));
        }
        return default;
    }

    private static string HintFor(Handle handle) => handle.Kind switch
    {
        HandleKind.RowHeight => Loc.T("Drag to change the row height"),
        HandleKind.LoreWidth => Loc.T("Drag to change the lore column width · double-click to reset"),
        HandleKind.SplitDivider => Loc.T("Drag to change how the parts share the height · double-click for equal parts"),
        HandleKind.ImageScale => Loc.T("Drag to scale the image · double-click for 100 %"),
        _ => "",
    };

    private static Cursor CursorFor(Handle handle) => handle.Kind switch
    {
        HandleKind.LoreWidth => ColumnCursor,
        HandleKind.ImageScale => ScaleCursor,
        _ => RowCursor,
    };

    /// <summary>Starts a resize if the press is on a handle (a double-click resets the value instead). True = the press is taken.</summary>
    private bool TryBeginResize(EditorViewModel editor, Point pos, PointerPressedEventArgs e)
    {
        var handle = FindHandle(editor, pos);
        if (handle.Kind == HandleKind.None) return false;
        var layout = editor.Layout;
        editor.EndEdit();
        editor.Session.BreakCoalescing(); // this drag is its own undo step

        if (e.ClickCount >= 2)
        {
            switch (handle.Kind)
            {
                case HandleKind.ImageScale when handle.PartId != null:
                    editor.SetPartScale(handle.PartId, 100);
                    break;
                case HandleKind.LoreWidth:
                    editor.SetLoreWidth(SheetStyle.Default.LoreWidth);
                    break;
                case HandleKind.SplitDivider when handle.PartId != null && handle.NextPartId != null:
                    if (layout.FindPart(handle.PartId) is { } a && layout.FindPart(handle.NextPartId) is { } b)
                    {
                        double each = Math.Round((a.Part.Weight + b.Part.Weight) / 2);
                        editor.SetSplitWeights(handle.PartId, each, handle.NextPartId, each);
                    }
                    break;
            }
            e.Handled = true;
            return true;
        }

        var drag = new ResizeDrag { Handle = handle, Start = pos };
        switch (handle.Kind)
        {
            case HandleKind.RowHeight when handle.SectionId != null:
                editor.SelectSection(handle.SectionId); // the inspector then shows the number changing
                drag.StartValue = editor.SectionSize(handle.SectionId);
                break;
            case HandleKind.SplitDivider when handle.PartId != null && handle.NextPartId != null:
                if (layout.FindPart(handle.PartId) is not { } first || layout.FindPart(handle.NextPartId) is not { } second) return false;
                drag.StartValue = first.Part.Weight;
                drag.StartOther = second.Part.Weight;
                drag.StartFirst = first.Box.H;
                drag.StartSpan = first.Box.H + second.Box.H;
                break;
            case HandleKind.ImageScale when handle.PartId != null:
                if (layout.FindPart(handle.PartId) is not { Image: { } image } part) return false;
                drag.StartValue = Math.Min(100, part.Part.Weight);
                drag.AnchoredAtBottom = image.AlignBottom;
                drag.Anchor = new Point(image.Container.CenterX, image.AlignBottom ? image.Container.Bottom : image.Container.CenterY);
                drag.StartSpan = Distance(drag, pos);
                break;
        }
        _resize = drag;
        Cursor = CursorFor(handle);
        e.Pointer.Capture(this);
        e.Handled = true;
        return true;
    }

    /// <summary>How far the pointer is from the point the image grows from: its base line in rows, its centre in the details row.</summary>
    private static double Distance(ResizeDrag drag, Point pos) =>
        drag.AnchoredAtBottom ? drag.Anchor.Y - pos.Y : Math.Sqrt(Math.Pow(pos.X - drag.Anchor.X, 2) + Math.Pow(pos.Y - drag.Anchor.Y, 2));

    private static void UpdateResize(EditorViewModel editor, ResizeDrag drag, Point pos)
    {
        var handle = drag.Handle;
        switch (handle.Kind)
        {
            case HandleKind.RowHeight when handle.SectionId != null:
                // The row's top does not move while it is resized, so the pointer's travel is the change in height.
                editor.SetSectionSize(handle.SectionId, drag.StartValue + (pos.Y - drag.Start.Y));
                break;

            case HandleKind.LoreWidth:
            {
                var layout = editor.Layout;
                double body = layout.LoreBox.Right - layout.LeftColumn.X, gap = layout.LoreBox.X - layout.LeftColumn.Right;
                if (body > 0) editor.SetLoreWidth((layout.LoreBox.Right - pos.X - gap / 2) / body * 100);
                break;
            }

            case HandleKind.SplitDivider when handle.PartId != null && handle.NextPartId != null && drag.StartSpan > 0:
            {
                // The two neighbours keep their combined share; the divider only moves between them.
                double total = drag.StartValue + drag.StartOther;
                double firstHeight = Math.Clamp(drag.StartFirst + (pos.Y - drag.Start.Y), drag.StartSpan * 0.04, drag.StartSpan * 0.96);
                double first = Math.Clamp(Math.Round(total * firstHeight / drag.StartSpan), 5, 1000);
                editor.SetSplitWeights(handle.PartId, first, handle.NextPartId, Math.Clamp(Math.Round(total - first), 5, 1000));
                break;
            }

            case HandleKind.ImageScale when handle.PartId != null && drag.StartSpan > 1:
                editor.SetPartScale(handle.PartId, Math.Clamp(Math.Round(drag.StartValue * Distance(drag, pos) / drag.StartSpan), 5, 100));
                break;
        }
    }

    /// <summary>Alt+wheel over an image changes its scale (or share), elsewhere in a row the row's height. Alt+Shift: fine steps.</summary>
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (_editor is not { IsPreview: false } editor || !e.KeyModifiers.HasFlag(KeyModifiers.Alt) || e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
        double turn = e.Delta.Y != 0 ? e.Delta.Y : e.Delta.X; // some platforms report Alt+wheel as horizontal
        if (turn == 0) return;
        int direction = Math.Sign(turn);
        bool fine = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        var pos = ToSheet(e.GetPosition(this));
        var hit = editor.Layout.HitTest(pos.X, pos.Y);
        if (hit.Kind == HitKind.Part && hit.PartId != null) editor.NudgePartScale(hit.PartId, direction * (fine ? 1 : EditorViewModel.ScaleStep));
        else if (hit.SectionId != null) editor.SetSectionSize(hit.SectionId, editor.SectionSize(hit.SectionId) + direction * (fine ? 1 : EditorViewModel.HeightStep));
        else return;
        e.Handled = true; // otherwise the view would scroll as well
    }
}
