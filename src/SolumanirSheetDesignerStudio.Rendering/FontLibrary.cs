using System.Runtime.InteropServices;
using HarfBuzzSharp;
using SkiaSharp;
using SolumanirSheetDesignerStudio.Core.Layout;
using HbBuffer = HarfBuzzSharp.Buffer;
using HbFont = HarfBuzzSharp.Font;

namespace SolumanirSheetDesignerStudio.Rendering;

/// <summary>
/// The sheet's fonts: bundled Cinzel / Alegreya / Alegreya Sans / Cascadia Mono (embedded resources) with system fallbacks, shaped
/// with HarfBuzz so kerning and ligatures match a browser. Thread-safe.
/// </summary>
public sealed class FontLibrary : ITextShaper, IDisposable
{
    private sealed class FaceEntry
    {
        public required SKTypeface Typeface { get; init; }
        public required Face HbFace { get; init; }
        public required HbFont HbFont { get; init; }
        public required int UnitsPerEm { get; init; }
    }

    private sealed record Bundled(string Family, int Weight, bool Italic, int FaceId);

    private static readonly Feature[] NoLigatures =
    {
        new(new Tag('l', 'i', 'g', 'a'), 0u, 0u, uint.MaxValue),
        new(new Tag('c', 'l', 'i', 'g'), 0u, 0u, uint.MaxValue),
    };

    private readonly object _lock = new();
    private readonly List<FaceEntry> _faces = new();
    private readonly List<Bundled> _bundled = new();
    private readonly Dictionary<(FontFamilyId, int, bool), int> _resolved = new();
    private readonly Dictionary<IntPtr, int> _byHandle = new();
    private readonly Dictionary<(int Face, int CodePoint), int> _fallback = new();
    private readonly Dictionary<(string, FontSpec), ShapedText> _shapeCache = new();
    private readonly Dictionary<(int, double), FontMetricsInfo> _metricsCache = new();
    private readonly List<string> _missing = new();

    public FontLibrary()
    {
        LoadBundled();
        foreach (var (family, name) in new[] { ("Cinzel", "Cinzel"), ("Alegreya", "Alegreya"), ("AlegreyaSans", "Alegreya Sans"), ("CascadiaMono", "Cascadia Mono") })
            if (!_bundled.Any(b => b.Family == family)) _missing.Add(name);
    }

    /// <summary>Bundled families that were not found in the build (the sheet then falls back to system fonts).</summary>
    public IReadOnlyList<string> MissingFonts => _missing;

    // ---------- loading ----------

    private void LoadBundled()
    {
        var asm = typeof(FontLibrary).Assembly;
        foreach (string res in asm.GetManifestResourceNames())
        {
            if (!res.StartsWith("Fonts/", StringComparison.Ordinal)) continue;
            string file = Path.GetFileNameWithoutExtension(res["Fonts/".Length..]);
            try
            {
                using var stream = asm.GetManifestResourceStream(res);
                if (stream == null) continue;
                using var data = SKData.Create(stream);
                var typeface = SKTypeface.FromData(data);
                if (typeface == null) continue;
                // File names follow Google Fonts' static instances: Family-Style.ttf (Cinzel-Bold, AlegreyaSans-Medium, Alegreya-BoldItalic…).
                int dash = file.IndexOf('-');
                string family = (dash < 0 ? file : file[..dash]).Replace(" ", "");
                string style = dash < 0 ? "Regular" : file[(dash + 1)..];
                _bundled.Add(new Bundled(family, WeightFromStyle(style), style.Contains("Italic", StringComparison.OrdinalIgnoreCase), Register(typeface)));
            }
            catch (Exception)
            {
                // A damaged font file must not stop the app; the family falls back to a system font.
            }
        }
    }

    private static int WeightFromStyle(string style)
    {
        string s = style.Replace("Italic", "", StringComparison.OrdinalIgnoreCase).ToLowerInvariant();
        return s switch
        {
            "thin" => 100, "extralight" => 200, "light" => 300, "" or "regular" => 400, "medium" => 500,
            "semibold" => 600, "bold" => 700, "extrabold" => 800, "black" => 900, _ => 400,
        };
    }

    private int Register(SKTypeface typeface)
    {
        if (_byHandle.TryGetValue(typeface.Handle, out int existing)) return existing;
        var face = new Face((_, tag) => GetTable(typeface, tag)) { UnitsPerEm = typeface.UnitsPerEm };
        var font = new HbFont(face);
        font.SetFunctionsOpenType();
        _faces.Add(new FaceEntry { Typeface = typeface, HbFace = face, HbFont = font, UnitsPerEm = Math.Max(1, typeface.UnitsPerEm) });
        _byHandle[typeface.Handle] = _faces.Count - 1;
        return _faces.Count - 1;
    }

