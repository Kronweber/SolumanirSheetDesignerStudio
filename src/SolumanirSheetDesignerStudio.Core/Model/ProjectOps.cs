using System.Collections.Immutable;

namespace SolumanirSheetDesignerStudio.Core.Model;

/// <summary>Pure edit functions over the immutable model. Every method returns the same instance when nothing changed.</summary>
public static class ProjectOps
{
    // ---------- generic list helpers ----------

    public static ImmutableList<T> ReplaceWhere<T>(this ImmutableList<T> list, Predicate<T> match, Func<T, T> f) where T : class
    {
        int i = list.FindIndex(match);
        if (i < 0) return list;
        var old = list[i];
        var updated = f(old);
        return ReferenceEquals(old, updated) ? list : list.SetItem(i, updated);
    }

    /// <summary>Moves the item at <paramref name="from"/> so that it ends up at index <paramref name="to"/>.</summary>
    public static ImmutableList<T> Move<T>(this ImmutableList<T> list, int from, int to)
    {
        if (from < 0 || from >= list.Count) return list;
        to = Math.Clamp(to, 0, list.Count - 1);
        if (from == to) return list;
        var item = list[from];
        return list.RemoveAt(from).Insert(to, item);
    }

    private static ImmutableList<T> InsertAt<T>(this ImmutableList<T> list, int? index, T item) =>
        index is { } i ? list.Insert(Math.Clamp(i, 0, list.Count), item) : list.Add(item);

    // ---------- lookup ----------

    public static Page? FindPage(this Project p, string? id) => id == null ? null : p.Pages.Find(x => x.Id == id);
    public static int PageIndex(this Project p, string? id) => id == null ? -1 : p.Pages.FindIndex(x => x.Id == id);

    public static IEnumerable<Section> AllSections(this Page pg)
    {
        foreach (var r in pg.Rows) yield return r;
        yield return pg.Details;
    }

    public static Section? FindSection(this Page pg, string? id) =>
        id == null ? null : pg.Details.Id == id ? pg.Details : pg.Rows.Find(r => r.Id == id);

    public static bool IsDetails(this Page pg, string? sectionId) => sectionId != null && pg.Details.Id == sectionId;

    public static (Section Section, Slot Slot, int Index)? FindSlot(this Page pg, string? slotId)
    {
        if (slotId == null) return null;
        foreach (var s in pg.AllSections())
        {
            int i = s.Slots.FindIndex(x => x.Id == slotId);
            if (i >= 0) return (s, s.Slots[i], i);
        }
        return null;
    }

    public static (Section Section, Slot Slot, Part Part, int Index)? FindPart(this Page pg, string? partId)
    {
        if (partId == null) return null;
        foreach (var s in pg.AllSections())
            foreach (var slot in s.Slots)
            {
                int i = slot.Parts.FindIndex(x => x.Id == partId);
                if (i >= 0) return (s, slot, slot.Parts[i], i);
            }
        return null;
    }

    public static LoreSection? FindLore(this Page pg, string? id) => id == null ? null : pg.Lore.Find(x => x.Id == id);

    // ---------- nested updates ----------

    public static Project UpdatePage(this Project p, string pageId, Func<Page, Page> f)
    {
        var pages = p.Pages.ReplaceWhere(x => x.Id == pageId, f);
        return ReferenceEquals(pages, p.Pages) ? p : p with { Pages = pages };
    }

    public static Page UpdateSection(this Page pg, string sectionId, Func<Section, Section> f)
    {
        if (pg.Details.Id == sectionId)
        {
            var d = f(pg.Details);
            return ReferenceEquals(d, pg.Details) ? pg : pg with { Details = d };
        }
        var rows = pg.Rows.ReplaceWhere(x => x.Id == sectionId, f);
        return ReferenceEquals(rows, pg.Rows) ? pg : pg with { Rows = rows };
    }

    public static Section UpdateSlot(this Section s, string slotId, Func<Slot, Slot> f)
    {
        var slots = s.Slots.ReplaceWhere(x => x.Id == slotId, f);
        return ReferenceEquals(slots, s.Slots) ? s : s with { Slots = slots };
    }

    public static Page UpdateSlot(this Page pg, string slotId, Func<Slot, Slot> f)
    {
        var found = pg.FindSlot(slotId);
        return found == null ? pg : pg.UpdateSection(found.Value.Section.Id, s => s.UpdateSlot(slotId, f));
    }

