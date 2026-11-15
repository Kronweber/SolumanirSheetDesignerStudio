using System.Collections.Immutable;
using CommunityToolkit.Mvvm.Input;
using SolumanirSheetDesignerStudio.App.Services;
using SolumanirSheetDesignerStudio.Core.Assets;
using SolumanirSheetDesignerStudio.Core.Layout;
using SolumanirSheetDesignerStudio.Core.Model;

namespace SolumanirSheetDesignerStudio.App.ViewModels;

public sealed partial class EditorViewModel
{
    // ---------- undo / redo ----------

    public string UndoTip => Session.UndoLabel is { } l ? Loc.T("Undo {0}", l) : Loc.T("Undo");
    public string RedoTip => Session.RedoLabel is { } l ? Loc.T("Redo {0}", l) : Loc.T("Redo");

    private bool CanUndo() => Session.CanUndo;
    private bool CanRedo() => Session.CanRedo;

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo()
    {
        EndEdit();
        Session.Undo();
    }

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo()
    {
        EndEdit();
        Session.Redo();
    }

    /// <summary>Applies an edit to the current page.</summary>
    public bool Edit(string label, Func<Page, Page> edit, string? coalesceKey = null) => Session.ApplyToPage(label, edit, coalesceKey);

    /// <summary>Applies an edit to a page chosen earlier (before an await): the user may be looking at another page by now.</summary>
    private bool EditPage(string pageId, string label, Func<Page, Page> edit) => Session.Apply(label, p => p.UpdatePage(pageId, edit));

    /// <summary>A pause this long while typing ends the undo step, so Undo takes back a phrase, not the whole session.</summary>
    private const long TypingPauseMs = 1200;
    private long _lastTextEditAt;

    // ---------- in-place text ----------

    public string GetFieldText(TextFieldRef field)
    {
        var page = CurrentPage;
        return field.Kind switch
        {
            TextFieldKind.PageName => page.Name,
            TextFieldKind.Subtitle => page.Subtitle,
            TextFieldKind.SectionName => page.FindSection(field.Id)?.Name ?? "",
            TextFieldKind.SlotLabel => page.FindSlot(field.Id)?.Slot.Label ?? "",
            TextFieldKind.LoreTitle => page.FindLore(field.Id)?.Title ?? "",
            TextFieldKind.LoreBody => page.FindLore(field.Id)?.Body ?? "",
            _ => "",
        };
    }

    /// <summary>Live text edit; consecutive changes to the same field form one undo step.</summary>
    public void SetFieldText(TextFieldRef field, string text)
    {
        long now = Environment.TickCount64;
        if (now - _lastTextEditAt > TypingPauseMs) Session.BreakCoalescing();
        _lastTextEditAt = now;
        string key = $"text:{field.Kind}:{field.Id}";
        string id = field.Id ?? "";
        switch (field.Kind)
        {
            case TextFieldKind.PageName:
                Edit(Loc.T("rename species"), p => p.Name == text ? p : p with { Name = text }, key);
                break;
            case TextFieldKind.Subtitle:
                Edit(Loc.T("edit subtitle"), p => p.Subtitle == text ? p : p with { Subtitle = text }, key);
                break;
            case TextFieldKind.SectionName:
                Edit(Loc.T("rename row"), p => p.UpdateSection(id, s => s.Name == text ? s : s with { Name = text }), key);
                break;
            case TextFieldKind.SlotLabel:
                Edit(Loc.T("edit label"), p => p.UpdateSlot(id, s => s.Label == text ? s : s with { Label = text }), key);
                break;
            case TextFieldKind.LoreTitle:
                Edit(Loc.T("rename section"), p => p.UpdateLore(id, l => l.Title == text ? l : l with { Title = text }), key);
                break;
            case TextFieldKind.LoreBody:
                Edit(Loc.T("edit lore"), p => p.UpdateLore(id, l => l.Body == text ? l : l with { Body = text }), key);
                break;
        }
    }

    public void BeginEdit(TextFieldRef field)
    {
        if (IsPreview || !FieldExists(field)) return;
        if (EditingField == field) return;
        Session.BreakCoalescing();
        EditingLoreHeight = 180;
        EditingField = field;
        Relayout();
    }

    public void EndEdit()
    {
        if (EditingField == null) return;
        EditingField = null;
        Session.BreakCoalescing();
        Relayout();
    }

