using System.Buffers.Binary;
using System.Collections.Immutable;
using System.IO.Compression;
using System.Text;
using SolumanirSheetDesignerStudio.Core.Assets;
using SolumanirSheetDesignerStudio.Core.Export;
using SolumanirSheetDesignerStudio.Core.IO;
using SolumanirSheetDesignerStudio.Core.Layout;
using SolumanirSheetDesignerStudio.Core.Model;
using SolumanirSheetDesignerStudio.Core.Png;
using SolumanirSheetDesignerStudio.Core.Session;

namespace SolumanirSheetDesignerStudio.SelfTest;

/// <summary>
/// Self-contained checks for the core library. Run: dotnet run --project tests/SolumanirSheetDesignerStudio.SelfTest
/// Options: --big (also exercises ZIP64 with a &gt;4 GB entry), --keep (leave the work folder for inspection).
/// </summary>
internal static class Program
{
    private static int _passed, _failed;
    private static string _work = "";

    private static void Check(bool ok, string what)
    {
        if (ok) _passed++;
        else { _failed++; Console.WriteLine("  FAIL: " + what); }
    }

    private static void Near(double actual, double expected, string what, double tol = 0.01) =>
        Check(Math.Abs(actual - expected) <= tol, $"{what}: expected {expected}, got {actual}");

    private static void Section(string name) => Console.WriteLine("- " + name);