    public static Slot UpdatePart(this Slot slot, string partId, Func<Part, Part> f)
    {
        var parts = slot.Parts.ReplaceWhere(x => x.Id == partId, f);
        return ReferenceEquals(parts, slot.Parts) ? slot : slot with { Parts = parts };
    }

    public static Page UpdatePart(this Page pg, string partId, Func<Part, Part> f)
    {
        var found = pg.FindPart(partId);
        return found == null ? pg : pg.UpdateSlot(found.Value.Slot.Id, s => s.UpdatePart(partId, f));
    }

    public static Page UpdateLore(this Page pg, string loreId, Func<LoreSection, LoreSection> f)
    {
        var lore = pg.Lore.ReplaceWhere(x => x.Id == loreId, f);
        return ReferenceEquals(lore, pg.Lore) ? pg : pg with { Lore = lore };
    }

    // ---------- pages ----------

    public static Project AddPage(this Project p, Page page, int? index = null) => p with { Pages = p.Pages.InsertAt(index, page) };

    public static Project RemovePages(this Project p, IReadOnlyCollection<string> ids, Func<Page> blankFactory)
    {
        var pages = p.Pages.RemoveAll(x => ids.Contains(x.Id));
        if (pages.Count == p.Pages.Count) return p;
        if (pages.Count == 0) pages = pages.Add(blankFactory());
        return p with { Pages = pages };
    }

    public static Project MovePage(this Project p, int from, int to)
    {
        var pages = p.Pages.Move(from, to);
        return ReferenceEquals(pages, p.Pages) ? p : p with { Pages = pages };
    }

    public static Page CloneWithNewIds(this Page pg, string? nameSuffix = null) => pg with
    {
        Id = Ids.New(),
        Name = nameSuffix == null ? pg.Name : pg.Name + nameSuffix,
        Rows = pg.Rows.Select(CloneWithNewIds).ToImmutableList(),
        Details = pg.Details.CloneWithNewIds(),
        Lore = pg.Lore.Select(l => l with { Id = Ids.New() }).ToImmutableList(),
    };

    public static Section CloneWithNewIds(this Section s) => s with
    {
        Id = Ids.New(),
        Slots = s.Slots.Select(CloneWithNewIds).ToImmutableList(),
    };

    public static Slot CloneWithNewIds(this Slot s) => s with
    {
        Id = Ids.New(),
        Parts = s.Parts.Select(x => x with { Id = Ids.New() }).ToImmutableList(),
    };

    /// <summary>Copy of the page with every image reference removed (used for templates).</summary>
    public static Page WithoutImages(this Page pg)
    {
        static Section Strip(Section s) => s with
        {
            Slots = s.Slots.Select(sl => sl with { Parts = sl.Parts.Select(x => x with { ImageId = null }).ToImmutableList() }).ToImmutableList(),
        };
        return pg with { Rows = pg.Rows.Select(Strip).ToImmutableList(), Details = Strip(pg.Details) };
    }

    // ---------- rows ----------

    public static Page AddRow(this Page pg, Section row, int? index = null) => pg with { Rows = pg.Rows.InsertAt(index, row) };

    public static Page RemoveRow(this Page pg, string rowId)
    {
        var rows = pg.Rows.RemoveAll(r => r.Id == rowId);
        return rows.Count == pg.Rows.Count ? pg : pg with { Rows = rows };
    }

    public static Page MoveRow(this Page pg, int from, int to)
    {
        var rows = pg.Rows.Move(from, to);
        return ReferenceEquals(rows, pg.Rows) ? pg : pg with { Rows = rows };
    }

    // ---------- slots ----------

    public static Page AddSlot(this Page pg, string sectionId, Slot slot, int? index = null) =>
        pg.UpdateSection(sectionId, s => s with { Slots = s.Slots.InsertAt(index, slot) });

    public static Page RemoveSlot(this Page pg, string slotId)
    {
        var found = pg.FindSlot(slotId);
        return found == null ? pg : pg.UpdateSection(found.Value.Section.Id, s => s with { Slots = s.Slots.RemoveAt(found.Value.Index) });
    }

