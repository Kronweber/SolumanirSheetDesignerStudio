using System.Globalization;
using System.Text;
using Markdig;
using Markdig.Extensions.EmphasisExtras;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using SolumanirSheetDesignerStudio.Core.Layout;

namespace SolumanirSheetDesignerStudio.Rendering;

/// <summary>
/// CommonMark + pipe tables + strikethrough via Markdig, converted to the sheet's own block model
/// (the sheet draws the text itself; no HTML is involved). Parsed results are cached.
/// </summary>
public sealed class MarkdigParser : IMarkdownParser
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseEmphasisExtras(EmphasisExtraOptions.Strikethrough)
        .Build();

    private readonly object _lock = new();
    private readonly Dictionary<string, IReadOnlyList<MdBlock>> _cache = new();

    public IReadOnlyList<MdBlock> Parse(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return Array.Empty<MdBlock>();
        lock (_lock)
            if (_cache.TryGetValue(markdown, out var cached)) return cached;

        IReadOnlyList<MdBlock> blocks;
        try
        {
            blocks = Blocks(Markdown.Parse(markdown, Pipeline));
        }
        catch (Exception)
        {
            // Never let odd input break the sheet: show the source as plain text.
            blocks = new MdBlock[] { new MdParagraph(new MdInline[] { new MdText(markdown) }) };
        }

        lock (_lock)
        {
            if (_cache.Count > 600) _cache.Clear();
            _cache[markdown] = blocks;
        }
        return blocks;
    }

    private static List<MdBlock> Blocks(ContainerBlock container)
    {
        var result = new List<MdBlock>();
        foreach (var block in container)
        {
            switch (block)
            {
                case HeadingBlock h:
                    result.Add(new MdHeading(h.Level, Inlines(h.Inline)));
                    break;
                case ParagraphBlock p:
                    result.Add(new MdParagraph(Inlines(p.Inline)));
                    break;
                case ListBlock l:
                {
                    int start = 1;
                    if (l.IsOrdered && !int.TryParse(l.OrderedStart, NumberStyles.Integer, CultureInfo.InvariantCulture, out start)) start = 1;
                    var items = new List<IReadOnlyList<MdBlock>>();
                    foreach (var item in l)
                        if (item is ListItemBlock li) items.Add(Blocks(li));
                    result.Add(new MdList(l.IsOrdered, start, !l.IsLoose, items));
                    break;
                }
                case QuoteBlock q:
                    result.Add(new MdQuote(Blocks(q)));
                    break;
                case ThematicBreakBlock:
                    result.Add(new MdRule());
                    break;
                case Table t:
                    result.Add(ConvertTable(t));
                    break;
                case CodeBlock c: // fenced and indented
                    result.Add(new MdCodeBlock(c.Lines.ToString()));
                    break;
                case HtmlBlock html: // raw HTML is shown as text; the sheet has no HTML renderer
                    result.Add(new MdParagraph(new MdInline[] { new MdText(html.Lines.ToString()) }));
                    break;
                case LinkReferenceDefinitionGroup:
                    break;
                case ContainerBlock other:
                    result.AddRange(Blocks(other));
                    break;
                case LeafBlock leaf when leaf.Inline != null:
                    result.Add(new MdParagraph(Inlines(leaf.Inline)));
                    break;
            }
        }
        return result;
    }

    private static MdTable ConvertTable(Table table)
    {
        IReadOnlyList<IReadOnlyList<MdInline>> header = Array.Empty<IReadOnlyList<MdInline>>();
        var rows = new List<IReadOnlyList<IReadOnlyList<MdInline>>>();
        foreach (var rowBlock in table)
        {
            if (rowBlock is not TableRow row) continue;
            var cells = new List<IReadOnlyList<MdInline>>();
            foreach (var cellBlock in row)
            {
                if (cellBlock is not TableCell cell) continue;
                var inlines = new List<MdInline>();
                foreach (var inner in cell)
                {
                    if (inner is not LeafBlock { Inline: not null } leaf) continue;
                    if (inlines.Count > 0) inlines.Add(new MdBreak());
                    inlines.AddRange(Inlines(leaf.Inline));
                }
                cells.Add(inlines);
            }
            if (row.IsHeader && header.Count == 0) header = cells;
            else rows.Add(cells);
        }
        return new MdTable(header, rows);
    }

    /// <summary>Inlines nested deeper than this keep their text and lose their styling, which bounds the recursion here and in the layout.</summary>
    private const int DepthNestingMax = 100;

    private static List<MdInline> Inlines(ContainerInline? container, int depth = 0)
    {
        var result = new List<MdInline>();
        if (container == null) return result;
        if (depth > DepthNestingMax)
        {
            AddText(result, PlainText(container));
            return result;
        }
        for (var inline = container.FirstChild; inline != null; inline = inline.NextSibling)
        {
            switch (inline)
            {
                case LiteralInline lit:
                    AddText(result, lit.Content.ToString());
                    break;
                case EmphasisInline em:
                    var kind = em.DelimiterChar == '~' ? MdEmphasisKind.Strike : em.DelimiterCount >= 2 ? MdEmphasisKind.Bold : MdEmphasisKind.Italic;
                    result.Add(new MdEmphasis(kind, Inlines(em, depth + 1)));
                    break;
                case CodeInline code:
                    result.Add(new MdCode(code.Content));
                    break;
                case LinkInline { IsImage: true } image: // images inside Markdown are out of scope: keep the alt text
                    AddText(result, PlainText(image));
                    break;
                case LinkInline link:
                    result.Add(new MdLink(link.Url ?? "", Inlines(link, depth + 1)));
                    break;
                case AutolinkInline auto:
                    result.Add(new MdLink(auto.Url, new MdInline[] { new MdText(auto.Url) }));
                    break;
                case LineBreakInline:
                    result.Add(new MdBreak()); // a single newline is a line break on the sheet
                    break;
                case HtmlEntityInline entity:
                    AddText(result, entity.Transcoded.ToString());
                    break;
                case HtmlInline html:
                    AddText(result, html.Tag);
                    break;
                case DelimiterInline delimiter:
                    AddText(result, delimiter.ToLiteral());
                    result.AddRange(Inlines(delimiter, depth + 1));
                    break;
                case ContainerInline other:
                    result.AddRange(Inlines(other, depth + 1));
                    break;
                default:
                    AddText(result, inline.ToString() ?? "");
                    break;
            }
        }
        return result;
    }

    private static void AddText(List<MdInline> list, string text)
    {
        if (text.Length == 0) return;
        if (list.Count > 0 && list[^1] is MdText previous) list[^1] = new MdText(previous.Text + text);
        else list.Add(new MdText(text));
    }

    private static string PlainText(ContainerInline container)
    {
        var sb = new StringBuilder();
        foreach (var inline in container.Descendants())
            if (inline is LiteralInline lit) sb.Append(lit.Content.ToString());
        return sb.ToString();
    }
}