    // ---------- rows ----------

    [RelayCommand]
    private void AddRow()
    {
        if (IsPreview) return;
        var page = CurrentPage;
        var row = Factory.NewRow(Settings.DefaultRowHeight);
        // After the selected row, otherwise at the end.
        int? index = null;
        string? selectedSection = Selection.SectionId ?? page.FindSlot(Selection.SlotId)?.Section.Id;
        int at = selectedSection == null ? -1 : page.Rows.FindIndex(r => r.Id == selectedSection);
        if (at >= 0) index = at + 1;
        if (Edit(Loc.T("add row"), p => p.AddRow(row, index))) SelectSection(row.Id);
    }

    public void InsertRow(string relativeToRowId, bool below)
    {
        int at = CurrentPage.Rows.FindIndex(r => r.Id == relativeToRowId);
        if (at < 0) return;
        var row = Factory.NewRow(Settings.DefaultRowHeight);
        if (Edit(Loc.T("add row"), p => p.AddRow(row, below ? at + 1 : at))) SelectSection(row.Id);
    }

    public void MoveRow(string rowId, int insertIndex)
    {
        int from = CurrentPage.Rows.FindIndex(r => r.Id == rowId);
        if (from < 0) return;
        int to = insertIndex > from ? insertIndex - 1 : insertIndex;
        Edit(Loc.T("move row"), p => p.MoveRow(from, to));
    }

    // ---------- slots ----------

    /// <summary>The section "add slot" targets: the selected one, else the first row, else the details row.</summary>
    private Section TargetSection()
    {
        var page = CurrentPage;
        return page.FindSection(Selection.SectionId)
               ?? page.FindSlot(Selection.SlotId)?.Section
               ?? page.FindPart(Selection.PartId)?.Section
               ?? (page.Rows.Count > 0 ? page.Rows[0] : page.Details);
    }

    [RelayCommand]
    private void AddSlot()
    {
        if (!IsPreview) AddSlotTo(TargetSection().Id);
    }

    public void AddSlotTo(string sectionId, int? index = null)
    {
        var page = CurrentPage;
        if (page.FindSection(sectionId) == null) return;
        var slot = Factory.NewSlot(DefaultSlotLabel(page, sectionId));
        if (Edit(Loc.T("add slot"), p => p.AddSlot(sectionId, slot, index))) SelectSlot(slot.Id);
    }

    private static string DefaultSlotLabel(Page page, string sectionId) => page.IsDetails(sectionId) ? Loc.T("Detail") : Loc.T("Outfit");

    public void MoveSlot(string slotId, string targetSectionId, int insertIndex) =>
        Edit(Loc.T("move slot"), p => p.MoveSlot(slotId, targetSectionId, insertIndex));

    public void SplitSlot(string slotId)
    {
        var part = new Part();
        if (Edit(Loc.T("split slot"), p => p.SplitSlot(slotId, part))) SelectPart(part.Id);
    }

    public void RemovePart(string partId)
    {
        var found = CurrentPage.FindPart(partId);
        if (found == null || found.Value.Slot.Parts.Count <= 1) return;
        string slotId = found.Value.Slot.Id;
        if (Edit(Loc.T("remove part"), p => p.RemovePart(partId))) SelectSlot(slotId);
    }

    public void MovePart(string partId, int insertIndex)
    {
        var found = CurrentPage.FindPart(partId);
        if (found == null) return;
        int from = found.Value.Index;
        int to = insertIndex > from ? insertIndex - 1 : insertIndex;
        Edit(Loc.T("reorder parts"), p => p.MovePart(partId, to));
    }

    public void ClearImage(string partId) => Edit(Loc.T("clear image"), p => p.SetPartImage(partId, null));

    public void SetPartScale(string partId, double scale) =>
        Edit(Loc.T("change scale"), p => p.UpdatePart(partId, x => x.Scale == scale ? x : x with { Scale = scale }), "scale:" + partId);

    // ---------- lore ----------

    [RelayCommand]
    private void AddLore()
    {
        if (IsPreview) return;
        var section = Factory.NewLore();
        int at = Selection.Kind == SelectionKind.Lore ? CurrentPage.Lore.FindIndex(l => l.Id == Selection.LoreId) : -1;
        if (Edit(Loc.T("add lore section"), p => p.AddLore(section, at >= 0 ? at + 1 : null))) SelectLore(section.Id);
    }