    private static Blob? GetTable(SKTypeface typeface, Tag tag)
    {
        int size = typeface.GetTableSize(tag);
        if (size <= 0) return null;
        IntPtr data = Marshal.AllocCoTaskMem(size);
        if (!typeface.TryGetTableData(tag, 0, size, data))
        {
            Marshal.FreeCoTaskMem(data);
            return null;
        }
        return new Blob(data, size, MemoryMode.ReadOnly, () => Marshal.FreeCoTaskMem(data));
    }

    // ---------- resolution ----------

    private int Resolve(FontSpec spec)
    {
        var key = (spec.Family, spec.Weight, spec.Italic);
        if (_resolved.TryGetValue(key, out int id)) return id;

        string? bundledFamily = spec.Family switch
        {
            FontFamilyId.Display => "Cinzel",
            FontFamilyId.Body => "Alegreya",
            FontFamilyId.Label => "AlegreyaSans",
            FontFamilyId.Mono => "CascadiaMono",
            _ => null,
        };
        var candidates = bundledFamily == null ? new List<Bundled>() : _bundled.Where(b => b.Family == bundledFamily).ToList();
        if (candidates.Count > 0)
        {
            // Prefer the requested slant, then the nearest weight.
            var pool = candidates.Where(c => c.Italic == spec.Italic).ToList();
            if (pool.Count == 0) pool = candidates;
            id = pool.OrderBy(c => Math.Abs(c.Weight - spec.Weight)).ThenByDescending(c => c.Weight).First().FaceId;
        }
        else
        {
            id = Register(SystemTypeface(spec));
        }
        _resolved[key] = id;
        return id;
    }

    private static SKTypeface SystemTypeface(FontSpec spec)
    {
        string[] names = spec.Family switch
        {
            FontFamilyId.Display => new[] { "Cinzel", "Trajan Pro", "Cormorant SC", "Georgia", "Times New Roman", "DejaVu Serif", "Liberation Serif", "serif" },
            FontFamilyId.Body => new[] { "Alegreya", "Georgia", "Palatino Linotype", "Times New Roman", "DejaVu Serif", "Liberation Serif", "serif" },
            FontFamilyId.Label => new[] { "Alegreya Sans", "Segoe UI", "Helvetica Neue", "Noto Sans", "DejaVu Sans", "Liberation Sans", "sans-serif" },
            _ => new[] { "Cascadia Mono", "Consolas", "SF Mono", "Menlo", "DejaVu Sans Mono", "Liberation Mono", "Noto Sans Mono", "Courier New", "monospace" },
        };
        var style = new SKFontStyle(spec.Weight, (int)SKFontStyleWidth.Normal, spec.Italic ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright);
        var manager = SKFontManager.Default;
        foreach (string name in names)
        {
            var tf = manager.MatchFamily(name, style);
            if (tf == null) continue;
            if (spec.Family == FontFamilyId.Mono && !tf.IsFixedPitch) continue; // fontconfig may substitute a proportional face
            return tf;
        }
        return SKTypeface.Default;
    }

    /// <summary>Face for a code point the primary face lacks, or the primary face itself when nothing has it.</summary>
    private int FallbackFor(int primary, int codePoint, FontSpec spec)
    {
        if (_fallback.TryGetValue((primary, codePoint), out int id)) return id;
        id = primary;
        try
        {
            var style = new SKFontStyle(spec.Weight, (int)SKFontStyleWidth.Normal, spec.Italic ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright);
            var tf = SKFontManager.Default.MatchCharacter(null, style, null, codePoint);
            if (tf != null && tf.GetGlyph(codePoint) != 0) id = Register(tf);
        }
        catch (Exception)
        {
            // keep the primary face: the glyph renders as "missing" instead of failing the layout
        }
        _fallback[(primary, codePoint)] = id;
        return id;
    }

    // ---------- ITextShaper ----------

    public FontMetricsInfo GetMetrics(FontSpec font)
    {
        lock (_lock)
        {
            int id = Resolve(font);
            if (_metricsCache.TryGetValue((id, font.Size), out var cached)) return cached;
            using var skFont = new SKFont(_faces[id].Typeface, (float)font.Size) { LinearMetrics = true, Hinting = SKFontHinting.None };
            var m = skFont.Metrics;
            var info = new FontMetricsInfo(-m.Ascent, m.Descent);
            _metricsCache[(id, font.Size)] = info;
            return info;
        }
    }

