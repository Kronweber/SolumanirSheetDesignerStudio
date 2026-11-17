using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SolumanirSheetDesignerStudio.Core.Assets;
using SolumanirSheetDesignerStudio.Core.Export;
using SolumanirSheetDesignerStudio.Core.Model;

namespace SolumanirSheetDesignerStudio.Core.IO;

public sealed class LoadedProject
{
    public required Project Project { get; init; }
    public string? CurrentPageId { get; init; }
    public double? Zoom { get; init; }
    public ExportOptions? LastExport { get; init; }
    /// <summary>Assets with lazy handles into the project file.</summary>
    public List<ImageAsset> Assets { get; } = new();
    /// <summary>Image entries that are compressed inside the ZIP (written by another tool) and need extracting before use.</summary>
    public List<(ImageMeta Meta, string EntryName)> CompressedImages { get; } = new();
    public Dictionary<string, byte[]> Thumbs { get; } = new();
    public bool IsLegacy { get; init; }
    public List<string> Warnings { get; } = new();
}

public sealed class SaveRequest
{
    public required string Path { get; init; }
    public required Project Project { get; init; }
    public string? CurrentPageId { get; init; }
    public double? Zoom { get; init; }
    public ExportOptions? LastExport { get; init; }
    public required AssetLibrary Assets { get; init; }
    public IReadOnlyDictionary<string, byte[]>? Thumbs { get; init; }
    /// <summary>True for Save / Save As (assets are re-pointed at the new file); false for "Save a Copy".</summary>
    public bool Relocate { get; init; } = true;
    /// <summary>Folder that receives assets which are not written (unreferenced) but still live in the file being replaced.</summary>
    public string? KeepAliveDir { get; init; }
}

public sealed record SaveResult(long FileBytes, int ImageCount, List<string> Warnings);

/// <summary>Reads and writes <c>.speciesproj</c> files: the native v2 ZIP container and the prototype's v1 blob (read-only).</summary>
public static class ProjectFile
{
    private const string JsonEntry = "project.json";
    private const string ImagesPrefix = "images/";
    private const string ThumbsPrefix = "thumbs/";
    private const int MaxThumbBytes = 4 << 20;
    /// <summary>Far above any real project's JSON; stops a corrupt length field from asking for gigabytes.</summary>
    private const long MaxJsonBytes = 512L << 20;
    private static readonly byte[] LegacyMagic = Encoding.ASCII.GetBytes("SPSHEET1");

    public static bool LooksLikeProject(string path) =>
        string.Equals(System.IO.Path.GetExtension(path), AppInfo.ProjectExtension, StringComparison.OrdinalIgnoreCase);

    // =====================================================================================
    // Load
    // =====================================================================================

    public static LoadedProject Load(string path)
    {
        path = System.IO.Path.GetFullPath(path);
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
        var magic = new byte[8];
        int got = fs.ReadAtLeast(magic, 8, throwOnEndOfStream: false);
        if (got == 8 && magic.AsSpan().SequenceEqual(LegacyMagic)) return LoadLegacy(fs, path);
        if (got >= 4 && magic[0] == (byte)'P' && magic[1] == (byte)'K') return LoadV2(fs, path);
        throw new InvalidProjectException("That file is not a valid species project.");
    }