    public void MoveLore(string loreId, int insertIndex)
    {
        int from = CurrentPage.Lore.FindIndex(l => l.Id == loreId);
        if (from < 0) return;
        int to = insertIndex > from ? insertIndex - 1 : insertIndex;
        Edit(Loc.T("move lore section"), p => p.MoveLore(from, to));
    }

    // ---------- selection-based commands ----------

    private bool HasSelection() => Selection.Kind != SelectionKind.None && !IsPreview;

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task DeleteSelectionAsync()
    {
        var page = CurrentPage;
        var sel = Selection;
        switch (sel.Kind)
        {
            case SelectionKind.Section:
            {
                var section = page.FindSection(sel.SectionId);
                if (section == null) return;
                if (page.IsDetails(section.Id))
                {
                    Main.Toasts.Show(Loc.T("The details row cannot be deleted. Remove its slots to hide it from the export."));
                    return;
                }
                if (!await ConfirmDeleteAsync(Loc.T("Delete the row \"{0}\" and its {1} slot(s)?", section.Name, section.Slots.Count))) return;
                Edit(Loc.T("delete row"), p => p.RemoveRow(section.Id));
                break;
            }
            case SelectionKind.Slot:
            {
                var found = page.FindSlot(sel.SlotId);
                if (found == null) return;
                bool hasImage = found.Value.Slot.Parts.Any(x => x.ImageId != null);
                if (hasImage && !await ConfirmDeleteAsync(Loc.T("Delete the slot \"{0}\"?", found.Value.Slot.Label))) return;
                string sectionId = found.Value.Section.Id;
                if (Edit(Loc.T("delete slot"), p => p.RemoveSlot(found.Value.Slot.Id))) SelectSection(sectionId);
                break;
            }
            case SelectionKind.Part:
                if (sel.PartId != null) RemovePart(sel.PartId);
                break;
            case SelectionKind.Lore:
            {
                var lore = page.FindLore(sel.LoreId);
                if (lore == null) return;
                if (lore.Body.Trim().Length > 0 && !await ConfirmDeleteAsync(Loc.T("Delete the lore section \"{0}\"?", lore.Title))) return;
                Edit(Loc.T("delete lore section"), p => p.RemoveLore(lore.Id));
                break;
            }
        }
    }

    private async Task<bool> ConfirmDeleteAsync(string message) =>
        !Settings.ConfirmDelete || await Main.Dialogs.ConfirmAsync(Loc.T("Delete"), message, Loc.T("Delete"));

    private bool CanDuplicate() => !IsPreview;