    /// <summary>
    /// Moves a slot to <paramref name="insertIndex"/> of the target section. The index is an insertion position in the
    /// target's current slot list (0..Count), i.e. "before the slot that is currently at that index".
    /// </summary>
    public static Page MoveSlot(this Page pg, string slotId, string targetSectionId, int insertIndex)
    {
        var found = pg.FindSlot(slotId);
        var target = pg.FindSection(targetSectionId);
        if (found == null || target == null) return pg;
        var (src, slot, from) = found.Value;
        if (src.Id == target.Id)
        {
            int to = insertIndex > from ? insertIndex - 1 : insertIndex;
            to = Math.Clamp(to, 0, src.Slots.Count - 1);
            return to == from ? pg : pg.UpdateSection(src.Id, s => s with { Slots = s.Slots.Move(from, to) });
        }
        var removed = pg.UpdateSection(src.Id, s => s with { Slots = s.Slots.RemoveAt(from) });
        return removed.UpdateSection(target.Id, s => s with { Slots = s.Slots.Insert(Math.Clamp(insertIndex, 0, s.Slots.Count), slot) });
    }

    // ---------- parts ----------

    public static Page SplitSlot(this Page pg, string slotId, Part? newPart = null) =>
        pg.UpdateSlot(slotId, s => s with { Parts = s.Parts.Add(newPart ?? new Part()) });

    /// <summary>Removes a part; a slot always keeps at least one part.</summary>
    public static Page RemovePart(this Page pg, string partId)
    {
        var found = pg.FindPart(partId);
        if (found == null || found.Value.Slot.Parts.Count <= 1) return pg;
        return pg.UpdateSlot(found.Value.Slot.Id, s => s with { Parts = s.Parts.RemoveAt(found.Value.Index) });
    }

    public static Page MovePart(this Page pg, string partId, int to)
    {
        var found = pg.FindPart(partId);
        if (found == null) return pg;
        return pg.UpdateSlot(found.Value.Slot.Id, s =>
        {
            var parts = s.Parts.Move(found.Value.Index, to);
            return ReferenceEquals(parts, s.Parts) ? s : s with { Parts = parts };
        });
    }

    public static Page SetPartImage(this Page pg, string partId, string? imageId) =>
        pg.UpdatePart(partId, x => x.ImageId == imageId ? x : x with { ImageId = imageId });

    // ---------- lore ----------

    public static Page AddLore(this Page pg, LoreSection section, int? index = null) => pg with { Lore = pg.Lore.InsertAt(index, section) };

    public static Page RemoveLore(this Page pg, string loreId)
    {
        var lore = pg.Lore.RemoveAll(l => l.Id == loreId);
        return lore.Count == pg.Lore.Count ? pg : pg with { Lore = lore };
    }

    public static Page MoveLore(this Page pg, int from, int to)
    {
        var lore = pg.Lore.Move(from, to);
        return ReferenceEquals(lore, pg.Lore) ? pg : pg with { Lore = lore };
    }

    // ---------- images ----------

    public static IEnumerable<string> ImageIds(this Page pg) =>
        from s in pg.AllSections() from slot in s.Slots from part in slot.Parts where part.ImageId != null select part.ImageId!;

    public static HashSet<string> ReferencedImageIds(this Project p)
    {
        var set = new HashSet<string>();
        foreach (var pg in p.Pages) foreach (var id in pg.ImageIds()) set.Add(id);
        return set;
    }

    public static Dictionary<string, int> ImageUsage(this Project p)
    {
        var d = new Dictionary<string, int>();
        foreach (var pg in p.Pages) foreach (var id in pg.ImageIds()) d[id] = d.GetValueOrDefault(id) + 1;
        return d;
    }