    private static int Main(string[] args)
    {
        _work = Path.Combine(Environment.GetEnvironmentVariable("SSDS_TEST_DIR") ?? Path.GetTempPath(), "ssds-selftest-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_work);
        Environment.SetEnvironmentVariable("SSDS_DATA_DIR", Path.Combine(_work, "appdata"));
        try
        {
            Section("model operations"); ModelOps();
            Section("undo / redo"); UndoRedo();
            Section("zip index + v2 save/load"); SaveLoad();
            Section("legacy v1 import and upgrade"); Legacy();
            Section("recovery"); Recovery();
            Section("png encoder"); Png();
            Section("layout: rows, slots, sizing"); LayoutRows();
            Section("layout: text, markdown, lore"); LayoutText();
            Section("export planning + naming"); Planning();
            Section("review regressions: files and sessions"); ReviewFiles();
            Section("review regressions: layout and pixels"); ReviewLayout();
            if (args.Contains("--big")) { Section("zip64 (>4 GB entry)"); Zip64(); }
        }
        catch (Exception ex)
        {
            _failed++;
            Console.WriteLine("  EXCEPTION: " + ex);
        }
        finally
        {
            if (!args.Contains("--keep"))
                try { Directory.Delete(_work, true); } catch { /* ignore */ }
        }
        Console.WriteLine($"{_passed} passed, {_failed} failed" + (args.Contains("--keep") ? $" (work dir: {_work})" : ""));
        return _failed == 0 ? 0 : 1;
    }

    // ---------------------------------------------------------------------------------------------

    private static void ModelOps()
    {
        var p = Factory.NewProject();
        var page = p.Pages[0];
        Check(page.Rows.Count == 1 && page.Rows[0].Slots.Count == 3 && page.Details.Slots.Count == 3 && page.Lore.Count == 1, "default page shape");
        Check(page.Rows[0].Sizing.Height == 440 && page.Details.Sizing.Height == 190, "default heights");
        Check(p.Style.LoreWidth == 30 && p.Style.LoreBorder == BorderKind.Ornate && p.Style.PageBorder == BorderKind.None, "default style");

        var row = page.Rows[0];
        var pg2 = page.AddRow(Factory.NewRow());
        Check(pg2.Rows.Count == 2 && ReferenceEquals(pg2.Rows[0], row), "add row shares structure");

        // move slot within a section
        string a = row.Slots[0].Id, b = row.Slots[1].Id, c = row.Slots[2].Id;
        var moved = page.MoveSlot(a, row.Id, 3);
        Check(moved.Rows[0].Slots.Select(s => s.Id).SequenceEqual(new[] { b, c, a }), "move slot to end");
        moved = page.MoveSlot(c, row.Id, 0);
        Check(moved.Rows[0].Slots.Select(s => s.Id).SequenceEqual(new[] { c, a, b }), "move slot to start");
        Check(ReferenceEquals(page.MoveSlot(a, row.Id, 1), page), "move slot no-op returns same instance");

        // move between sections
        var cross = page.MoveSlot(a, page.Details.Id, 1);
        Check(cross.Rows[0].Slots.Count == 2 && cross.Details.Slots.Count == 4 && cross.Details.Slots[1].Id == a, "move slot to details");

        // split / remove part
        var split = page.SplitSlot(a);
        var slot = split.FindSlot(a)!.Value.Slot;
        Check(slot.Parts.Count == 2 && slot.IsSplit, "split slot");
        var unsplit = split.RemovePart(slot.Parts[1].Id);
        Check(unsplit.FindSlot(a)!.Value.Slot.Parts.Count == 1, "remove part");
        Check(ReferenceEquals(unsplit.RemovePart(unsplit.FindSlot(a)!.Value.Slot.Parts[0].Id), unsplit), "last part cannot be removed");

        // images
        string partId = row.Slots[0].Parts[0].Id;
        var withImg = p.UpdatePage(page.Id, x => x.SetPartImage(partId, "img1"));
        Check(withImg.ReferencedImageIds().SetEquals(new[] { "img1" }), "referenced ids");
        var dup = withImg.AddPage(withImg.Pages[0].CloneWithNewIds(" (copy)"));
        Check(dup.ImageUsage()["img1"] == 2 && dup.Pages[1].Id != dup.Pages[0].Id && dup.Pages[1].Name.EndsWith("(copy)"), "duplicate page shares assets");
        Check(dup.ReplaceImage("img1", "img2").ImageUsage().GetValueOrDefault("img2") == 2, "replace image everywhere");
        Check(dup.Pages[0].WithoutImages().ImageIds().Count() == 0, "template strips images");

        // weights
        Check(new Part { Scale = 0 }.Weight == 100 && new Part { Scale = 2000 }.Weight == 1000 && new Part { Scale = 1 }.Weight == 5 && new Part { Scale = double.NaN }.Weight == 100, "part weight clamps");

        // removing every page leaves a blank one
        var none = p.RemovePages(new[] { page.Id }, () => Factory.NewPage());
        Check(none.Pages.Count == 1 && none.Pages[0].Id != page.Id, "never zero pages");
    }

    private static void UndoRedo()
    {
        using var s = DocumentSession.CreateNew(Factory.NewProject(), Path.Combine(_work, "rec-undo"));
        Check(!s.IsDirty && !s.CanUndo, "fresh session is clean");
        string page = s.CurrentPageId;

        s.ApplyToPage("Rename", p => p with { Name = "A" }, "name");
        s.ApplyToPage("Rename", p => p with { Name = "Ab" }, "name");
        s.ApplyToPage("Rename", p => p with { Name = "Abc" }, "name");
        Check(s.CurrentPage.Name == "Abc" && s.IsDirty, "edits applied");
        s.Undo();
        Check(s.CurrentPage.Name == "New Species" && !s.IsDirty, "typing coalesces into one undo step and returns to the clean state");
        s.Redo();
        Check(s.CurrentPage.Name == "Abc", "redo");

        s.BreakCoalescing();
        s.ApplyToPage("Rename", p => p with { Name = "Abcd" }, "name");
        s.Undo();
        Check(s.CurrentPage.Name == "Abc", "coalescing breaks when the field is left");

        Check(!s.ApplyToPage("Noop", p => p), "no-op edit reports no change");

        // undo switches back to the page the edit was made on
        var second = Factory.NewPage();
        s.Apply("Add page", p => p.AddPage(second), selectPageId: second.Id);
        Check(s.CurrentPageId == second.Id, "new page selected");
        s.ApplyToPage("Subtitle", p => p with { Subtitle = "x" });
        s.SelectPage(page);
        s.Undo();
        Check(s.CurrentPageId == second.Id && s.CurrentPage.Subtitle == "", "undo navigates to the edited page");
        s.Undo();
        Check(s.Project.Pages.Count == 1 && s.CurrentPageId == page, "undo add page");

        // deleting the current page selects a neighbour
        s.Redo();
        s.Apply("Delete", p => p.RemovePages(new[] { second.Id }, () => Factory.NewPage()));
        Check(s.CurrentPageId == page, "current page follows deletion");
    }

    private sealed class FakeProbe : IImageProbe
    {
        public ImageProbeResult? Probe(Stream stream)
        {
            Span<byte> head = stackalloc byte[4];
            if (stream.ReadAtLeast(head, 4, false) < 4 || head[0] != (byte)'F') return null;
            return new ImageProbeResult(head[1] * 10, head[2] * 10, "image/png", ".png");
        }
    }

    private static string MakeImage(string name, byte w, byte h, int size, int seed)
    {
        var data = new byte[size];
        new Random(seed).NextBytes(data);
        data[0] = (byte)'F'; data[1] = w; data[2] = h;
        string path = Path.Combine(_work, name);
        File.WriteAllBytes(path, data);
        return path;
    }

    private static byte[] ReadAll(AssetLibrary lib, ImageAsset a)
    {
        using var s = lib.OpenRead(a);
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    private static void SaveLoad()
    {
        var probe = new FakeProbe();
        string f1 = MakeImage("one.png", 30, 60, 70_000, 1), f2 = MakeImage("two.png", 20, 20, 5_000, 2), f3 = MakeImage("three.png", 10, 40, 123_456, 3);
        string projPath = Path.Combine(_work, "World.speciesproj");
        string partA, partB;

        using (var s = DocumentSession.CreateNew(Factory.NewProject(), Path.Combine(_work, "rec-a")))
        {
            var a1 = s.ImportImage(f1, probe);
            var a2 = s.ImportImage(f2, probe);
            var a3 = s.ImportImage(f3, probe);
            Check(a1.PixelWidth == 300 && a1.PixelHeight == 600 && a1.ByteLength == 70_000, "import probes the image");
            Check(ReferenceEquals(s.ImportImage(f1, probe), a1) || s.ImportImage(f1, probe).Id == a1.Id, "identical content is stored once");
            Check(s.Assets.Count == 3, "three assets");
            Check(s.ImportImageFails(Path.Combine(_work, "World.speciesproj.nope"), probe), "missing file fails cleanly");

            partA = s.CurrentPage.Rows[0].Slots[0].Parts[0].Id;
            partB = s.CurrentPage.Rows[0].Slots[1].Parts[0].Id;
            s.ApplyToPage("img", p => p.SetPartImage(partA, a1.Id).SetPartImage(partB, a2.Id));
            s.ApplyToPage("img", p => p.SetPartImage(partB, a3.Id)); // a2 is now only reachable through undo
            s.Zoom = 0.75;
            s.LastExport = new ExportOptions { ScaleMode = ExportScaleMode.X4, Folder = "/tmp/out" };

            var req = s.CreateSaveRequest(projPath, relocate: true, new Dictionary<string, byte[]> { [s.CurrentPageId] = new byte[] { 1, 2, 3 } });
            var result = ProjectFile.Save(req);
            s.CompleteSave(req);
            Check(result.ImageCount == 2 && result.Warnings.Count == 0, "only referenced images are written");
            Check(!s.IsDirty && s.FilePath == projPath, "session is clean after save");
            Check(s.Assets.Get(a1.Id)!.Source.Path == projPath && s.Assets.Get(a3.Id)!.Source.Path == projPath, "assets relocated into the project file");
            Check(s.Assets.Get(a2.Id)!.Source.Path != projPath && File.Exists(s.Assets.Get(a2.Id)!.Source.Path), "undo-only asset stays in the working folder");
            Check(ReadAll(s.Assets, s.Assets.Get(a1.Id)!).AsSpan().SequenceEqual(File.ReadAllBytes(f1)), "relocated bytes are identical");
            Check(!File.Exists(projPath + ".tmp"), "no temp file left");

            // undo brings back a2; second save must write it again
            s.Undo();
            Check(s.IsDirty && s.CurrentPage.FindPart(partB)!.Value.Part.ImageId == a2.Id, "undo after save");
            req = s.CreateSaveRequest(projPath, true, null);
            result = ProjectFile.Save(req);
            s.CompleteSave(req);
            Check(result.ImageCount == 2, "second save");
            Check(Directory.EnumerateFiles(_work).All(f => !Path.GetFileName(f).StartsWith(Path.GetFileName(projPath) + ".")), "saving writes nothing but the project file next to it");
            Check(ReadAll(s.Assets, s.Assets.Get(a2.Id)!).AsSpan().SequenceEqual(File.ReadAllBytes(f2)), "raw copy from the old project file is intact");
            Check(s.Assets.Get(a3.Id)!.Source.Path != projPath, "asset dropped from the file was moved out before the swap");
            Check(ReadAll(s.Assets, s.Assets.Get(a3.Id)!).AsSpan().SequenceEqual(File.ReadAllBytes(f3)), "moved-out asset is intact");

            // save a copy must not touch the session
            string copy = Path.Combine(_work, "Copy.speciesproj");
            var copyReq = s.CreateSaveRequest(copy, relocate: false, null);
            ProjectFile.Save(copyReq);
            Check(File.Exists(copy) && s.FilePath == projPath && s.Assets.Get(a1.Id)!.Source.Path == projPath, "save a copy leaves the session alone");
        }
        Check(!Directory.Exists(Path.Combine(_work, "rec-a")) || !Directory.EnumerateDirectories(Path.Combine(_work, "rec-a")).Any(), "clean close removes the working folder");

        // archive structure: readable by the framework, images Stored
        using (var zip = ZipFile.OpenRead(projPath))
        {
            Check(zip.GetEntry("project.json") != null, "project.json present");
            var imgs = zip.Entries.Where(e => e.FullName.StartsWith("images/")).ToList();
            Check(imgs.Count == 2 && imgs.All(e => e.CompressedLength == e.Length), "image entries are stored uncompressed");
        }
        var index = ZipIndex.Read(projPath);
        Check(index.Count == 3 && index.Where(e => e.Name.StartsWith("images/")).All(e => e.IsStored && e.DataOffset > 0), "zip index resolves data offsets");

        // reload
        using (var s = DocumentSession.Open(projPath, Path.Combine(_work, "rec-b")))
        {
            Check(s.Assets.Count == 2 && !s.IsDirty && !s.IsLegacySource, "reload");
            Check(s.Zoom == 0.75 && s.LastExport?.ScaleMode == ExportScaleMode.X4 && s.LastExport.Folder == "/tmp/out", "view state round-trips");
            var a = s.Assets.Get(s.CurrentPage.FindPart(partA)!.Value.Part.ImageId!)!;
            Check(a.PixelWidth == 300 && a.FileName == "one.png" && a.Sha256 != null, "asset metadata round-trips");
            Check(ReadAll(s.Assets, a).AsSpan().SequenceEqual(File.ReadAllBytes(f1)), "lazy handle reads the original bytes");
            using var stream = s.Assets.OpenRead(a);
            stream.Seek(-10, SeekOrigin.End);
            var tail = new byte[10];
            stream.ReadExactly(tail);
            Check(tail.AsSpan().SequenceEqual(File.ReadAllBytes(f1).AsSpan(69_990)), "range stream seeks");
        }

        // saving an opened project over itself
        using (var s = DocumentSession.Open(projPath, Path.Combine(_work, "rec-c")))
        {
            s.ApplyToPage("x", p => p with { Subtitle = "changed" });
            var req = s.CreateSaveRequest(projPath, true, null);
            ProjectFile.Save(req);
            s.CompleteSave(req);
            Check(ProjectFile.Load(projPath).Project.Pages[0].Subtitle == "changed", "new version saved");
        }

        // garbage is rejected, never crashes
        string junk = Path.Combine(_work, "junk.speciesproj");
        File.WriteAllText(junk, "this is not a project");
        bool threw = false;
        try { ProjectFile.Load(junk); } catch (InvalidProjectException) { threw = true; }
        Check(threw, "invalid file raises InvalidProjectException");
        File.WriteAllBytes(junk, File.ReadAllBytes(projPath).AsSpan(0, 2000).ToArray());
        threw = false;
        try { ProjectFile.Load(junk); } catch (InvalidProjectException) { threw = true; }
        Check(threw, "truncated zip raises InvalidProjectException");
    }

    private static bool ImportImageFails(this DocumentSession s, string path, IImageProbe probe)
    {
        try { s.ImportImage(path, probe); return false; }
        catch (IOException) { return true; }
    }

    private static void Legacy()
    {
        // Build a v1 file exactly as the prototype's saveProject() does.
        byte[] img1 = { (byte)'F', 5, 10, 65, 65, 65, 65, 65, 65, 65, 65, 65, 65 }, img2 = { (byte)'F', 2, 2, 66, 66, 66, 66 };
        string json = """
        {"v":1,"project":{"title":"Old World","current":"p2","pages":[
          {"id":"p1","name":"Elves","subtitle":"Tall folk",
           "rows":[{"id":"r1","name":"Female","h":300,"slots":[{"id":"s1","label":"Base","img":"i1","scale":80},{"id":"s2","label":"Split","parts":[{"id":"q1","img":"i2","scale":150},{"id":"q2","img":null,"scale":""}]}]},
                   {"id":"r2","name":"Male","mode":"auto","maxH":"500","renderH":2000,"h":"","slots":[]}],
           "detailsTitle":"Marks","detailH":150,"details":[{"id":"d1","label":"Ears","img":null}],
           "lore":[{"id":"l1","title":"Overview","body":"Hello **world**"}]},
          {"id":"p2","name":"Dwarves","subtitle":"","rows":[],"detailsTitle":"Features","detailH":190,"det":{"mode":"auto","maxH":120},"details":[],"lore":[]}
        ]},"images":[{"id":"i1","name":"a.png","type":"image/png","size":13,"w":50,"h":100},{"id":"i2","name":"b.jpg","type":"","size":7,"w":20,"h":20}]}
        """;
        byte[] jb = Encoding.UTF8.GetBytes(json);
        string path = Path.Combine(_work, "old.speciesproj");
        using (var fs = File.Create(path))
        {
            fs.Write(Encoding.ASCII.GetBytes("SPSHEET1"));
            Span<byte> len = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(len, (uint)jb.Length);
            fs.Write(len);
            fs.Write(jb);
            fs.Write(img1);
            fs.Write(img2);
        }

        using var s = DocumentSession.Open(path, Path.Combine(_work, "rec-legacy"));
        Check(s.IsLegacySource && !s.IsDirty && s.Project.Title == "Old World" && s.CurrentPageId == "p2", "legacy header fields");
        var p1 = s.Project.Pages[0];
        Check(p1.Name == "Elves" && p1.Rows.Count == 2 && p1.Rows[0].Sizing.Height == 300 && p1.Rows[0].Sizing.Mode == SizingMode.Fixed, "legacy fixed row");
        Check(p1.Rows[0].Slots[0].Parts.Count == 1 && p1.Rows[0].Slots[0].Parts[0].ImageId == "i1" && p1.Rows[0].Slots[0].Parts[0].Scale == 80, "legacy single-image slot becomes one part");
        Check(p1.Rows[0].Slots[1].Parts.Count == 2 && p1.Rows[0].Slots[1].Parts[0].Scale == 150 && p1.Rows[0].Slots[1].Parts[1].Scale == 100, "legacy split slot, empty scale defaults to 100");
        Check(p1.Rows[1].Sizing.Mode == SizingMode.Auto && p1.Rows[1].Sizing.MaxHeight == 500 && p1.Rows[1].Sizing.ExportPx == 2000 && p1.Rows[1].Sizing.Height == 440, "legacy auto row, string numbers");
        Check(p1.Details.Name == "Marks" && p1.Details.Sizing.Height == 150 && p1.Details.Slots.Count == 1, "legacy details from detailH");
        var p2 = s.Project.Pages[1];
        Check(p2.Details.Sizing.Mode == SizingMode.Auto && p2.Details.Sizing.MaxHeight == 120 && p2.Details.Sizing.Height == 440, "legacy details from a det block without h use the prototype's 440 fallback");
        Check(s.Project.Style == SheetStyle.Default, "legacy gets the default style");
        Check(s.Assets.Count == 2 && ReadAll(s.Assets, s.Assets.Get("i1")!).AsSpan().SequenceEqual(img1) && ReadAll(s.Assets, s.Assets.Get("i2")!).AsSpan().SequenceEqual(img2), "legacy images are lazy ranges of the v1 file");
        Check(s.Assets.Get("i2")!.MimeType == "image/jpeg" && s.Assets.Get("i1")!.PixelHeight == 100, "legacy image metadata");

        // Saving upgrades the same path to v2 while reading the images out of the v1 file being replaced.
        var req = s.CreateSaveRequest(path, true, null);
        ProjectFile.Save(req);
        s.CompleteSave(req);
        Check(!s.IsLegacySource && File.ReadAllBytes(path)[0] == (byte)'P', "saved as v2 zip");
        var re = ProjectFile.Load(path);
        Check(!re.IsLegacy && re.Assets.Count == 2 && re.Project.Pages[0].Rows[1].Sizing.ExportPx == 2000, "upgraded file loads");
        Check(ReadAll(s.Assets, s.Assets.Get("i1")!).AsSpan().SequenceEqual(img1), "images survive the upgrade");

        // A file that ends inside its header is a damaged project, not a stream error.
        string cut = Path.Combine(_work, "cut.speciesproj");
        File.WriteAllBytes(cut, Encoding.ASCII.GetBytes("SPSHEET1\0\0"));
        Check(Throws<InvalidProjectException>(() => ProjectFile.Load(cut)), "a legacy file cut off inside its header raises InvalidProjectException");
    }

    private static void Recovery()
    {
        string root = Path.Combine(_work, "rec-crash");
        var probe = new FakeProbe();
        string projPath = Path.Combine(_work, "Recover.speciesproj");
        string f1 = MakeImage("r1.png", 4, 4, 3000, 11), f2 = MakeImage("r2.png", 8, 8, 4000, 12);

        // A saved project with one image, then unsaved work that adds a second image — and a "crash".
        var s = DocumentSession.CreateNew(Factory.NewProject(), root);
        string partA = s.CurrentPage.Rows[0].Slots[0].Parts[0].Id, partB = s.CurrentPage.Rows[0].Slots[1].Parts[0].Id;
        var a1 = s.ImportImage(f1, probe);
        s.ApplyToPage("img", p => p.SetPartImage(partA, a1.Id));
        var req = s.CreateSaveRequest(projPath, true, null);
        ProjectFile.Save(req);
        s.CompleteSave(req);
        Check(!s.Recovery.HasSnapshot, "no snapshot while clean");

        var a2 = s.ImportImage(f2, probe);
        s.ApplyToPage("img", p => p.SetPartImage(partB, a2.Id) with { Name = "Unsaved Name" });
        Check(s.Recovery.WriteSnapshot(s.CreateRecoverySnapshot()), "recovery snapshot written");
        Check(RecoveryStore.FindOrphans(root).Count == 0, "a live session is not offered for recovery");
        s.Close(keepRecovery: true); // simulated crash: lock released, folder left behind

        var orphans = RecoveryStore.FindOrphans(root);
        Check(orphans.Count == 1 && orphans[0].Info.Title == "My World" && orphans[0].Info.ProjectPath == projPath, "orphaned session found");

        using (var r = DocumentSession.Recover(orphans[0]))
        {
            Check(r.IsDirty && r.FilePath == projPath && r.CurrentPage.Name == "Unsaved Name", "recovered state");
            Check(r.Assets.Count == 2 && r.LoadWarnings.Count == 0, "recovered both images");
            Check(ReadAll(r.Assets, r.Assets.Get(a1.Id)!).AsSpan().SequenceEqual(File.ReadAllBytes(f1)), "saved image comes from the project file");
            Check(ReadAll(r.Assets, r.Assets.Get(a2.Id)!).AsSpan().SequenceEqual(File.ReadAllBytes(f2)), "unsaved image comes from the recovery folder");
            Check(RecoveryStore.FindOrphans(root).Count == 0, "adopted session is locked again");
        }
        Check(RecoveryStore.FindOrphans(root).Count == 0, "clean close clears recovery data");
    }

    // ---------------------------------------------------------------------------------------------

    private static void Png()
    {
        Check(Crc32.Compute(Encoding.ASCII.GetBytes("123456789")) == 0xCBF43926u, "crc32 check value");
        Check(Adler32.Compute(Encoding.ASCII.GetBytes("Wikipedia")) == 0x11E60398u, "adler32 check value");
        var rnd = new Random(5);
        var blob = new byte[100_000];
        rnd.NextBytes(blob);
        Check(Adler32.Combine(Adler32.Compute(blob.AsSpan(0, 33_333)), Adler32.Compute(blob.AsSpan(33_333)), blob.Length - 33_333) == Adler32.Compute(blob), "adler32 combine");

        const int w = 257, h = 301;
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                px[i] = (byte)(x * 3 + y); px[i + 1] = (byte)(y * 2); px[i + 2] = (byte)((x ^ y) & 0xF0); px[i + 3] = (byte)(x < 40 ? rnd.Next(256) : 255);
            }

        foreach (var level in new[] { PngCompression.Fast, PngCompression.Balanced, PngCompression.Small })
        {
            int[] cuts = { 0, 1, 64, 65, 200, h };
            var strips = new PngStrip[cuts.Length - 1];
            // Encode out of order and in parallel, as the exporter does.
            Parallel.For(0, strips.Length, k =>
            {
                int rows = cuts[k + 1] - cuts[k];
                strips[k] = PngStripEncoder.Encode(px.AsSpan(cuts[k] * w * 4, rows * w * 4), w, rows, w * 4, level, isLast: k == strips.Length - 1);
            });
            using var ms = new MemoryStream();
            var writer = new PngStreamWriter(ms, w, h);
            foreach (var s in strips) writer.WriteStrip(s);
            writer.Finish();
            byte[] png = ms.ToArray();
            File.WriteAllBytes(Path.Combine(_work, $"test-{level}.png"), png);

            byte[]? decoded = DecodePng(png, out int dw, out int dh, out string? error);
            Check(decoded != null && dw == w && dh == h, $"png ({level}) decodes: {error}");
            Check(decoded != null && decoded.AsSpan().SequenceEqual(px), $"png ({level}) pixels are bit-exact");
        }
        string keep = Environment.GetEnvironmentVariable("SSDS_TEST_PNG_OUT") ?? "";
        if (keep.Length > 0)
        {
            File.Copy(Path.Combine(_work, "test-Balanced.png"), keep, true);
            File.WriteAllBytes(keep + ".rgba", px);
        }
    }

    /// <summary>Minimal reference decoder (chunk CRCs, zlib with Adler check, all five filters).</summary>
    private static byte[]? DecodePng(byte[] png, out int width, out int height, out string? error)
    {
        width = height = 0;
        error = null;
        try
        {
            int p = 8;
            using var idat = new MemoryStream();
            bool end = false;
            while (p < png.Length)
            {
                int len = (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(p));
                string type = Encoding.ASCII.GetString(png, p + 4, 4);
                var data = png.AsSpan(p + 8, len);
                uint crc = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(p + 8 + len));
                if (Crc32.Compute(png.AsSpan(p + 4, 4 + len)) != crc) { error = "bad crc in " + type; return null; }
                if (type == "IHDR")
                {
                    width = (int)BinaryPrimitives.ReadUInt32BigEndian(data);
                    height = (int)BinaryPrimitives.ReadUInt32BigEndian(data[4..]);
                    if (data[8] != 8 || data[9] != 6) { error = "not 8-bit rgba"; return null; }
                }
                else if (type == "IDAT") idat.Write(data);
                else if (type == "IEND") end = true;
                p += 12 + len;
            }
            if (!end) { error = "no IEND"; return null; }
            idat.Position = 0;
            using var z = new ZLibStream(idat, CompressionMode.Decompress);
            int rb = width * 4;
            var raw = new byte[height * (rb + 1)];
            z.ReadExactly(raw);
            if (z.ReadByte() != -1) { error = "trailing data"; return null; }
            var outPx = new byte[height * rb];
            for (int y = 0; y < height; y++)
            {
                int f = raw[y * (rb + 1)];
                var src = raw.AsSpan(y * (rb + 1) + 1, rb);
                var dst = outPx.AsSpan(y * rb, rb);
                ReadOnlySpan<byte> up = y > 0 ? outPx.AsSpan((y - 1) * rb, rb) : new byte[rb];
                for (int i = 0; i < rb; i++)
                {
                    int a = i >= 4 ? dst[i - 4] : 0, b = up[i], c = i >= 4 ? up[i - 4] : 0;
                    int pred = f switch
                    {
                        0 => 0, 1 => a, 2 => b, 3 => (a + b) / 2,
                        _ => Paeth(a, b, c),
                    };
                    dst[i] = (byte)(src[i] + pred);
                }
            }
            return outPx;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    // ---------------------------------------------------------------------------------------------

    /// <summary>Deterministic stand-in for HarfBuzz: every character is one glyph, half an em wide (spaces a quarter).</summary>
    private sealed class FakeShaper : ITextShaper
    {
        public FontMetricsInfo GetMetrics(FontSpec font) => new(font.Size * 0.8, font.Size * 0.2);

        public ShapedText Shape(string text, FontSpec font)
        {
            int n = text.Length;
            var g = new ushort[n]; var c = new int[n]; var a = new float[n];
            for (int i = 0; i < n; i++) { g[i] = text[i]; c[i] = i; a[i] = (float)(font.Size * (text[i] == ' ' ? 0.25 : 0.5)); }
            return new ShapedText(g, c, a, new float[n], new float[n], new int[n]);
        }
    }

    /// <summary>Blank-line separated paragraphs; "# " headings; "- " list items; "---" rules.</summary>
    private sealed class FakeMarkdown : IMarkdownParser
    {
        public IReadOnlyList<MdBlock> Parse(string markdown)
        {
            var blocks = new List<MdBlock>();
            foreach (string chunk in markdown.Replace("\r", "").Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
            {
                if (chunk.StartsWith("# ")) blocks.Add(new MdHeading(1, new MdInline[] { new MdText(chunk[2..]) }));
                else if (chunk == "---") blocks.Add(new MdRule());
                else if (chunk.StartsWith("- "))
                    blocks.Add(new MdList(false, 1, true, chunk.Split('\n').Select(l => (IReadOnlyList<MdBlock>)new MdBlock[] { new MdParagraph(new MdInline[] { new MdText(l[2..]) }) }).ToList()));
                else blocks.Add(new MdParagraph(new MdInline[] { new MdText(chunk) }));
            }
            return blocks;
        }
    }

    private static SheetLayoutEngine Engine(Dictionary<string, (int, int)> sizes) =>
        new(new FakeShaper(), new FakeMarkdown(), id => sizes.TryGetValue(id, out var s) ? s : null);

    private static void LayoutRows()
    {
        var sizes = new Dictionary<string, (int, int)> { ["tall"] = (4000, 6000), ["wide"] = (2000, 1000), ["sq"] = (500, 500) };
        var engine = Engine(sizes);
        var style = SheetStyle.Default;

        // --- default page geometry (page border None → 6-unit transparent frame) ---
        var page = Factory.NewPage() with { Lore = ImmutableList<LoreSection>.Empty };
        var L = engine.Layout(page, style);
        double contentW = 1600 - 28 - 12 - 44; // 1516
        Near(L.NameBox.X, 14 + 6 + 22, "content left");
        Near(L.NameBox.W, contentW, "content width");
        Near(L.NameBox.Y, 14 + 6 + 20, "header top");
        Near(L.SubtitleBox.Y, 40 + 60 + 4 + 13 + 6, "subtitle top");
        double bodyY = 40 + 117 + 10;
        Near(L.Rows[0].Box.Y, bodyY, "body top");
        double loreW = contentW * 0.30, leftW = contentW - 14 - loreW;
        Near(L.LoreBox.W, loreW, "lore width is 30% of the body");
        Near(L.Rows[0].Box.W, leftW, "left column width");
        // row box: 10 + banner 37 + 8 + (440 + 28) + 7
        Near(L.Rows[0].Box.H, 10 + 37 + 8 + 468 + 7, "plain row height");
        var slots = L.Rows[0].Slots;
        double slotW = (leftW - 22 - 16) / 3;
        Near(slots[0].Area.W, slotW, "three equal slots");
        Near(slots[1].Area.X - slots[0].Area.Right, 8, "slot gap");
        Near(slots[0].Area.H, 440, "fixed image area height");
        Near(slots[0].LabelBox.Y, slots[0].Area.Bottom + 2, "label below area");
        Near(L.Details!.Box.Y, L.Rows[0].Box.Bottom + 14, "details below rows");
        Near(L.Details.Slots[0].Area.H, 190, "details height");
        double leftH = L.Details.Box.Bottom - bodyY;
        Near(L.LoreBox.H, leftH, "lore box stretches to the left column");
        Near(L.Height, 14 + 6 + 20 + 117 + 10 + leftH + 22 + 6 + 14, "sheet height");

        // ornate page border narrows the frame to 3 units; ornate rows have a 34-unit banner
        var ornate = engine.Layout(page, style with { PageBorder = BorderKind.Ornate, RowBorder = BorderKind.Ornate });
        Near(ornate.NameBox.W, 1600 - 28 - 6 - 44, "ornate frame content width");
        Near(ornate.Rows[0].Box.H, 10 + 34 + 8 + 468 + 7, "ornate row height");

        // preview hides an empty details row; edit mode keeps it
        var noDetails = page with { Details = page.Details with { Slots = ImmutableList<Slot>.Empty } };
        Check(engine.Layout(noDetails, style).Details == null, "empty details hidden in preview/export");
        Check(engine.Layout(noDetails, style, new LayoutOptions { EditChrome = true }).Details != null, "empty details shown while editing");
        Check(engine.Layout(page, style).Chrome.Count == 0 && engine.Layout(page, style, new LayoutOptions { EditChrome = true }).Chrome.Count > 0, "placeholders only in edit mode");
        var edit = engine.Layout(page, style, new LayoutOptions { EditChrome = true });
        Near(edit.Height, L.Height, "edit chrome does not change the layout");
        Near(edit.Rows[0].Slots[2].Area.W, slotW, "edit chrome does not change slot widths");

        // --- fixed row, unsplit: contain fit, bottom-centre, scale shrinks the box ---
        Section Row(params Slot[] s) => new() { Name = "R", Sizing = new Sizing { Height = 440 }, Slots = s.ToImmutableList() };
        Slot One(string? img, double scale = 100) => new() { Label = "L", Parts = ImmutableList.Create(new Part { ImageId = img, Scale = scale }) };
        var p1 = page with { Rows = ImmutableList.Create(Row(One("tall"), One("wide", 50), One(null))) };
        L = engine.Layout(p1, style);
        var a0 = L.Rows[0].Slots[0];
        var img0 = a0.Parts[0].Image!;
        Near(img0.Container.H, 438, "image box = area minus outline");
        Near(img0.Drawn.H, 438, "tall image fills the height");
        Near(img0.Drawn.W, 438 * 4000.0 / 6000, "tall image keeps its aspect");
        Near(img0.Drawn.Bottom, a0.Area.Bottom - 1, "bottom aligned on the ground line");
        Near(img0.Drawn.CenterX, a0.Area.CenterX, "horizontally centred");
        var img1 = L.Rows[0].Slots[1].Parts[0].Image!;
        Near(img1.Container.W, (slotW - 2) * 0.5, "50% scale halves the box width");
        Near(img1.Container.H, 219, "50% scale halves the box height");
        Near(img1.Container.Bottom, a0.Area.Bottom - 1, "scaled box stays on the ground line");
        Near(img1.Drawn.W, img1.Container.W, "wide image fills the box width");
        Check(L.Rows[0].Slots[2].Parts[0].Image == null && L.Images.Count == 2, "empty part has no placement");

        // details: centred both ways
        var pd = page with { Details = page.Details with { Slots = ImmutableList.Create(One("wide")) } };
        var det = engine.Layout(pd, style).Details!.Slots[0];
        Near(det.Parts[0].Image!.Drawn.CenterY, det.Area.CenterY, "details image vertically centred");

        // --- fixed row, split: weights 150 / 100 → 60 / 40 of the height left after the divider ---
        var splitSlot = new Slot { Label = "S", Parts = ImmutableList.Create(new Part { ImageId = "sq", Scale = 150 }, new Part { ImageId = "wide", Scale = 100 }) };
        var p2 = page with { Rows = ImmutableList.Create(Row(splitSlot) with { Sizing = new Sizing { Height = 440, ExportPx = 2000 } }) };
        L = engine.Layout(p2, style);
        var sp = L.Rows[0].Slots[0];
        Near(sp.Parts[0].Box.H, 437 * 0.6, "split part 1 gets 60%");
        Near(sp.Parts[1].Box.H, 437 * 0.4, "split part 2 gets 40%");
        Near(sp.Parts[1].Box.Y, sp.Parts[0].Box.Bottom + 1, "1-unit divider between parts");
        Near(sp.Parts[1].Box.Bottom, sp.Area.Bottom - 1, "parts fill the area");
        Near(sp.Parts[0].Image!.Container.W, sp.Area.W - 2, "split image fills its part");
        Near(sp.Parts[0].Image!.ExportPx, 1200, "export px divided by weight");
        Near(sp.Parts[1].Image!.ExportPx, 800, "export px divided by weight (2)");
        Near(sp.Parts[0].Image!.Need, 1200 / sp.Parts[0].Image!.Drawn.H, "need from export px");

        // minimum heights: Fixed 40 is clamped, then the 90-unit area minimum applies
        var tiny = engine.Layout(page with { Rows = ImmutableList.Create(Row(One(null)) with { Sizing = new Sizing { Height = 10 } }) }, style);
        Near(tiny.Rows[0].Slots[0].Area.H, 90, "image area minimum is 90");

        // --- auto row: hugs the images ---
        var autoRow = new Section { Name = "A", Sizing = new Sizing { Mode = SizingMode.Auto }, Slots = ImmutableList.Create(One("tall"), One("wide", 50), One(null)) };
        L = engine.Layout(page with { Rows = ImmutableList.Create(autoRow) }, style);
        double innerW = slotW - 2;
        double tallH = innerW * 1.5;
        Near(L.Rows[0].Slots[0].Area.H, tallH + 2, "auto row is as tall as its tallest image");
        Near(L.Rows[0].Slots[0].Parts[0].Image!.Drawn.W, innerW, "auto image spans the slot width");
        var w50 = L.Rows[0].Slots[1].Parts[0].Image!;
        Near(w50.Drawn.W, innerW * 0.5, "auto image at 50% width");
        Near(w50.Drawn.H, innerW * 0.25, "auto image natural aspect");
        Near(w50.Drawn.Bottom, L.Rows[0].Slots[1].Area.Bottom - 1, "auto image bottom aligned");
        Near(L.Rows[0].Slots[2].Area.H, tallH + 2, "all areas in a row share one height");

        // auto row with max height: no image exceeds maxHeight × scale / parts
        var capped = autoRow with { Sizing = new Sizing { Mode = SizingMode.Auto, MaxHeight = 300 } };
        L = engine.Layout(page with { Rows = ImmutableList.Create(capped) }, style);
        Near(L.Rows[0].Slots[0].Parts[0].Image!.Drawn.H, 300, "max height caps the image");
        Near(L.Rows[0].Slots[0].Parts[0].Image!.Drawn.W, 200, "capped image keeps its aspect");
        Near(L.Rows[0].Slots[0].Area.H, 302, "capped row height");

        // auto row, empty: parts take 60, the area still has its 90 minimum
        L = engine.Layout(page with { Rows = ImmutableList.Create(autoRow with { Slots = ImmutableList.Create(One(null)) }) }, style);
        Near(L.Rows[0].Slots[0].Area.H, 90, "empty auto row uses the 90 minimum");

        // auto + split: export px divided equally, widths as unsplit
        var autoSplit = autoRow with { Sizing = new Sizing { Mode = SizingMode.Auto, ExportPx = 1000 }, Slots = ImmutableList.Create(splitSlot) };
        L = engine.Layout(page with { Rows = ImmutableList.Create(autoSplit) }, style);
        sp = L.Rows[0].Slots[0];
        Near(sp.Parts[0].Image!.ExportPx, 500, "auto split: export px shared equally");
        Near(sp.Parts[0].Image!.Drawn.W, sp.Area.W - 2, "auto split: weight above 100 is capped to full width");
        Near(sp.Area.H, (sp.Area.W - 2) + ((sp.Area.W - 2) * 0.5 + 1) + 2, "auto split: parts stack");

        // --- hit testing ---
        L = engine.Layout(p1, style, new LayoutOptions { EditChrome = true });
        var slot0 = L.Rows[0].Slots[0];
        var hit = L.HitTest(slot0.Area.CenterX, slot0.Area.CenterY);
        Check(hit.Kind == HitKind.Part && hit.SlotId == slot0.Slot.Id && hit.PartId == slot0.Parts[0].Part.Id, "hit: part");
        Check(L.HitTest(slot0.LabelBox.CenterX, slot0.LabelBox.CenterY).Kind == HitKind.SlotLabel, "hit: slot label");
        Check(L.HitTest(L.Rows[0].NameBox.X + 5, L.Rows[0].NameBox.CenterY).Kind == HitKind.SectionName, "hit: row name");
        Check(L.HitTest(L.NameBox.CenterX, L.NameBox.CenterY).Kind == HitKind.PageName, "hit: page name");
        Check(L.HitTest(3, 3).Kind == HitKind.Sheet && L.HitTest(-5, 3).Kind == HitKind.None, "hit: sheet / outside");
        var add = L.Affordances.First(a => a.Kind == AffordanceKind.AddSlot);
        Check(L.HitTest(add.Rect.CenterX, add.Rect.CenterY) is { Kind: HitKind.AddSlot } h2 && h2.SectionId == L.Rows[0].Section.Id, "hit: add-slot button");
        Check(L.BoundsOf(new Selection(SelectionKind.Slot, SlotId: slot0.Slot.Id)) == slot0.Box, "selection bounds");
    }

    private static void LayoutText()
    {
        var engine = Engine(new());
        var style = SheetStyle.Default;
        var shaper = new FakeShaper();

        // header: centred, letter-spacing after every glyph (including the last)
        var page = Factory.NewPage() with { Name = "ABCD", Lore = ImmutableList<LoreSection>.Empty };
        var L = engine.Layout(page, style);
        var name = L.Content.OfType<GlyphRunItem>().First(g => g.Font == SheetLayoutEngine.NameFont);
        double width = 4 * 24 + 4 * 48 * 0.07;
        Near(name.X, L.NameBox.X + (L.NameBox.W - width) / 2, "name centred including trailing letter-spacing");
        // line box 60, ascent 38 (38.4 rounded), descent 10: baseline = floor((60-48)/2) + 38
        Near(name.Y, L.NameBox.Y + 6 + 38, "name baseline from the 60-unit line box");
        Check(name.Glyphs.Length == 4 && Math.Abs(name.Positions[2] - (24 + 3.36f)) < 0.01, "glyph positions carry letter-spacing");
        var rowName = L.Content.OfType<GlyphRunItem>().First(g => g.Font == SheetLayoutEngine.RowNameFont);
        Check(rowName.Glyphs.SequenceEqual("FEMALE".Select(c => (ushort)c)), "row names are upper-cased");
        Check(L.Fields[new TextFieldRef(TextFieldKind.PageName)].Text == "ABCD", "text field registered");
        var hidden = engine.Layout(page, style, new LayoutOptions { EditChrome = true, HiddenField = new TextFieldRef(TextFieldKind.PageName) });
        Check(!hidden.Content.OfType<GlyphRunItem>().Any(g => g.Font == SheetLayoutEngine.NameFont), "field under an editor is not drawn");

        // --- inline layout ---
        var body = new FontSpec(FontFamilyId.Body, 400, false, 20);
        InlineBox Box(string text) => new(shaper, new[] { new InlineSpan(text, body, default) });
        // each letter 10 wide, space 5
        var box = Box("aaaa bbbb cccc");
        var lines = box.Break(100);
        Check(lines.Count == 2 && Math.Abs(box.LineWidth(lines[0]) - 85) < 0.01 && Math.Abs(box.LineWidth(lines[1]) - 40) < 0.01, "greedy wrap, trailing space hangs");
        lines = Box("abcdefghijklmnop").Break(50);
        Check(lines.Count == 4, "long words break (overflow-wrap)");
        lines = Box("abcdefghijklmnop").Break(50, breakWord: false);
        Check(lines.Count == 1, "no break-word when disabled");
        Near(Box("aa bbbbbb c").MinContentWidth(), 60, "min-content = longest word");
        Near(Box("aa bbbbbb c").MaxContentWidth(), 20 + 5 + 60 + 5 + 10, "max-content = unwrapped width");
        lines = Box("well-known thing").Break(60);
        Check(lines.Count == 3, "break after a hyphen inside a word");
        box = new InlineBox(shaper, new[] { new InlineSpan("ab", body, default), InlineSpan.Break(body), new InlineSpan("cd", body, default) });
        Check(box.Break(1000).Count == 2, "hard line break");

        // pretty: the orphan last line is avoided
        box = Box("aaaa aaaa aaaa aaaa aaaa aaaa b");
        var greedy = box.Break(140);
        var pretty = box.Break(140, pretty: true);
        Check(box.LineWidth(greedy[^1]) < 50 && box.LineWidth(pretty[^1]) >= 50, "pretty wrapping avoids a one-word last line");
        Check(pretty.Sum(l => l.End - l.Start) <= greedy.Sum(l => l.End - l.Start) + 2 && pretty[0].Start == 0 && pretty[^1].End == greedy[^1].End, "pretty keeps every cluster");

        // --- lore: margin collapsing ---
        LoreSection Lore(string bodyText) => new() { Title = "T", Body = bodyText };
        PageLayout Lay(string bodyText) => engine.Layout(page with { Lore = ImmutableList.Create(Lore(bodyText)) }, style);
        var l0 = Lay("").Lore[0];
        Near(l0.Box.H, 39 + 10 + 28, "empty lore section: title bar + margin + 28 minimum");
        var l1 = Lay("one line").Lore[0];
        Near(l1.BodyBox.Y - l1.TitleBox.Y, 39 + 10, "paragraph starts 10 below the title bar");
        Near(l1.Box.H, 39 + 10 + 29 + 10, "one paragraph: line 29 + trailing margin 10");
        var l2 = Lay("one\n\ntwo").Lore[0];
        Near(l2.Box.H, 39 + 10 + 29 + 10 + 29 + 10, "two paragraphs");
        var l3 = Lay("# Head\n\ntext").Lore[0];
        Near(l3.BodyBox.Y - l3.TitleBox.Y, 39 + 14, "heading margin (14) collapses with the title bar margin (10)");
        Near(l3.Box.H, 39 + 14 + 25 * 1.45 + 4 + 29 + 10, "heading then paragraph");
        var l4 = Lay("text\n\n# Head").Lore[0];
        Near(l4.Box.H, 39 + 10 + 29 + 14 + 25 * 1.45 + 4, "paragraph (10) and heading (14) margins collapse to 14");
        var l5 = Lay("text\n\n- a\n- b").Lore[0];
        Near(l5.Box.H, 39 + 10 + 29 + 10 + 29 + 2 + 29 + 8, "list: margin 8 collapses with paragraph 10, items 2 apart");
        var l6 = Lay("a\n\n---\n\nb").Lore[0];
        Near(l6.Box.H, 39 + 10 + 29 + 14 + 1 + 14 + 29 + 10, "rule with 14 margins");

        // the lore box grows the page when it is the taller column
        string longText = string.Join("\n\n", Enumerable.Repeat("paragraph", 60));
        var tall = engine.Layout(page with { Lore = ImmutableList.Create(Lore(longText)) }, style);
        Check(tall.LoreBox.H > tall.Rows[0].Box.H + tall.Details!.Box.H + 14 + 1 && Math.Abs(tall.LoreBox.H - (13 + 39 + 10 + 60 * 39 + 15)) < 0.01, "lore taller than the left column sets the body height");

        // editing a lore body reserves the editor's height
        string loreId = l1.Section.Id;
        var pg = page with { Lore = ImmutableList.Create(new LoreSection { Id = "e", Title = "T", Body = "x" }) };
        var ed = engine.Layout(pg, style, new LayoutOptions { EditChrome = true, EditingLoreId = "e", EditingLoreHeight = 400 });
        Near(ed.Lore[0].BodyBox.H, 400, "lore editor height reserved");
        Near(engine.Layout(pg, style, new LayoutOptions { EditChrome = true, EditingLoreId = "e", EditingLoreHeight = 50 }).Lore[0].BodyBox.H, 180, "lore editor minimum 180");
        _ = loreId;
    }

    private static void Planning()
    {
        var sizes = new Dictionary<string, (int, int)> { ["tall"] = (4000, 6000), ["small"] = (100, 150) };
        var engine = Engine(sizes);
        var style = SheetStyle.Default;
        Slot One(string? img) => new() { Label = "L", Parts = ImmutableList.Create(new Part { ImageId = img }) };
        var row = new Section { Name = "R", Sizing = new Sizing { Height = 440 }, Slots = ImmutableList.Create(One("tall"), One("small")) };
        var page = Factory.NewPage() with { Rows = ImmutableList.Create(row), Lore = ImmutableList<LoreSection>.Empty };
        var L = engine.Layout(page, style);

        // Acceptance 2: a 6000-px-tall image in a 440-unit row comes out 6000 px tall at Auto.
        var tall = L.Images.First(i => i.ImageId == "tall");
        var auto = ExportPlanner.Plan(L, new ExportOptions { MaxLongEdge = 0 });
        Near(tall.Drawn.H * auto.Scale, 6000, "auto: image lands at its native height", 0.001);
        Near(tall.Drawn.W * auto.Scale, 4000, "auto: image lands at its native width", 0.001);
        Check(auto.Width == (int)Math.Round(1600 * auto.Scale) && auto.Height == (int)Math.Round(L.Height * auto.Scale) && !auto.Capped, "output size = sheet × scale");
        Check(auto.UpscaledImages == 1 && auto.DownscaledImages == 0, "the small image is reported as upscaled");

        // Default 30 000 px cap.
        var capped = ExportPlanner.Plan(L, new ExportOptions());
        Check(auto.Width > 30000 ? capped.Capped && Math.Max(capped.Width, capped.Height) <= 30000 : !capped.Capped, "long edge cap");

        // Acceptance 3: Export px = 2000 → the image is 2000 px tall and the page scales with it.
        var L2 = engine.Layout(page with { Rows = ImmutableList.Create(row with { Sizing = new Sizing { Height = 440, ExportPx = 2000 } }) }, style);
        var p2 = ExportPlanner.Plan(L2, new ExportOptions { MaxLongEdge = 0 });
        Near(L2.Images.First(i => i.ImageId == "tall").Drawn.H * p2.Scale, 2000, "export px: image is 2000 px tall", 0.001);
        Check(p2.DownscaledImages == 1, "export px below native counts as downscaled");

        // No images → the 2× floor. Fixed modes.
        var empty = engine.Layout(Factory.NewPage(), style);
        Near(ExportPlanner.Plan(empty, new ExportOptions()).Scale, 2, "auto never goes below 2×");
        Near(ExportPlanner.Plan(L, new ExportOptions { ScaleMode = ExportScaleMode.X4 }).Scale, 4, "fixed 4×");
        Check(ExportPlanner.Plan(L, new ExportOptions { ScaleMode = ExportScaleMode.X8 }).Width == 12800, "8× is 12800 px wide");
        Check(ExportPlanner.Plan(L, new ExportOptions { ScaleMode = ExportScaleMode.TargetWidth, TargetWidth = 5000 }).Width == 5000, "target width");
        Check(ExportPlanner.Plan(L, new ExportOptions { ScaleMode = ExportScaleMode.CustomScale, CustomScale = 1.5 }).Width == 2400, "custom scale");
        Check(ExportPlanner.StripRows(20000, 100000, 8) is var rows && rows * 20000L <= 64_000_000 && rows >= 16, "strip budget");

        // naming
        var project = Factory.NewProject() with { Title = "My: World" };
        var pg = project.Pages[0] with { Name = "Sky/Elves?" };
        project = project with { Pages = ImmutableList.Create(pg, pg with { Id = "x2" }, pg with { Id = "x3", Name = "" }) };
        var names = FileNaming.ExpandAll("{name}.png", project, project.Pages);
        Check(names[0] == "Sky Elves.png" && names[1] == "Sky Elves (2).png" && names[2] == "untitled.png", "file naming: sanitised and unique: " + string.Join(" | ", names));
        Check(FileNaming.Expand("{index} - {project} - {name}", project, pg, 0) == "01 - My World - Sky Elves.png", "file naming tokens");
        Check(FileNaming.Expand("<Species name>.png", project, pg, 0) == "Sky Elves.png", "the handoff's default pattern spelling works");

        // A name also has to fit a Linux file name (255 bytes) together with the suffixes added to it.
        Check(FileNaming.Sanitize(new string('x', 200)).Length == 120, "a long plain name keeps the 120 character limit");
        string wide = FileNaming.Sanitize(new string('日', 200));
        Check(wide.Length == 73 && Encoding.UTF8.GetByteCount(wide) == 219, "a long name of three-byte characters is cut to 220 bytes: " + wide.Length);
        string pairs = FileNaming.Sanitize(string.Concat(Enumerable.Repeat("\U0001F600", 100)));
        Check(pairs.Length == 110 && char.IsLowSurrogate(pairs[^1]), "the cut never splits a surrogate pair: " + pairs.Length);
    }

    private static bool Throws<T>(Action action) where T : Exception
    {
        try { action(); return false; }
        catch (T) { return true; }
    }

    private static void ReviewFiles()
    {
        var probe = new FakeProbe();

        // --- the recovery store only ever touches its own session folders ---
        string root = Path.Combine(_work, "rec-foreign");
        string foreign = Path.Combine(root, "My Documents"), lookalike = Path.Combine(root, "20200101-000000-abcdef");
        Directory.CreateDirectory(foreign);
        Directory.CreateDirectory(lookalike); // right name, but no lock file: not provably ours
        File.WriteAllText(Path.Combine(foreign, "keep.txt"), "x");
        File.WriteAllText(Path.Combine(lookalike, "keep.txt"), "x");
        foreach (string d in new[] { foreign, lookalike }) Directory.SetLastWriteTimeUtc(d, DateTime.UtcNow.AddDays(-2));
        Check(RecoveryStore.FindOrphans(root).Count == 0, "foreign folders are not offered for recovery");
        Check(File.Exists(Path.Combine(foreign, "keep.txt")) && File.Exists(Path.Combine(lookalike, "keep.txt")), "foreign folders are not deleted");
        Check(Directory.GetFiles(foreign).Length == 1 && Directory.GetFiles(lookalike).Length == 1, "no lock file is created in foreign folders");
        Check(RecoveryStore.IsSessionName("20261115-101500-0a1b2c") && !RecoveryStore.IsSessionName("Photos") && !RecoveryStore.IsSessionName("20261115-101500-0A1B2C"), "session folder name shape");

        // --- a late snapshot can neither outlive a save nor resurrect a closed session ---
        string f1 = MakeImage("rv1.png", 10, 20, 5000, 11), f2 = MakeImage("rv2.png", 30, 40, 7000, 12), f3 = MakeImage("rv3.png", 5, 5, 3000, 13);
        string projPath = Path.Combine(_work, "review.speciesproj");
        string partA, partB;
        using (var s = DocumentSession.CreateNew(Factory.NewProject(), Path.Combine(_work, "rec-review")))
        {
            partA = s.CurrentPage.Rows[0].Slots[0].Parts[0].Id;
            partB = s.CurrentPage.Rows[0].Slots[1].Parts[0].Id;
            var a1 = s.ImportImage(f1, probe);
            var a2 = s.ImportImage(f2, probe);
            s.ApplyToPage("img", p => p.SetPartImage(partA, a1.Id).SetPartImage(partB, a2.Id));

            var stale = s.CreateRecoverySnapshot();
            var req = s.CreateSaveRequest(projPath, true, null);
            Check(s.Assets.Count == 2, "creating a save request has no side effects");
            ProjectFile.Save(req);
            s.CompleteSave(req);
            Check(!s.Recovery.WriteSnapshot(stale) && !s.Recovery.HasSnapshot, "a snapshot taken before the save is not written after it");

            // typing after a save starts a new undo step, so the saved state stays reachable
            s.ApplyToPage("t", p => p with { Name = "A" }, "name");
            var req2 = s.CreateSaveRequest(projPath, true, null);
            ProjectFile.Save(req2);
            s.CompleteSave(req2);
            s.ApplyToPage("t", p => p with { Name = "AB" }, "name");
            s.Undo();
            Check(s.CurrentPage.Name == "A" && !s.IsDirty, "undo after a save returns to exactly the saved state");

            // --- images imported while nothing uses them yet survive a save that completes meanwhile ---
            using (s.HoldAssets())
            {
                var pending = s.ImportImage(f3, probe);
                s.ApplyToPage("t", p => p with { Subtitle = "s" });
                var req3 = s.CreateSaveRequest(projPath, true, null);
                ProjectFile.Save(req3);
                s.CompleteSave(req3);
                Check(s.Assets.Contains(pending.Id) && File.Exists(pending.Source.Path), "an image being imported is not pruned or deleted by a save");
            }
            Check(s.PruneUnreachableAssets() == 1 && s.Assets.Count == 2, "once the hold is released, unused images are pruned as before");

            // --- all or nothing: an image that cannot be read back intact fails the save and leaves the file alone ---
            byte[] before = File.ReadAllBytes(projPath);
            var a3 = s.ImportImage(f3, probe);
            s.ApplyToPage("img", p => p.AddSlot(p.Rows[0].Id, new Slot { Parts = ImmutableList.Create(new Part { ImageId = a3.Id }) }));
            var working = s.Assets.Get(a3.Id)!.Source.Path;
            byte[] good = File.ReadAllBytes(working);
            byte[] bad = (byte[])good.Clone();
            bad[^1] ^= 0xFF;
            File.WriteAllBytes(working, bad);
            var reqBad = s.CreateSaveRequest(projPath, true, null);
            Check(Throws<IOException>(() => ProjectFile.Save(reqBad)), "a changed image fails the save");
            Check(File.ReadAllBytes(projPath).AsSpan().SequenceEqual(before) && !File.Exists(projPath + ".tmp"), "a failed save leaves the project file, and nothing else, behind");
            File.Delete(working);
            Check(Throws<IOException>(() => ProjectFile.Save(s.CreateSaveRequest(projPath, true, null))), "a missing image fails the save");
            Check(File.ReadAllBytes(projPath).AsSpan().SequenceEqual(before), "…and the project file is still the previous one");
            File.WriteAllBytes(working, good);

            // --- once the image is back, the next save goes through ---
            var reqOk = s.CreateSaveRequest(projPath, true, null);
            ProjectFile.Save(reqOk);
            s.CompleteSave(reqOk);
            Check(File.Exists(projPath) && !File.ReadAllBytes(projPath).AsSpan().SequenceEqual(before), "the next save replaces the project file");
            Check(ReadAll(s.Assets, s.Assets.Get(a3.Id)!).AsSpan().SequenceEqual(good) && ReadAll(s.Assets, s.Assets.Get(a1.Id)!).AsSpan().SequenceEqual(File.ReadAllBytes(f1)), "assets read correctly from the new file");

            // --- a copy must never replace the file the open project lives in ---
            Check(Throws<IOException>(() => ProjectFile.Save(s.CreateSaveRequest(projPath, relocate: false, null))), "Save a Copy onto the project's own file is refused");

            // --- redo shows the page the change led to, wherever the user has gone since ---
            string first = s.CurrentPageId;
            var second = Factory.NewPage();
            s.Apply("Add page", p => p.AddPage(second), selectPageId: second.Id);
            s.ApplyToPage("Subtitle", p => p with { Subtitle = "on second" });
            s.Undo();
            s.SelectPage(first);
            s.Redo();
            Check(s.CurrentPageId == second.Id && s.CurrentPage.Subtitle == "on second", "redo navigates to the edited page");
            s.Undo(); s.Undo();
            Check(s.CurrentPageId == first, "undo of 'add page' returns to the page it was added from");
            s.Redo();
            Check(s.CurrentPageId == second.Id, "redo of 'add page' shows the new page");

            var late = s.CreateRecoverySnapshot();
            string dir = s.Recovery.Dir;
            s.Close(keepRecovery: false);
            Check(!s.Recovery.WriteSnapshot(late) && !Directory.Exists(dir), "a snapshot arriving after the session closed does not recreate its folder");
        }

        // --- files that were edited by hand or damaged ---
        var dup = new Page { Id = "same", Rows = ImmutableList.Create(new Section { Id = "r", Slots = ImmutableList.Create(new Slot { Id = "s" }, new Slot { Id = "s" }) }, new Section { Id = "r" }) };
        var norm = ProjectOps.Normalize(new Project { Pages = ImmutableList.Create(dup, dup) });
        Check(norm.Pages.Select(p => p.Id).Distinct().Count() == 2, "duplicate page ids are made unique");
        var np = norm.Pages[0];
        var ids = np.AllSections().Select(x => x.Id).Concat(np.AllSections().SelectMany(x => x.Slots).Select(x => x.Id))
            .Concat(np.AllSections().SelectMany(x => x.Slots).SelectMany(x => x.Parts).Select(x => x.Id)).ToList();
        Check(ids.Count == ids.Distinct().Count(), "duplicate row, slot and part ids are made unique within a page");
        Check(new Sizing { Height = 1e12 }.EffectiveHeight == Sizing.MaxHeightLimit && new Sizing { Height = double.NaN }.EffectiveHeight == 440 && new Sizing { MaxHeight = 3 }.EffectiveMaxHeight == Sizing.MinMaxHeight, "absurd sizes are clamped");

        string junk = Path.Combine(_work, "junk.speciesproj");
        var bytes = new byte[4096];
        new Random(5).NextBytes(bytes);
        bytes[0] = (byte)'P'; bytes[1] = (byte)'K';
        File.WriteAllBytes(junk, bytes);
        Check(Throws<InvalidProjectException>(() => ProjectFile.Load(junk)), "a damaged archive is reported as an invalid project");
        var legacyHuge = new byte[64];
        Encoding.ASCII.GetBytes("SPSHEET1").CopyTo(legacyHuge, 0);
        BinaryPrimitives.WriteUInt32BigEndian(legacyHuge.AsSpan(8), 0xFFFFFFF0);
        File.WriteAllBytes(junk, legacyHuge);
        Check(Throws<InvalidProjectException>(() => ProjectFile.Load(junk)), "a legacy file with an impossible JSON length is rejected without allocating it");
    }

    private static void ReviewLayout()
    {
        var shaper = new FakeShaper();
        var body = new FontSpec(FontFamilyId.Body, 400, false, 20);

        // --- pretty wrapping never rebuilds a word that overflow-wrap had to split ---
        var box = new InlineBox(shaper, new[] { new InlineSpan("aaaa aaaa abcdefghijklmnopqrstuvwxyz aaaa b", body, default) });
        var pretty = box.Break(140, pretty: true);
        Check(pretty.All(l => box.LineWidth(l) <= 140.01), "pretty wrapping keeps every line inside the column");

        // --- collapsed white space belongs to the run it started in ---
        var layouter = new MarkdownLayouter(shaper, default, default, new List<SceneItem>());
        var bold = body with { Weight = 700 };
        var spans = layouter.Flatten(new MdInline[]
        {
            new MdText("a "), new MdEmphasis(MdEmphasisKind.Bold, new MdInline[] { new MdText("b") }), new MdText("  c"),
            new MdEmphasis(MdEmphasisKind.Bold, new MdInline[] { new MdText("two words") }),
        }, body, default, false);
        string Describe(InlineSpan x) => (x.Font.Weight >= 700 ? "B" : "R") + "[" + x.Text + "]";
        Check(string.Join("", spans.Select(Describe)) == "R[a ]B[b]R[ c]B[two words]", "the space after a bold run is regular, the space inside one is bold");

        // --- upper-casing follows CSS text-transform where ToUpperInvariant does not ---
        Check(TextCase.Upper("stra\u00DFe \uFB01sh") == "STRASSE FISH" && TextCase.Upper("plain") == "PLAIN", "upper-casing expands sharp s and ligatures");

        // --- snapping is translation-invariant, so an edge lands on the same pixel in every export strip ---
        bool invariant = true;
        for (int i = 0; i < 2000 && invariant; i++)
        {
            double v = i * 0.25 - 100;
            for (int n = -7; n <= 7; n++) invariant &= PixelGrid.Round(v + n) == PixelGrid.Round(v) + n;
        }
        Check(invariant && PixelGrid.Round(0.5) == 1 && PixelGrid.Round(1.5) == 2 && PixelGrid.Round(-0.5) == 0, "pixel rounding is half-up and translation-invariant");

        var img = new ImagePlacement { ImageId = "x", SectionId = "s", SlotId = "sl", PartId = "p", AlignBottom = true, Container = new RectD(10.5, 20.5, 101, 77), Drawn = new RectD(10.5, 20.5, 101, 77), FileWidth = 300, FileHeight = 200 };
        bool sameInEveryStrip = true;
        foreach (double scale in new[] { 1.0, 1.5, 2.37, (double)(float)13.636363 })
        {
            var whole = PixelGrid.PlaceImage(img, scale);
            for (int stripTop = 0; stripTop < 40; stripTop++)
            {
                var inStrip = PixelGrid.PlaceImage(img, scale, 0, -stripTop);
                sameInEveryStrip &= inStrip.X == whole.X && inStrip.Y == whole.Y - stripTop && inStrip.W == whole.W && inStrip.H == whole.H;
            }
            var planned = ExportPlanner.OutputSize(img, scale);
            sameInEveryStrip &= planned == (whole.W, whole.H);
        }
        Check(sameInEveryStrip, "an image has the same size and place in every strip, and the planner reports that size");

        // --- a row with far more slots than fit still gives every slot a positive width ---
        var many = new Section { Name = "M", Slots = Enumerable.Range(0, 400).Select(_ => new Slot()).ToImmutableList() };
        var L = Engine(new()).Layout(Factory.NewPage() with { Rows = ImmutableList.Create(many), Lore = ImmutableList<LoreSection>.Empty }, SheetStyle.Default);
        Check(L.Rows[0].Slots.All(sl => sl.Area.W > 0) && L.Rows[0].Slots[^1].Area.Right <= L.Rows[0].Box.Right + 0.01, "400 slots: positive widths, inside the row");

        // --- strips always fit one buffer ---
        Check((long)ExportPlanner.StripRows(400_000_000, 16, 1) * 400_000_000 * 4 <= int.MaxValue, "strip rows respect the 2 GB buffer limit at extreme widths");
    }

    private static void Zip64()
    {
        // A sparse >4 GB "image" forces ZIP64 size and offset fields for itself and the entries after it.
        string big = Path.Combine(_work, "big.png");
        const long size = (4L << 30) + 12_345;
        using (var fs = new FileStream(big, FileMode.Create, FileAccess.Write))
        {
            fs.Write(new byte[] { (byte)'F', 9, 9, 0 });
            fs.SetLength(size);
            fs.Position = size - 4;
            fs.Write(new byte[] { 1, 2, 3, 4 });
        }
        string small = MakeImage("after.png", 3, 3, 4096, 77);
        string path = Path.Combine(_work, "Big.speciesproj");
        using var s = DocumentSession.CreateNew(Factory.NewProject(), Path.Combine(_work, "rec-big"));
        var probe = new FakeProbe();
        // Register the big file in place (importing would copy and hash 4 GB a second time).
        s.Assets.Put(new ImageAsset { Id = "aaa_big", FileName = "big.png", MimeType = "image/png", PixelWidth = 90, PixelHeight = 90, ByteLength = size, Source = new AssetSource(big, 0, size) });
        var after = s.ImportImage(small, probe);
        string pa = s.CurrentPage.Rows[0].Slots[0].Parts[0].Id, pb = s.CurrentPage.Rows[0].Slots[1].Parts[0].Id;
        s.ApplyToPage("img", p => p.SetPartImage(pa, "aaa_big").SetPartImage(pb, after.Id));
        var req = s.CreateSaveRequest(path, true, null);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        ProjectFile.Save(req);
        s.CompleteSave(req);
        Console.WriteLine($"  saved {new FileInfo(path).Length / 1048576.0:F0} MB in {sw.Elapsed.TotalSeconds:F1} s, managed heap {GC.GetTotalMemory(false) / 1048576.0:F0} MB");
        Check(GC.GetTotalMemory(false) < 200L << 20, "saving a >4 GB image does not load it into memory");
        var bigAsset = s.Assets.Get("aaa_big")!;
        Check(bigAsset.Source.Path == path && bigAsset.Source.Length == size, "zip64 entry indexed");
        using (var stream = s.Assets.OpenRead(bigAsset))
        {
            var head = new byte[4];
            stream.ReadExactly(head);
            stream.Seek(-4, SeekOrigin.End);
            var tail = new byte[4];
            stream.ReadExactly(tail);
            Check(head[0] == (byte)'F' && head[1] == 9 && tail.AsSpan().SequenceEqual(new byte[] { 1, 2, 3, 4 }), "zip64 entry bytes at both ends");
        }
        Check(ReadAll(s.Assets, s.Assets.Get(after.Id)!).AsSpan().SequenceEqual(File.ReadAllBytes(small)), "entry located after 4 GB reads correctly");
        var re = ProjectFile.Load(path);
        Check(re.Assets.Count == 2 && re.Assets.First(a => a.Id == "aaa_big").Source.Length == size, "zip64 project reloads");
    }
}