    /// <summary>Ctrl+D: duplicates the selected row, slot or lore section — or the page when nothing is selected.</summary>
    [RelayCommand(CanExecute = nameof(CanDuplicate))]
    private void DuplicateSelection()
    {
        var page = CurrentPage;
        var sel = Selection;
        switch (sel.Kind)
        {
            case SelectionKind.Section:
            {
                int at = page.Rows.FindIndex(r => r.Id == sel.SectionId);
                if (at < 0) { Main.Toasts.Show(Loc.T("The details row cannot be duplicated.")); return; }
                var copy = page.Rows[at].CloneWithNewIds();
                if (Edit(Loc.T("duplicate row"), p => p.AddRow(copy, at + 1))) SelectSection(copy.Id);
                break;
            }
            case SelectionKind.Slot:
            case SelectionKind.Part:
            {
                var found = page.FindSlot(sel.SlotId ?? page.FindPart(sel.PartId)?.Slot.Id);
                if (found == null) return;
                var copy = found.Value.Slot.CloneWithNewIds();
                if (Edit(Loc.T("duplicate slot"), p => p.AddSlot(found.Value.Section.Id, copy, found.Value.Index + 1))) SelectSlot(copy.Id);
                break;
            }
            case SelectionKind.Lore:
            {
                int at = page.Lore.FindIndex(l => l.Id == sel.LoreId);
                if (at < 0) return;
                var copy = page.Lore[at] with { Id = Ids.New() };
                if (Edit(Loc.T("duplicate lore section"), p => p.AddLore(copy, at + 1))) SelectLore(copy.Id);
                break;
            }
            default:
                DuplicatePages(new[] { page.Id });
                break;
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void MoveSelectionBack() => MoveSelection(-1);

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void MoveSelectionForward() => MoveSelection(1);

    /// <summary>Alt+arrows: moves the selected row / lore section up or down, a slot left or right, a part within its slot.</summary>
    public void MoveSelection(int delta)
    {
        var page = CurrentPage;
        var sel = Selection;
        switch (sel.Kind)
        {
            case SelectionKind.Section:
            {
                int at = page.Rows.FindIndex(r => r.Id == sel.SectionId);
                if (at >= 0) Edit(Loc.T("move row"), p => p.MoveRow(at, at + delta));
                break;
            }
            case SelectionKind.Slot:
            {
                var found = page.FindSlot(sel.SlotId);
                if (found == null) return;
                int to = Math.Clamp(found.Value.Index + delta, 0, found.Value.Section.Slots.Count - 1);
                string sectionId = found.Value.Section.Id;
                int from = found.Value.Index;
                Edit(Loc.T("move slot"), p => p.UpdateSection(sectionId, s => s with { Slots = s.Slots.Move(from, to) }));
                break;
            }
            case SelectionKind.Part:
            {
                var found = page.FindPart(sel.PartId);
                if (found != null) Edit(Loc.T("reorder parts"), p => p.MovePart(found.Value.Part.Id, found.Value.Index + delta));
                break;
            }
            case SelectionKind.Lore:
            {
                int at = page.Lore.FindIndex(l => l.Id == sel.LoreId);
                if (at >= 0) Edit(Loc.T("move lore section"), p => p.MoveLore(at, at + delta));
                break;
            }
        }
    }

    private bool CanRename() => !IsPreview;

    /// <summary>F2: starts in-place editing of the selected element's name (the species name when nothing is selected).</summary>
    [RelayCommand(CanExecute = nameof(CanRename))]
    private void RenameSelection()
    {
        var sel = Selection;
        BeginEdit(sel.Kind switch
        {
            SelectionKind.Section => new TextFieldRef(TextFieldKind.SectionName, sel.SectionId),
            SelectionKind.Slot or SelectionKind.Part => new TextFieldRef(TextFieldKind.SlotLabel, sel.SlotId ?? CurrentPage.FindPart(sel.PartId)?.Slot.Id),
            SelectionKind.Lore => new TextFieldRef(TextFieldKind.LoreTitle, sel.LoreId),
            _ => new TextFieldRef(TextFieldKind.PageName),
        });
    }

    /// <summary>
    /// Esc: leave text editing, else leave preview, else drop the selection one level. (A running export is only
    /// cancelled with its Cancel button: Esc is pressed too casually to throw minutes of rendering away.)
    /// </summary>
    public void Escape()
    {
        if (EditingField != null) { EndEdit(); return; }
        if (IsPreview) { IsPreview = false; return; }
        var sel = Selection;
        switch (sel.Kind)
        {
            case SelectionKind.Part: Select(new Selection(SelectionKind.Slot, SlotId: sel.SlotId)); break;
            case SelectionKind.Slot: Select(new Selection(SelectionKind.Section, sel.SectionId)); break;
            case SelectionKind.None: break;
            default: Selection = Selection.None; break;
        }
    }

    // ---------- images ----------

    public static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif" };

    public static bool IsImageFile(string path) => ImageExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    private async Task<ImageAsset?> ImportAsync(string path)
    {
        try
        {
            return await Task.Run(() => Session.ImportImage(path, Render.Probe));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Main.Toasts.Error(Loc.T("Could not read \"{0}\": {1}", Path.GetFileName(path), ex.Message));
            return null;
        }
    }

    private async Task<ImageAsset?> ImportAsync(byte[] data, string name)
    {
        try
        {
            return await Task.Run(() => Session.ImportImage(data, name, Render.Probe));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Main.Toasts.Error(Loc.T("The clipboard does not hold a readable image."));
            return null;
        }
    }

    /// <summary>"Click to browse": picks an image file for a part.</summary>
    public async Task BrowseImageAsync(string partId)
    {
        if (_browsing) return; // a double-click must not open two file pickers
        _browsing = true;
        try
        {
            string pageId = Session.CurrentPageId;
            var files = await Main.Dialogs.OpenImagesAsync(multiple: false);
            if (files.Count > 0 && !_disposed) await SetPartImageAsync(partId, files[0], pageId);
        }
        finally { _browsing = false; }
    }

    private bool _browsing;

    /// <param name="pageId">The page the part is on; defaults to the page shown when the call is made.</param>
    public async Task SetPartImageAsync(string partId, string path, string? pageId = null)
    {
        pageId ??= Session.CurrentPageId;
        using var busy = BeginBusy();
        var asset = await ImportAsync(path);
        if (asset == null || _disposed) return;
        if (EditPage(pageId, Loc.T("set image"), p => p.SetPartImage(partId, asset.Id)) && Session.CurrentPageId == pageId) SelectPart(partId);
    }

    /// <summary>
    /// Dropping files on a row adds one slot per file, labelled with the file name, where they were dropped
    /// (<paramref name="insertIndex"/>; null = at the end).
    /// </summary>
    public async Task AppendSlotsAsync(string sectionId, IReadOnlyList<string> paths, int? insertIndex = null)
    {
        if (CurrentPage.FindSection(sectionId) == null) return;
        string pageId = Session.CurrentPageId;
        using var busy = BeginBusy();
        var slots = new List<Slot>();
        foreach (string path in paths)
        {
            var asset = await ImportAsync(path);
            if (_disposed) return;
            if (asset == null) continue;
            slots.Add(new Slot { Label = Path.GetFileNameWithoutExtension(asset.FileName), Parts = ImmutableList.Create(new Part { ImageId = asset.Id }) });
        }
        if (slots.Count == 0) return;
        if (EditPage(pageId, Loc.T("add images"), p => p.UpdateSection(sectionId, s => s with { Slots = s.Slots.InsertRange(Math.Clamp(insertIndex ?? s.Slots.Count, 0, s.Slots.Count), slots) })) && Session.CurrentPageId == pageId)
            SelectSlot(slots[^1].Id);
    }

    /// <summary>Ctrl+V: pastes an image into the selected part, or as a new slot of the selected row.</summary>
    [RelayCommand]
    private async Task PasteImageAsync()
    {
        if (IsPreview) return;
        // The target is whatever is selected when the key is pressed, not when the image has finished loading.
        var page = CurrentPage;
        string pageId = page.Id;
        var sel = Selection;
        string? partId = sel.Kind == SelectionKind.Part ? sel.PartId
            : sel.Kind == SelectionKind.Slot ? page.FindSlot(sel.SlotId)?.Slot.Parts[0].Id
            : null;
        string sectionId = TargetSection().Id;
        string newSlotLabel = DefaultSlotLabel(page, sectionId);

        using var busy = BeginBusy();
        var clip = await Main.Dialogs.ReadClipboardImageAsync();
        if (_disposed) return;
        if (clip == null)
        {
            Main.Toasts.Show(Loc.T("The clipboard does not hold an image."));
            return;
        }
        var asset = clip.FilePath != null ? await ImportAsync(clip.FilePath) : await ImportAsync(clip.Data!, clip.Name);
        if (asset == null || _disposed) return;
        if (partId != null)
        {
            EditPage(pageId, Loc.T("paste image"), p => p.SetPartImage(partId, asset.Id));
        }
        else
        {
            var slot = new Slot { Label = newSlotLabel, Parts = ImmutableList.Create(new Part { ImageId = asset.Id }) };
            if (EditPage(pageId, Loc.T("paste image"), p => p.AddSlot(sectionId, slot)) && Session.CurrentPageId == pageId) SelectSlot(slot.Id);
        }
    }

    /// <summary>Library "replace file": every placement of the old asset now shows the new file (undoable).</summary>
    public async Task<bool> ReplaceAssetAsync(string oldId, string path)
    {
        using var busy = BeginBusy();
        var asset = await ImportAsync(path);
        if (asset == null || asset.Id == oldId || _disposed) return false;
        bool changed = Session.Apply(Loc.T("replace image"), p => p.ReplaceImage(oldId, asset.Id));
        if (!changed) Main.Toasts.Show(Loc.T("That image is not placed anywhere, so there was nothing to replace."));
        return changed;
    }

    // ---------- style ----------

    /// <summary>Changes the sheet style: the page's own style when it has an override, otherwise the project's.</summary>
    public void EditStyle(Func<SheetStyle, SheetStyle> change, string key)
    {
        string pageId = Session.CurrentPageId;
        Session.Apply(Loc.T("change style"), p =>
        {
            var page = p.FindPage(pageId);
            if (page?.Style != null) return p.UpdatePage(pageId, x => x with { Style = change(x.Style!) });
            var next = change(p.Style);
            return next == p.Style ? p : p with { Style = next };
        }, "style:" + key);
    }

    public void SetPageStyleOverride(bool enabled)
    {
        var projectStyle = Session.Project.Style;
        Edit(enabled ? Loc.T("use a page style") : Loc.T("use the project style"),
            p => enabled ? (p.Style != null ? p : p with { Style = projectStyle }) : (p.Style == null ? p : p with { Style = null }));
    }

    // ---------- pages ----------

    [RelayCommand]
    private void AddPage() => AddPage(null);

    public void AddPage(Page? template)
    {
        var page = (template ?? LoadDefaultTemplate() ?? Factory.NewPage(Settings.DefaultRowHeight)).CloneWithNewIds();
        int at = Session.Project.PageIndex(Session.CurrentPageId);
        Session.Apply(Loc.T("add page"), p => p.AddPage(page, at + 1), selectPageId: page.Id);
    }

    public void DuplicatePages(IReadOnlyCollection<string> ids)
    {
        var project = Session.Project;
        var sources = project.Pages.Where(p => ids.Contains(p.Id)).ToList();
        if (sources.Count == 0) return;
        var copies = sources.Select(p => p.CloneWithNewIds(Loc.T(" (copy)"))).ToList();
        int at = project.PageIndex(sources[^1].Id) + 1;
        Session.Apply(Loc.T("duplicate page"), p => p with { Pages = p.Pages.InsertRange(at, copies) }, selectPageId: copies[0].Id);
    }

    public async Task DeletePagesAsync(IReadOnlyCollection<string> ids)
    {
        var pages = Session.Project.Pages.Where(p => ids.Contains(p.Id)).ToList();
        if (pages.Count == 0) return;
        string message = pages.Count == 1
            ? Loc.T("Delete \"{0}\"?", string.IsNullOrWhiteSpace(pages[0].Name) ? Loc.T("Untitled") : pages[0].Name)
            : Loc.T("Delete {0} pages?", pages.Count);
        if (!await Main.Dialogs.ConfirmAsync(Loc.T("Delete page"), message, Loc.T("Delete"))) return;
        double rowHeight = Settings.DefaultRowHeight;
        Session.Apply(Loc.T("delete page"), p => p.RemovePages(ids, () => Factory.NewPage(rowHeight)));
    }

    /// <summary>Moves the given pages (keeping their order) so that they start at <paramref name="insertIndex"/> of the current list.</summary>
    public void MovePages(IReadOnlyCollection<string> ids, int insertIndex)
    {
        Session.Apply(Loc.T("reorder pages"), p =>
        {
            var moving = p.Pages.Where(x => ids.Contains(x.Id)).ToList();
            if (moving.Count == 0) return p;
            int before = p.Pages.Take(Math.Clamp(insertIndex, 0, p.Pages.Count)).Count(x => !ids.Contains(x.Id));
            var rest = p.Pages.RemoveAll(x => ids.Contains(x.Id));
            var result = rest.InsertRange(before, moving);
            return result.SequenceEqual(p.Pages) ? p : p with { Pages = result };
        });
    }

    public void MovePage(string pageId, int delta)
    {
        int at = Session.Project.PageIndex(pageId);
        if (at >= 0) Session.Apply(Loc.T("reorder pages"), p => p.MovePage(at, at + delta));
    }

    public async Task RenamePageAsync(string pageId)
    {
        var page = Session.Project.FindPage(pageId);
        if (page == null) return;
        string? name = await Main.Dialogs.PromptAsync(Loc.T("Rename page"), Loc.T("Species name"), page.Name);
        if (name == null) return;
        Session.Apply(Loc.T("rename species"), p => p.UpdatePage(pageId, x => x with { Name = name }));
    }

    public void SetProjectTitle(string title) =>
        Session.Apply(Loc.T("rename project"), p => p.Title == title ? p : p with { Title = title }, "project-title");
}
