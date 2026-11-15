using System.Text;

namespace SolumanirSheetDesignerStudio.Core.Layout;

/// <summary>CSS <c>text-transform: uppercase</c> for the cases plain <see cref="string.ToUpperInvariant"/> misses.</summary>
public static class TextCase
{
    // One lower-case character that becomes several capitals (escapes: most of these are ligatures that look like ASCII).
    private static readonly (char From, string To)[] Expansions =
    {
        ('\u00DF', "SS"), ('\u0149', "\u02BCN"),
        ('\uFB00', "FF"), ('\uFB01', "FI"), ('\uFB02', "FL"), ('\uFB03', "FFI"), ('\uFB04', "FFL"), ('\uFB05', "ST"), ('\uFB06', "ST"),
    };

    public static string Upper(string text)
    {
        string upper = text.ToUpperInvariant();
        if (upper.AsSpan().IndexOfAny("\u00DF\u0149\uFB00\uFB01\uFB02\uFB03\uFB04\uFB05\uFB06") < 0) return upper;
        var sb = new StringBuilder(upper.Length + 4);
        foreach (char c in upper)
        {
            string? expanded = null;
            foreach (var (from, to) in Expansions)
                if (from == c) { expanded = to; break; }
            if (expanded != null) sb.Append(expanded);
            else sb.Append(c);
        }
        return sb.ToString();
    }
}