    public ShapedText Shape(string text, FontSpec font)
    {
        if (string.IsNullOrEmpty(text)) return ShapedText.Empty;
        lock (_lock)
        {
            // Letter-spacing only toggles ligatures; the advance itself is added by the layout.
            var key = (text, font with { LetterSpacing = font.LetterSpacing != 0 ? 1 : 0 });
            if (_shapeCache.TryGetValue(key, out var cached)) return cached;
            if (_shapeCache.Count > 20000) _shapeCache.Clear();
            var result = ShapeCore(text, font);
            _shapeCache[key] = result;
            return result;
        }
    }

    private ShapedText ShapeCore(string text, FontSpec font)
    {
        int primary = Resolve(font);
        var primaryFace = _faces[primary].Typeface;

        // Split into runs by typeface (font fallback for characters the primary face lacks).
        var runs = new List<(int Start, int Length, int Face)>();
        int runStart = 0, runFace = primary, i = 0;
        while (i < text.Length)
        {
            bool pair = char.IsSurrogatePair(text, i);
            int cp = pair ? char.ConvertToUtf32(text[i], text[i + 1]) : text[i];
            int len = pair ? 2 : 1;
            int face = runFace;
            if (!IsNeutral(cp))
                face = primaryFace.GetGlyph(cp) != 0 ? primary : FallbackFor(primary, cp, font);
            if (face != runFace)
            {
                if (i > runStart) runs.Add((runStart, i - runStart, runFace));
                runStart = i;
                runFace = face;
            }
            i += len;
        }
        if (text.Length > runStart) runs.Add((runStart, text.Length - runStart, runFace));

        var glyphs = new List<ushort>(text.Length);
        var clusters = new List<int>(text.Length);
        var advances = new List<float>(text.Length);
        var offX = new List<float>(text.Length);
        var offY = new List<float>(text.Length);
        var faces = new List<int>(text.Length);
        var features = font.LetterSpacing != 0 ? NoLigatures : Array.Empty<Feature>();

        foreach (var (start, length, faceId) in runs)
        {
            var entry = _faces[faceId];
            using var buffer = new HbBuffer();
            buffer.AddUtf16(text.AsSpan(), start, length); // full text as context; clusters are indices into it
            buffer.GuessSegmentProperties();
            buffer.Direction = Direction.LeftToRight; // the sheet lays text out left to right only
            entry.HbFont.Shape(buffer, features);

            var infos = buffer.GlyphInfos;
            var positions = buffer.GlyphPositions;
            float scale = (float)(font.Size / entry.UnitsPerEm);
            for (int g = 0; g < infos.Length; g++)
            {
                glyphs.Add((ushort)infos[g].Codepoint);
                clusters.Add((int)infos[g].Cluster);
                advances.Add(positions[g].XAdvance * scale);
                offX.Add(positions[g].XOffset * scale);
                offY.Add(positions[g].YOffset * scale);
                faces.Add(faceId);
            }
        }
        return new ShapedText(glyphs.ToArray(), clusters.ToArray(), advances.ToArray(), offX.ToArray(), offY.ToArray(), faces.ToArray());
    }

    /// <summary>Characters that stay in the surrounding run whatever the face (spaces, joiners, marks, variation selectors).</summary>
    private static bool IsNeutral(int cp) =>
        cp <= 0x20 || cp == 0xA0 || cp == 0x200B || cp == 0x200C || cp == 0x200D || cp == 0xFEFF ||
        (cp >= 0x0300 && cp <= 0x036F) || (cp >= 0xFE00 && cp <= 0xFE0F);

    // ---------- painting support ----------

    /// <summary>A Skia font for a shaped run. Missing bold / italic styles are synthesised. The caller disposes it.</summary>
    public SKFont CreateSkFont(FontSpec spec, int faceId)
    {
        SKTypeface typeface;
        lock (_lock) typeface = _faces[Math.Clamp(faceId, 0, _faces.Count - 1)].Typeface;
        return new SKFont(typeface, (float)spec.Size)
        {
            Subpixel = true,
            LinearMetrics = true,
            Hinting = SKFontHinting.None,
            Edging = SKFontEdging.Antialias,
            Embolden = spec.Weight >= 600 && typeface.FontWeight < 550,
            SkewX = spec.Italic && typeface.FontSlant == SKFontStyleSlant.Upright ? -0.2f : 0,
        };
    }

    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var f in _faces)
            {
                f.HbFont.Dispose();
                f.HbFace.Dispose();
            }
            _faces.Clear();
            _shapeCache.Clear();
        }
    }
}
