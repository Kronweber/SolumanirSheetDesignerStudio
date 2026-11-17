using System.Collections.Immutable;
using CommunityToolkit.Mvvm.ComponentModel;
using SolumanirSheetDesignerStudio.App.Services;
using SolumanirSheetDesignerStudio.Core.Layout;
using SolumanirSheetDesignerStudio.Core.Model;

namespace SolumanirSheetDesignerStudio.App.ViewModels;

/// <summary>
/// Sizes changed directly on the sheet — dragging an edge, turning the wheel, pressing + or − — and images placed
/// by dragging. Each of these used to need the inspector: select, find the field, type.
/// </summary>
public sealed partial class EditorViewModel
{
    public const double ScaleStep = 5, HeightStep = 10;
    private const double MaxRowHeight = 4000, MaxRowCap = 8000;

    /// <summary>What the element under the pointer can do, shown in the status bar.</summary>
    [ObservableProperty] private string _hint = "";

    /// <summary>A Fixed row's image-area height, or an Auto row's cap on image height (what its bottom edge stands for).</summary>
    public double SectionSize(string sectionId)
    {
        var section = CurrentPage.FindSection(sectionId);
        if (section == null) return 0;
        if (section.Sizing.Mode != SizingMode.Auto) return section.Sizing.EffectiveHeight;
        if (section.Sizing.EffectiveMaxHeight > 0) return section.Sizing.EffectiveMaxHeight;
        return Layout.FindSection(sectionId)?.Slots.FirstOrDefault()?.Area.H ?? Sizing.DefaultHeight; // no cap yet: start from what is shown
    }

    public void SetSectionSize(string sectionId, double value)
    {
        var section = CurrentPage.FindSection(sectionId);
        if (section == null || IsPreview || !double.IsFinite(value)) return;
        if (section.Sizing.Mode == SizingMode.Auto)
        {
            double cap = Math.Clamp(Math.Round(value), Sizing.MinMaxHeight, MaxRowCap);
            Edit(Loc.T("change max height"), p => p.UpdateSection(sectionId, s => s.Sizing.MaxHeight == cap ? s : s with { Sizing = s.Sizing with { MaxHeight = cap } }), "size:" + sectionId);
        }
        else
        {
            double height = Math.Clamp(Math.Round(value), Sizing.MinHeight, MaxRowHeight);
            Edit(Loc.T("change row height"), p => p.UpdateSection(sectionId, s => s.Sizing.Height == height ? s : s with { Sizing = s.Sizing with { Height = height } }), "size:" + sectionId);
        }
    }

    /// <summary>True when the part's number is a share of the slot's height (split slot in a Fixed row) rather than a scale in percent.</summary>
    public bool IsWeighted(string partId) =>
        CurrentPage.FindPart(partId) is { } found && found.Slot.Parts.Count > 1 && found.Section.Sizing.Mode != SizingMode.Auto;

    public void NudgePartScale(string partId, double delta)
    {
        if (IsPreview || CurrentPage.FindPart(partId) is not { } found) return;
        double max = IsWeighted(partId) ? 1000 : 100;
        SetPartScale(partId, Math.Clamp(Math.Round(Math.Min(max, found.Part.Weight) + delta), 5, max));
    }

    /// <summary>Two neighbouring parts of a split slot trade height; the others keep theirs.</summary>
    public void SetSplitWeights(string firstPartId, double first, string secondPartId, double second) =>
        Edit(Loc.T("change split"), p => p.UpdatePart(firstPartId, x => x.Scale == first ? x : x with { Scale = first })
            .UpdatePart(secondPartId, x => x.Scale == second ? x : x with { Scale = second }), "split:" + firstPartId);

    public void SetLoreWidth(double percent)
    {
        if (IsPreview || !double.IsFinite(percent)) return;
        double width = Math.Clamp(Math.Round(percent * 2) / 2, 24, 52);
        EditStyle(s => s.LoreWidth == width ? s : s with { LoreWidth = width }, "lore-width");
    }

    /// <summary>"+" / "−": grows or shrinks whatever is selected — an image's scale, a split part's share, a row's height.</summary>
    public bool NudgeSelectionSize(int direction, bool fine)
    {
        if (IsPreview) return false;
        var sel = Selection;
        var page = CurrentPage;
        string? partId = sel.Kind == SelectionKind.Part ? sel.PartId
            : sel.Kind == SelectionKind.Slot ? page.FindSlot(sel.SlotId)?.Slot.Parts[0].Id
            : null;
        if (partId != null)
        {
            NudgePartScale(partId, direction * (fine ? 1 : ScaleStep));
            return true;
        }
        if (sel.Kind == SelectionKind.Section && sel.SectionId != null)
        {
            SetSectionSize(sel.SectionId, SectionSize(sel.SectionId) + direction * (fine ? 1 : HeightStep));
            return true;
        }
        return false;
    }

    // ---------- images placed by dragging ----------

    /// <summary>Puts a library image into a part.</summary>
    public void PlaceAsset(string partId, string assetId)
    {
        if (IsPreview || !Session.Assets.Contains(assetId)) return;
        if (Edit(Loc.T("set image"), p => p.SetPartImage(partId, assetId))) SelectPart(partId);
    }

    /// <summary>Adds a slot showing a library image at <paramref name="index"/> of a row (null = at the end).</summary>
    public void InsertSlotWithAsset(string sectionId, string assetId, int? index)
    {
        if (IsPreview || Session.Assets.Get(assetId) is not { } asset) return;
        var slot = new Slot { Label = Path.GetFileNameWithoutExtension(asset.FileName), Parts = ImmutableList.Create(new Part { ImageId = asset.Id }) };
        if (Edit(Loc.T("add image"), p => p.AddSlot(sectionId, slot, index))) SelectSlot(slot.Id);
    }

    /// <summary>
    /// Dragging one image onto another part: the two parts swap images (an empty target simply receives it), or,
    /// with <paramref name="copy"/>, the target gets a second placement of the same image.
    /// </summary>
    public void MoveImage(string fromPartId, string toPartId, bool copy)
    {
        if (IsPreview || fromPartId == toPartId) return;
        var page = CurrentPage;
        if (page.FindPart(fromPartId) is not { } from || page.FindPart(toPartId) is not { } to || from.Part.ImageId == null) return;
        string? moving = from.Part.ImageId, displaced = to.Part.ImageId;
        bool changed = copy
            ? Edit(Loc.T("copy image"), p => p.SetPartImage(toPartId, moving))
            : Edit(Loc.T("move image"), p => p.SetPartImage(toPartId, moving).SetPartImage(fromPartId, displaced));
        if (changed) SelectPart(toPartId);
    }
}