    private static LoadedProject LoadV2(FileStream fs, string path)
    {
        ProjectDocument doc;
        var thumbs = new Dictionary<string, byte[]>();
        try
        {
            fs.Position = 0;
            using var zip = new ZipArchive(fs, ZipArchiveMode.Read, leaveOpen: true);
            var jsonEntry = zip.GetEntry(JsonEntry) ?? throw new InvalidProjectException("The project file has no project.json.");
            using (var js = jsonEntry.Open()) doc = ProjectJson.Deserialize(js);
            foreach (var e in zip.Entries)
            {
                if (!e.FullName.StartsWith(ThumbsPrefix, StringComparison.Ordinal) || e.Length is <= 0 or > MaxThumbBytes) continue;
                try
                {
                    using var ts = e.Open();
                    using var ms = new MemoryStream((int)e.Length);
                    ts.CopyTo(ms);
                    thumbs[System.IO.Path.GetFileNameWithoutExtension(e.Name)] = ms.ToArray();
                }
                catch (Exception ex) when (ex is InvalidDataException or IOException)
                {
                    // Thumbnails are a cache: a damaged one is simply rendered again.
                }
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or EndOfStreamException)
        {
            throw new InvalidProjectException("The project file is damaged: " + ex.Message, ex);
        }

        var result = new LoadedProject
        {
            Project = ProjectJson.ToProject(doc.Project),
            CurrentPageId = doc.Project.CurrentPageId,
            Zoom = doc.Project.Zoom,
            LastExport = doc.Project.LastExport,
        };
        foreach (var kv in thumbs) result.Thumbs[kv.Key] = kv.Value;

        Dictionary<string, ZipEntryInfo> index;
        try
        {
            index = new Dictionary<string, ZipEntryInfo>(StringComparer.Ordinal);
            foreach (var e in ZipIndex.Read(fs))
                if (e.Name.StartsWith(ImagesPrefix, StringComparison.Ordinal))
                    index[System.IO.Path.GetFileNameWithoutExtension(e.Name)] = e;
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException)
        {
            throw new InvalidProjectException("The project file is damaged: " + ex.Message, ex);
        }

        foreach (var meta in doc.Images)
        {
            if (!index.TryGetValue(meta.Id, out var entry))
            {
                result.Warnings.Add($"Image \"{meta.FileName}\" is missing from the project file.");
                continue;
            }
            if (entry.IsStored && entry.DataOffset >= 0 && entry.UncompressedSize > 0)
                result.Assets.Add(meta.ToAsset(new AssetSource(path, entry.DataOffset, entry.UncompressedSize)));
            else
                result.CompressedImages.Add((meta, entry.Name));
        }
        return result;
    }

    /// <summary>Extracts a compressed image entry to a standalone file (only needed for archives re-packed by other tools).</summary>
    public static ImageAsset ExtractCompressedImage(string projectPath, ImageMeta meta, string entryName, string targetDir)
    {
        Directory.CreateDirectory(targetDir);
        using var zip = ZipFile.OpenRead(projectPath);
        var entry = zip.GetEntry(entryName) ?? throw new IOException("Image entry not found: " + entryName);
        string target = System.IO.Path.Combine(targetDir, System.IO.Path.GetFileName(entryName));
        using (var src = entry.Open())
        using (var dst = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
            src.CopyTo(dst, 1 << 20);
        return meta.ToAsset(new AssetSource(target, 0, new FileInfo(target).Length));
    }

    // ---------- legacy v1 (HTML prototype) ----------

    private static LoadedProject LoadLegacy(FileStream fs, string path)
    {
        Span<byte> lenBytes = stackalloc byte[4];
        fs.Position = 8;
        bool hasLength = fs.ReadAtLeast(lenBytes, 4, throwOnEndOfStream: false) == 4;
        uint jsonLen = BinaryPrimitives.ReadUInt32BigEndian(lenBytes);
        if (!hasLength || 12 + (long)jsonLen > fs.Length || jsonLen > MaxJsonBytes) throw new InvalidProjectException("The legacy project file is truncated or damaged.");
        var json = new byte[jsonLen];
        fs.ReadExactly(json);

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }); }
        catch (JsonException ex) { throw new InvalidProjectException("The legacy project data is corrupt: " + ex.Message, ex); }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("project", out var pr) || pr.ValueKind != JsonValueKind.Object)
                throw new InvalidProjectException("The legacy project data has no project block.");

            var pages = ImmutableList.CreateBuilder<Page>();
            if (pr.TryGetProperty("pages", out var pagesEl) && pagesEl.ValueKind == JsonValueKind.Array)
                foreach (var pg in pagesEl.EnumerateArray())
                    if (pg.ValueKind == JsonValueKind.Object) pages.Add(LegacyPage(pg));

            var result = new LoadedProject
            {
                // v1 has no style block: the default style applies.
                Project = ProjectOps.Normalize(new Project { Title = Str(pr, "title") ?? "My World", Style = SheetStyle.Default, Pages = pages.ToImmutable() }),
                CurrentPageId = Str(pr, "current"),
                IsLegacy = true,
            };

            long offset = 12 + (long)jsonLen;
            if (root.TryGetProperty("images", out var imgs) && imgs.ValueKind == JsonValueKind.Array)
            {
                foreach (var m in imgs.EnumerateArray())
                {
                    if (m.ValueKind != JsonValueKind.Object) continue;
                    long size = (long)(Num(m, "size") ?? 0);
                    string? id = Str(m, "id");
                    string name = Str(m, "name") ?? "image";
                    if (size < 0 || offset + size > fs.Length)
                    {
                        result.Warnings.Add($"Image \"{name}\" is truncated in the legacy file.");
                        break; // every later offset is unreliable too
                    }
                    // The id names the image's files in the working folder and in the saved project.
                    if (!string.IsNullOrEmpty(id) && FileNaming.Sanitize(id, "") != id)
                        result.Warnings.Add($"Image \"{name}\" has an id that cannot be used and was left out.");
                    else if (!string.IsNullOrEmpty(id) && size > 0)
                    {
                        result.Assets.Add(new ImageAsset
                        {
                            Id = id,
                            FileName = name,
                            MimeType = Str(m, "type") is { Length: > 0 } t ? t : ImageFormats.MimeForFile(name),
                            PixelWidth = (int)Math.Clamp(Num(m, "w") ?? 0, 0, int.MaxValue),
                            PixelHeight = (int)Math.Clamp(Num(m, "h") ?? 0, 0, int.MaxValue),
                            ByteLength = size,
                            Source = new AssetSource(path, offset, size),
                        });
                    }
                    offset += size;
                }
            }
            return result;
        }
    }

    private static Page LegacyPage(JsonElement pg)
    {
        string pageId = Str(pg, "id") ?? Ids.New();
        var rows = ImmutableList.CreateBuilder<Section>();
        if (pg.TryGetProperty("rows", out var rowsEl) && rowsEl.ValueKind == JsonValueKind.Array)
            foreach (var r in rowsEl.EnumerateArray())
                if (r.ValueKind == JsonValueKind.Object)
                    rows.Add(new Section
                    {
                        Id = Str(r, "id") ?? Ids.New(),
                        Name = Str(r, "name") ?? "",
                        Sizing = LegacySizing(r),
                        Slots = LegacySlots(r, "slots"),
                    });

        // Details: either the newer det{mode,h,maxH,renderH} block or the older detailH number. A missing height
        // falls back to the row default in both cases, exactly as the prototype's cfg() does.
        Sizing detSizing = pg.TryGetProperty("det", out var det) && det.ValueKind == JsonValueKind.Object
            ? LegacySizing(det)
            : new Sizing { Height = Pos(Num(pg, "detailH")) ?? Sizing.DefaultHeight };

        var lore = ImmutableList.CreateBuilder<LoreSection>();
        if (pg.TryGetProperty("lore", out var loreEl) && loreEl.ValueKind == JsonValueKind.Array)
            foreach (var l in loreEl.EnumerateArray())
                if (l.ValueKind == JsonValueKind.Object)
                    lore.Add(new LoreSection { Id = Str(l, "id") ?? Ids.New(), Title = Str(l, "title") ?? "", Body = Str(l, "body") ?? "" });

        return new Page
        {
            Id = pageId,
            Name = Str(pg, "name") ?? "",
            Subtitle = Str(pg, "subtitle") ?? "",
            Rows = rows.ToImmutable(),
            Details = new Section
            {
                Id = pageId + "d",
                Name = Str(pg, "detailsTitle") ?? Factory.NewDetails().Name,
                Sizing = detSizing,
                Slots = LegacySlots(pg, "details"),
            },
            Lore = lore.ToImmutable(),
        };
    }

    private static Sizing LegacySizing(JsonElement e) => new()
    {
        Mode = string.Equals(Str(e, "mode"), "auto", StringComparison.OrdinalIgnoreCase) ? SizingMode.Auto : SizingMode.Fixed,
        Height = Pos(Num(e, "h")) ?? Sizing.DefaultHeight,
        MaxHeight = Pos(Num(e, "maxH")),
        ExportPx = Pos(Num(e, "renderH")),
    };

    private static double? Pos(double? v) => v is { } d && d > 0 ? d : null;

    private static ImmutableList<Slot> LegacySlots(JsonElement owner, string property)
    {
        var slots = ImmutableList.CreateBuilder<Slot>();
        if (!owner.TryGetProperty(property, out var arr) || arr.ValueKind != JsonValueKind.Array) return slots.ToImmutable();
        foreach (var s in arr.EnumerateArray())
        {
            if (s.ValueKind != JsonValueKind.Object) continue;
            string slotId = Str(s, "id") ?? Ids.New();
            var parts = ImmutableList.CreateBuilder<Part>();
            if (s.TryGetProperty("parts", out var partsEl) && partsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var p in partsEl.EnumerateArray())
                    if (p.ValueKind == JsonValueKind.Object)
                        parts.Add(new Part { Id = Str(p, "id") ?? Ids.New(), ImageId = Str(p, "img"), Scale = Num(p, "scale") ?? 100 });
            }
            if (parts.Count == 0)
                parts.Add(new Part { Id = slotId + "p", ImageId = Str(s, "img"), Scale = Num(s, "scale") ?? 100 }); // single-image legacy slot
            slots.Add(new Slot { Id = slotId, Label = Str(s, "label") ?? "", Parts = parts.ToImmutable() });
        }
        return slots.ToImmutable();
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>The prototype stored cleared number boxes as "" — accept numbers, numeric strings, or nothing.</summary>
    private static double? Num(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out double d)) return double.IsFinite(d) ? d : null;
        if (v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out d))
            return double.IsFinite(d) ? d : null;
        return null;
    }

    // =====================================================================================
    // Save
    // =====================================================================================

    /// <summary>
    /// Writes a v2 project. Image entries are streamed one at a time (Stored, never recompressed) and checked against
    /// their recorded size and SHA-256 on the way; the file is written next to the target as <c>*.tmp</c>, flushed, and
    /// only then swapped in. It is all or nothing: if any image cannot be read back intact the save fails and the
    /// previous file stays as it was.
    /// </summary>
    public static SaveResult Save(SaveRequest req, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        string path = System.IO.Path.GetFullPath(req.Path);
        string tmp = path + ".tmp";
        var warnings = new List<string>();
        var assets = req.Assets;

        if (!req.Relocate && assets.All.Any(a => PathUtil.Same(a.Source.Path, path)))
            throw new IOException("A copy cannot replace the file the open project is stored in. Use Save instead.");

        // Ids without a library entry (already reported when the project was opened) keep their place on the sheet.
        var referenced = req.Project.ReferencedImageIds().OrderBy(x => x, StringComparer.Ordinal)
            .Select(assets.Get).Where(a => a != null).Select(a => a!).ToList();

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        try
        {
            var written = WriteArchive(tmp, req, referenced, progress, ct);
            ct.ThrowIfCancellationRequested();
            long size = new FileInfo(tmp).Length;

            if (req.Relocate) SwapInAndRelocate(tmp, path, req, written, warnings);
            else File.Move(tmp, path, overwrite: true);

            progress?.Report(1);
            return new SaveResult(size, written.Count, warnings);
        }
        catch
        {
            PathUtil.TryDelete(tmp);
            throw;
        }
    }

    /// <summary>Writes the complete archive to <paramref name="tmp"/> and returns the image assets it contains (with their hashes).</summary>
    private static List<ImageAsset> WriteArchive(string tmp, SaveRequest req, List<ImageAsset> referenced, IProgress<double>? progress, CancellationToken ct)
    {
        var written = new List<ImageAsset>(referenced.Count);
        long totalBytes = Math.Max(1, referenced.Sum(a => a.ByteLength));
        long done = 0;
        var buffer = new byte[1 << 20];

        using var fs = new FileStream(tmp, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 20);
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create, leaveOpen: true, Encoding.UTF8))
        using (var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            foreach (var asset in referenced)
            {
                ct.ThrowIfCancellationRequested();
                long copied = 0;
                try
                {
                    using var src = req.Assets.OpenRead(asset);
                    var entry = zip.CreateEntry(ImagesPrefix + asset.Id + asset.Extension, CompressionLevel.NoCompression);
                    using var dst = entry.Open();
                    int n;
                    while ((n = src.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        sha.AppendData(buffer, 0, n);
                        dst.Write(buffer, 0, n);
                        copied += n;
                        progress?.Report(Math.Min(0.98, (done + copied) / (double)totalBytes));
                    }
                }
                catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException)
                {
                    throw new IOException($"The project was not saved: image \"{asset.FileName}\" could not be read ({ex.Message}).", ex);
                }
                done += copied;

                string hash = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
                if (copied != asset.ByteLength || (asset.Sha256 != null && !string.Equals(asset.Sha256, hash, StringComparison.OrdinalIgnoreCase)))
                    throw new IOException($"The project was not saved: the stored data of image \"{asset.FileName}\" has changed or is damaged. Replace or remove that image, then save again.");
                written.Add(asset.Sha256 == null ? asset with { Sha256 = hash } : asset);
            }

            if (req.Thumbs != null)
            {
                var pageIds = req.Project.Pages.Select(p => p.Id).ToHashSet();
                foreach (var (pageId, png) in req.Thumbs)
                {
                    if (!pageIds.Contains(pageId) || png.Length is 0 or > MaxThumbBytes) continue;
                    var entry = zip.CreateEntry(ThumbsPrefix + pageId + ".png", CompressionLevel.NoCompression);
                    using var dst = entry.Open();
                    dst.Write(png, 0, png.Length);
                }
            }

            var doc = ProjectJson.BuildDocument(req.Project, req.CurrentPageId, req.Zoom, req.LastExport, written);
            var jsonEntry = zip.CreateEntry(JsonEntry, CompressionLevel.Optimal);
            using var js = jsonEntry.Open();
            byte[] bytes = ProjectJson.Serialize(doc);
            js.Write(bytes, 0, bytes.Length);
        }
        fs.Flush(flushToDisk: true);
        return written;
    }

    /// <summary>
    /// Save / Save As: replaces <paramref name="path"/> with the finished <paramref name="tmp"/> and re-points the
    /// library at it. Everything that can fail is done before the swap; after it only in-memory records change.
    /// </summary>
    private static void SwapInAndRelocate(string tmp, string path, SaveRequest req, List<ImageAsset> written, List<string> warnings)
    {
        var assets = req.Assets;

        // Where each image sits in the new file. Offsets are the same before and after the rename.
        var index = ZipIndex.Read(tmp).Where(e => e.Name.StartsWith(ImagesPrefix, StringComparison.Ordinal))
            .ToDictionary(e => System.IO.Path.GetFileNameWithoutExtension(e.Name), StringComparer.Ordinal);
        foreach (var a in written)
            if (!index.TryGetValue(a.Id, out var e) || !e.IsStored || e.DataOffset < 0 || e.UncompressedSize != a.ByteLength)
                throw new IOException($"The project was not saved: image \"{a.FileName}\" could not be verified in the new file.");

        // Assets that are not in the new file but still live in the file about to be replaced (unused now, yet
        // reachable through undo or the library) are moved out first.
        var writtenIds = written.Select(a => a.Id).ToHashSet();
        if (req.KeepAliveDir != null)
        {
            foreach (var a in assets.All.Where(a => !writtenIds.Contains(a.Id) && PathUtil.Same(a.Source.Path, path)).ToList())
            {
                try { assets.DetachToStore(a, req.KeepAliveDir); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    warnings.Add($"Could not preserve unused image \"{a.FileName}\": {ex.Message}");
                }
            }
        }

        // Swap under the exclusive gate so no reader holds the old file open, then re-point the assets.
        if (!assets.Gate.TryEnterWrite(TimeSpan.FromSeconds(60)))
            throw new IOException("The project file is busy (images are still being read). Try again in a moment.");
        try
        {
            File.Move(tmp, path, overwrite: true);

            foreach (var a in written)
            {
                var e = index[a.Id];
                if (assets.Get(a.Id) is { } current)
                    assets.Put(current with { Source = new AssetSource(path, e.DataOffset, e.UncompressedSize), Sha256 = current.Sha256 ?? a.Sha256 }, notify: false);
            }
            // Whatever still points into the replaced file has no data any more (it could not be moved out above).
            foreach (var a in assets.All.Where(a => !writtenIds.Contains(a.Id) && PathUtil.Same(a.Source.Path, path)).ToList())
                assets.Remove(a.Id, notify: false);
        }
        finally
        {
            assets.Gate.ExitWrite();
        }
    }

    /// <summary>First-page thumbnail stored in a project file, for the start screen. Null when absent or unreadable.</summary>
    public static byte[]? ReadFirstThumbnail(string path)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            var json = zip.GetEntry(JsonEntry);
            string? firstId = null;
            if (json != null)
            {
                using var js = json.Open();
                using var doc = JsonDocument.Parse(js);
                if (doc.RootElement.TryGetProperty("project", out var pr) && pr.TryGetProperty("pages", out var pages) &&
                    pages.ValueKind == JsonValueKind.Array && pages.GetArrayLength() > 0 && pages[0].TryGetProperty("id", out var id))
                    firstId = id.GetString();
            }
            var entry = (firstId != null ? zip.GetEntry(ThumbsPrefix + firstId + ".png") : null)
                        ?? zip.Entries.FirstOrDefault(e => e.FullName.StartsWith(ThumbsPrefix, StringComparison.Ordinal));
            if (entry == null || entry.Length > MaxThumbBytes) return null;
            using var es = entry.Open();
            using var ms = new MemoryStream();
            es.CopyTo(ms);
            return ms.ToArray();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}