    /// <summary>Rewrites every reference to <paramref name="oldId"/> (null <paramref name="newId"/> clears them).</summary>
    public static Project ReplaceImage(this Project p, string oldId, string? newId)
    {
        Section Fix(Section s)
        {
            if (!s.Slots.Any(sl => sl.Parts.Any(x => x.ImageId == oldId))) return s;
            return s with
            {
                Slots = s.Slots.Select(sl => sl.Parts.Any(x => x.ImageId == oldId)
                    ? sl with { Parts = sl.Parts.Select(x => x.ImageId == oldId ? x with { ImageId = newId } : x).ToImmutableList() }
                    : sl).ToImmutableList(),
            };
        }

        var pages = p.Pages;
        for (int i = 0; i < pages.Count; i++)
        {
            var pg = pages[i];
            var rows = pg.Rows;
            for (int r = 0; r < rows.Count; r++)
            {
                var fixedRow = Fix(rows[r]);
                if (!ReferenceEquals(fixedRow, rows[r])) rows = rows.SetItem(r, fixedRow);
            }
            var details = Fix(pg.Details);
            if (!ReferenceEquals(rows, pg.Rows) || !ReferenceEquals(details, pg.Details))
                pages = pages.SetItem(i, pg with { Rows = rows, Details = details });
        }
        return ReferenceEquals(pages, p.Pages) ? p : p with { Pages = pages };
    }

    // ---------- load-time repair ----------

    /// <summary>
    /// Repairs anything a hand-edited or damaged file could leave null, empty or duplicated. Ids end up unique
    /// (pages across the project, everything else within its page), which the editor relies on.
    /// </summary>
    public static Project Normalize(Project? project)
    {
        project ??= new Project();
        var pageIds = new HashSet<string>(StringComparer.Ordinal);
        var pages = (project.Pages ?? ImmutableList<Page>.Empty).Where(p => p != null)
            .Select(p => NormalizePage(p) is var n && pageIds.Add(n.Id) ? n : NormalizePage(p with { Id = Ids.New() })).ToImmutableList();
        if (pages.Count == 0) pages = pages.Add(Factory.NewPage());
        return project with { Title = project.Title ?? "", Style = NormalizeStyle(project.Style) ?? SheetStyle.Default, Pages = pages };
    }

    // A number too large for a double reads as infinity, which cannot be written back. Such values return to their defaults.

    private static SheetStyle? NormalizeStyle(SheetStyle? style) =>
        style == null || double.IsFinite(style.LoreWidth) ? style : style with { LoreWidth = SheetStyle.Default.LoreWidth };

    private static Sizing NormalizeSizing(Sizing? sizing)
    {
        sizing ??= new Sizing();
        return sizing with
        {
            Height = double.IsFinite(sizing.Height) ? sizing.Height : Sizing.DefaultHeight,
            MaxHeight = sizing.MaxHeight is { } heightMax && double.IsFinite(heightMax) ? heightMax : null,
            ExportPx = sizing.ExportPx is { } heightExportPx && double.IsFinite(heightExportPx) ? heightExportPx : null,
        };
    }

    public static Page NormalizePage(Page pg)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        string Unique(string? id) => !string.IsNullOrEmpty(id) && used.Add(id) ? id : NewUnique();
        string NewUnique()
        {
            string id;
            do id = Ids.New(); while (!used.Add(id));
            return id;
        }

        Section Fix(Section s) => s with
        {
            Id = Unique(s.Id),
            Name = s.Name ?? "",
            Sizing = NormalizeSizing(s.Sizing),
            Slots = (s.Slots ?? ImmutableList<Slot>.Empty).Where(x => x != null).Select(slot =>
            {
                var parts = (slot.Parts ?? ImmutableList<Part>.Empty).Where(x => x != null)
                    .Select(x => Unique(x.Id) is var id && id == x.Id && double.IsFinite(x.Scale) ? x : x with { Id = id, Scale = double.IsFinite(x.Scale) ? x.Scale : 100 }).ToImmutableList();
                if (parts.Count == 0) parts = parts.Add(new Part { Id = NewUnique() });
                return slot with { Id = Unique(slot.Id), Label = slot.Label ?? "", Parts = parts };
            }).ToImmutableList(),
        };

        return pg with
        {
            Id = string.IsNullOrEmpty(pg.Id) ? Ids.New() : pg.Id,
            Name = pg.Name ?? "",
            Subtitle = pg.Subtitle ?? "",
            Rows = (pg.Rows ?? ImmutableList<Section>.Empty).Where(r => r != null).Select(Fix).ToImmutableList(),
            Details = Fix(pg.Details ?? Factory.NewDetails()),
            Lore = (pg.Lore ?? ImmutableList<LoreSection>.Empty).Where(l => l != null)
                .Select(l => l with { Id = Unique(l.Id), Title = l.Title ?? "", Body = l.Body ?? "" }).ToImmutableList(),
            Style = NormalizeStyle(pg.Style),
        };
    }
}
