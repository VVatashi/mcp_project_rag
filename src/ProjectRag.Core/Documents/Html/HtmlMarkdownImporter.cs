using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;

namespace ProjectRag.Core.Documents.Html;

/// <summary>
/// Turns an HTML page into Markdown with the marks of the anchors.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>The headings, the paragraphs, the lists, the tables, the code and the quotes keep their kind.</item>
/// <item>A term of a definition list is a paragraph. A list with a long definition, such as a list of formats with their mappings, makes each term a heading one level below the section.
/// The text after such a list stays in the section of its last term.</item>
/// <item>The scripts, the styles, the navigation, the table of contents and the footer of the page go away.</item>
/// <item>An element whose id a link of the page names gets the mark <c>&lt;!-- rag:anchor id --&gt;</c>, and so does each heading with an id.
/// A link to an anchor of the page stays a link, so the chunks link to each other as in a PDF file.</item>
/// <item>The formulas of LaTeX for MathJax, <c>\( … \)</c> in the text and <c>\[ … \]</c> in a stem block of Asciidoctor, become <c>$ … $</c> and <c>$$ … $$</c>.</item>
/// </list>
/// </remarks>
internal static partial class HtmlMarkdownImporter
{
    private static readonly HashSet<string> BlockTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "address", "article", "aside", "blockquote", "caption", "dd", "details", "dialog", "div", "dl", "dt", "fieldset", "figcaption", "figure",
        "footer", "h1", "h2", "h3", "h4", "h5", "h6", "header", "hgroup", "hr", "li", "main", "nav", "ol", "p", "pre", "section", "summary", "table", "td", "th", "tr", "ul",
    };

    private static readonly string BlockSelector = string.Join(", ", BlockTags);

    private static readonly HashSet<string> SkippedTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "button", "canvas", "embed", "footer", "form", "head", "iframe", "img", "input", "nav", "noscript", "object", "script", "select", "style", "svg", "template", "textarea",
    };

    /// <summary>
    /// Reads an HTML page. The parser takes the encoding from the byte order mark or the meta tag of the page.
    /// </summary>
    /// <param name="bytes">The bytes of the page.</param>
    /// <param name="fallbackTitle">The title when the page has none.</param>
    /// <param name="address">The address of a web page after its redirects, which turns its relative links into absolute ones. <see langword="null"/> for a file.</param>
    /// <returns>The text of the document in Markdown.</returns>
    public static DocumentText Read(byte[] bytes, string fallbackTitle, Uri? address)
    {
        HtmlParser parser = new();
        using MemoryStream stream = new(bytes, writable: false);
        using IHtmlDocument document = parser.ParseDocument(stream);
        string content = new Writer(document, address).Write();
        string title = Collapse(document.Title ?? string.Empty);
        if (title.Length == 0)
            title = MarkdownStructureReader.FindTitle(content) ?? fallbackTitle;

        return new DocumentText(title, content);
    }

    private static string Collapse(string text) => WhitespaceRegex().Replace(text, " ").Trim();

    private static bool IsBlock(IElement element) => BlockTags.Contains(element.LocalName);

    // A wrapper outside the known tags, such as a custom element, is a block when it holds blocks.
    // A link around a card of text stays a link. Only a heading, a list, a table or code inside it makes it a block.
    private static bool HoldsBlocks(IElement element) =>
        element.QuerySelector(element.LocalName == "a" ? "h1, h2, h3, h4, h5, h6, ul, ol, dl, table, pre" : BlockSelector) is not null;

    // The navigation and the table of contents repeat the headings, and the footer holds the dates of the build.
    private static bool IsSkipped(IElement element) =>
        SkippedTags.Contains(element.LocalName) || element.Id is "toc" or "footer" || element.ClassList.Contains("toc") || element.ClassList.Contains("toc2");

    // MathJax takes "_" inside \textit{num_levels}, but KaTeX wants "\_" in its text mode.
    private static string TextUnderscores(string latex) =>
        TextCommandRegex().Replace(latex, static match => match.Value.Replace(@"\_", "_", StringComparison.Ordinal).Replace("_", @"\_", StringComparison.Ordinal));

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"\\(?:text(?:it|tt|bf|rm|sf|up|sl|sc|normal)?|mbox|emph)\{[^{}]*\}")]
    private static partial Regex TextCommandRegex();

    // The delimiters of MathJax: \( … \) inside a line and \[ … \] for a display formula.
    [GeneratedRegex(@"\\\((?<math>.+?)\\\)", RegexOptions.Singleline)]
    private static partial Regex InlineMathRegex();

    [GeneratedRegex(@"^\\\[(?<math>[\s\S]*)\\\]$")]
    private static partial Regex DisplayMathRegex();

    [GeneratedRegex(@"(?:^|\s)language-(?<language>[\w+#.-]+)")]
    private static partial Regex LanguageRegex();

    /// <summary>
    /// Writes the blocks of one part of a page. A list item, a quote and a cell of a table get a writer of their own,
    /// which shares the anchors with the writer of the page, so the marks stay at the start of a line outside the nested block.
    /// </summary>
    private sealed class Writer
    {
        private readonly List<string> _blocks = [];
        private readonly HashSet<string> _targets;
        private readonly HashSet<string> _written;
        private readonly List<string> _pending;
        private readonly Uri? _address;
        private readonly Uri? _linkBase;
        private readonly bool _nested;
        private readonly bool _inTable;
        private readonly IHtmlDocument _document;
        private int _level = 1;

        public Writer(IHtmlDocument document, Uri? address)
        {
            _document = document;
            _address = address;
            // The element base may move the relative links of a web page to another folder.
            _linkBase = address is not null && document.QuerySelector("base[href]")?.GetAttribute("href") is { } baseHref && Uri.TryCreate(address, baseHref, out Uri? moved) ? moved : address;
            _targets = new(StringComparer.Ordinal);
            _written = new(StringComparer.Ordinal);
            _pending = [];
            foreach (IElement link in document.QuerySelectorAll("a[href]"))
            {
                if (LocalAnchor(link.GetAttribute("href")!) is { } anchor)
                    _targets.Add(anchor);
            }
        }

        private Writer(Writer parent, bool inTable)
        {
            _document = parent._document;
            _address = parent._address;
            _linkBase = parent._linkBase;
            _targets = parent._targets;
            _written = parent._written;
            _pending = parent._pending;
            _nested = true;
            _inTable = inTable;
        }

        public string Write()
        {
            Blocks(_document.Body ?? _document.DocumentElement);
            Flush();
            return string.Join("\n\n", _blocks) + "\n";
        }

        private List<string> Nested(IElement element, bool inTable = false)
        {
            Writer writer = new(this, inTable || _inTable);
            writer.Blocks(element);
            return writer._blocks;
        }

        // The element itself, not only its children, becomes the blocks: a list keeps its markers.
        private List<string> NestedBlock(IElement element)
        {
            Writer writer = new(this, _inTable);
            writer.Block(element);
            return writer._blocks;
        }

        // A block of the page takes the marks of the anchors before it. A nested block leaves them to the block of the page around it.
        private void Add(string block)
        {
            if (block.Length == 0)
                return;

            if (!_nested && _pending.Count > 0)
            {
                block = string.Concat(_pending.Select(static anchor => $"<!-- rag:anchor {anchor} -->\n")) + block;
                _pending.Clear();
            }

            _blocks.Add(block);
        }

        private void Flush()
        {
            if (_pending.Count > 0)
                _blocks.Add(string.Join('\n', _pending.Select(static anchor => $"<!-- rag:anchor {anchor} -->")));

            _pending.Clear();
        }

        // A mark holds no space and no '>'. Each anchor gets one mark.
        private void Anchor(string? id, bool always = false)
        {
            if (string.IsNullOrEmpty(id) || !(always || _targets.Contains(id)) || id.Any(static c => char.IsWhiteSpace(c) || c == '>') || !_written.Add(id))
                return;

            _pending.Add(id);
        }

        private void Blocks(IElement parent)
        {
            List<INode> run = [];
            foreach (INode node in parent.ChildNodes)
            {
                // A skipped element inside a line, such as an image, leaves the line whole.
                if (node is IElement skipped && IsSkipped(skipped) && !IsBlock(skipped))
                    continue;

                if (node is IElement element && (IsBlock(element) || HoldsBlocks(element)))
                {
                    Paragraph(run);
                    if (!IsSkipped(element))
                        Block(element);
                }
                else
                {
                    run.Add(node);
                }
            }

            Paragraph(run);
        }

        private void Paragraph(List<INode> run)
        {
            string text = Collapse(string.Concat(run.Select(Inline)));
            run.Clear();
            Add(MarkdownText.EscapeStart(text));
        }

        private void Block(IElement element)
        {
            Anchor(element.Id);
            Anchor(element.GetAttribute("name"));
            switch (element.LocalName)
            {
                case "h1" or "h2" or "h3" or "h4" or "h5" or "h6":
                    Heading(element);
                    break;
                case "p" or "dt" or "summary" or "figcaption" or "caption":
                    Add(MarkdownText.EscapeStart(Collapse(Inline(element))));
                    break;
                case "pre":
                    Code(element);
                    break;
                case "ul" or "ol":
                    List(element);
                    break;
                case "dl":
                    Definitions(element);
                    break;
                case "table":
                    Table((IHtmlTableElement)element);
                    break;
                case "blockquote":
                    Add(string.Join("\n>\n", Nested(element).Select(static block => "> " + block.Replace("\n", "\n> ", StringComparison.Ordinal))));
                    break;
                case "hr":
                    break;
                case "div" when element.ClassList.Contains("stemblock"):
                    Formula(element);
                    break;
                // Asciidoctor sets a note as a table of an icon and the text. The label of the note goes away, as in a PDF file.
                case "div" when element.ClassList.Contains("admonitionblock") && element.QuerySelector("td.content") is { } content:
                    Blocks(content);
                    break;
                // The notes of the callouts of a listing are a table of numbers and texts: "(1) The text".
                case "div" when element.ClassList.Contains("colist") && element.QuerySelector("table") is IHtmlTableElement callouts:
                    foreach (IHtmlTableRowElement row in callouts.Rows.Where(static row => row.Cells.Length == 2))
                        Add(MarkdownText.EscapeStart($"({Collapse(row.Cells[0].TextContent)}) {Collapse(string.Join(' ', Nested(row.Cells[1])))}"));

                    break;
                default:
                    Blocks(element);
                    break;
            }
        }

        private void Heading(IElement element)
        {
            Anchor(element.Id, always: true);
            if (HeadingLine(element, element.LocalName[1] - '0') is { } line)
            {
                _level = element.LocalName[1] - '0';
                Add(line);
            }
        }

        // The marker of the heading comes first, so a number such as "1." needs no escape. A "#" at the end of a heading would close it.
        private string? HeadingLine(IElement element, int level)
        {
            string text = Collapse(Inline(element));
            if (text.Length == 0)
                return null;

            if (text.EndsWith('#'))
                text = $"{text[..^1]}\\#";

            return $"{new string('#', level)} {text}";
        }

        private void Definitions(IElement list)
        {
            // HTML allows a div around a term and its definition.
            List<IElement> children = [.. list.Children.SelectMany(static child => child.LocalName == "div" ? (IEnumerable<IElement>)child.Children : [child])];
            // A long definition holds lists, tables or code. Its term names a part of the section, so the chunks of the part carry it in their heading.
            bool parts = !_nested && _level < 6 && children.Any(static child => child.LocalName == "dd" && child.QuerySelector("ul, ol, dl, table, pre") is not null);
            List<IElement> terms = [];
            foreach (IElement child in children)
            {
                if (child.LocalName == "dt")
                {
                    terms.Add(child);
                    continue;
                }

                if (child.LocalName != "dd")
                    continue;

                foreach (IElement term in terms)
                {
                    Anchor(term.Id, always: parts);
                    Add(parts ? HeadingLine(term, _level + 1) ?? string.Empty : MarkdownText.EscapeStart(Collapse(Inline(term))));
                }

                terms.Clear();
                if (parts)
                    _level++;

                Blocks(child);
                if (parts)
                    _level--;
            }

            foreach (IElement term in terms)
                Block(term);
        }

        private void Code(IElement element)
        {
            string text = element.TextContent.Replace("\r\n", "\n", StringComparison.Ordinal).Trim('\n').TrimEnd();
            if (text.Length == 0)
                return;

            if (_inTable)
            {
                Add(MarkdownText.CodeSpan(Collapse(text)).Replace("|", "\\|", StringComparison.Ordinal));
                return;
            }

            IElement code = element.QuerySelector("code") ?? element;
            string language = code.GetAttribute("data-lang") ?? LanguageRegex().Match(code.ClassName ?? element.ClassName ?? string.Empty).Groups["language"].Value;
            string fence = text.Contains("```", StringComparison.Ordinal) ? "~~~~" : "```";
            Add($"{fence}{language}\n{text}\n{fence}");
        }

        private void Formula(IElement element)
        {
            string text = element.TextContent.Trim();
            Match display = DisplayMathRegex().Match(text);
            Add(display.Success ? $"$$\n{TextUnderscores(display.Groups["math"].Value.Trim())}\n$$" : MarkdownText.EscapeStart(Collapse(Inline(element))));
        }

        private void List(IElement element)
        {
            bool ordered = element.LocalName == "ol";
            int number = ordered && int.TryParse(element.GetAttribute("start"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int start) ? start : 1;
            // Generated HTML may set a nested list right inside a list, without its item. It belongs to the item before it.
            // Any other element right inside the list is a block of its own, without a marker.
            List<(bool Item, List<string> Blocks)> parts = [];
            foreach (IElement child in element.Children.Where(static child => !IsSkipped(child)))
            {
                Anchor(child.Id);
                if (child.LocalName == "li")
                    parts.Add((true, Nested(child)));
                else if (child.LocalName is "ul" or "ol" && parts.Count > 0 && parts[^1].Item)
                    parts[^1].Blocks.AddRange(NestedBlock(child));
                else
                    parts.Add((false, NestedBlock(child)));
            }

            foreach ((bool item, List<string> blocks) in parts.Where(static part => part.Blocks.Count > 0))
            {
                if (!item)
                {
                    blocks.ForEach(Add);
                    continue;
                }

                // The blocks after the first one indent under the marker, so they stay inside the item.
                string marker = ordered ? string.Create(CultureInfo.InvariantCulture, $"{number++}. ") : "- ";
                string indent = new(' ', marker.Length);
                Add(marker + string.Join("\n\n", blocks).Replace("\n", "\n" + indent, StringComparison.Ordinal).Replace("\n" + indent + "\n", "\n\n", StringComparison.Ordinal));
            }
        }

        private void Table(IHtmlTableElement table)
        {
            // A pipe table holds one line in a cell, so a table inside a cell becomes its text.
            if (_inTable)
            {
                Add(Collapse(Inline(table)));
                return;
            }

            if (table.Caption is { } caption)
            {
                Anchor(caption.Id);
                Add(MarkdownText.EscapeStart(Collapse(Inline(caption))));
            }

            List<List<string>> rows = [];
            // The rows that a cell above still covers, by column.
            List<int> covered = [];
            IElement? group = null;
            foreach (IHtmlTableRowElement row in table.Rows)
            {
                // A cell over several rows ends with its group of rows, such as the head.
                if (row.ParentElement != group)
                {
                    covered.Clear();
                    group = row.ParentElement;
                }

                List<string> cells = [];
                foreach (IHtmlTableCellElement cell in row.Cells)
                {
                    SkipCovered(cells, covered);
                    // A cell holds one line: its blocks join with spaces.
                    cells.Add(Collapse(string.Join(' ', Nested(cell, inTable: true))));
                    for (int i = 1; i < Math.Max(1, cell.ColumnSpan); i++)
                        cells.Add(string.Empty);

                    for (int i = cells.Count - Math.Max(1, cell.ColumnSpan); i < cells.Count; i++)
                    {
                        while (covered.Count <= i)
                            covered.Add(0);

                        // A row span of 0 reaches the end of the group.
                        covered[i] = cell.RowSpan <= 0 ? int.MaxValue : cell.RowSpan - 1;
                    }
                }

                SkipCovered(cells, covered);
                for (int i = cells.Count; i < covered.Count; i++)
                {
                    if (covered[i] > 0)
                        covered[i]--;
                }

                // A row that the cells above cover fully adds nothing.
                if (row.Cells.Length > 0)
                    rows.Add(cells);
            }

            if (rows.Count == 0)
                return;

            // A pipe table needs a head. A table without one takes its first row.
            int width = rows.Max(static row => row.Count);
            StringBuilder builder = new();
            for (int i = 0; i < rows.Count; i++)
            {
                builder.Append("| ").AppendJoin(" | ", rows[i].Concat(Enumerable.Repeat(string.Empty, width - rows[i].Count))).Append(" |\n");
                if (i == 0)
                    builder.Append('|').Append(string.Concat(Enumerable.Repeat(" --- |", width))).Append('\n');
            }

            Add(builder.ToString().TrimEnd());
        }

        // A column that a cell of a row above covers gets an empty cell, so the next cell keeps its column.
        private static void SkipCovered(List<string> cells, List<int> covered)
        {
            while (cells.Count < covered.Count && covered[cells.Count] > 0)
            {
                covered[cells.Count]--;
                cells.Add(string.Empty);
            }
        }

        private string Inline(INode node)
        {
            switch (node)
            {
                case IText text:
                    return InlineText(text.Data);
                case IElement element when IsSkipped(element):
                    return string.Empty;
                case IElement element:
                    Anchor(element.Id);
                    Anchor(element.GetAttribute("name"));
                    return element.LocalName switch
                    {
                        "br" => " ",
                        "code" or "kbd" or "samp" or "tt" => CodeSpan(element.TextContent),
                        "a" => Link(element),
                        // A block inside a cell or an item becomes a part of its line.
                        _ when IsBlock(element) => $" {string.Concat(element.ChildNodes.Select(Inline))} ",
                        _ => string.Concat(element.ChildNodes.Select(Inline)),
                    };
                default:
                    return string.Empty;
            }
        }

        private string CodeSpan(string text)
        {
            string span = MarkdownText.CodeSpan(Collapse(text));
            return _inTable ? span.Replace("|", "\\|", StringComparison.Ordinal) : span;
        }

        // The text of the page holds no Markdown, so its signs get escapes. A formula of MathJax keeps its LaTeX.
        private string InlineText(string text)
        {
            text = WhitespaceRegex().Replace(text, " ");
            StringBuilder builder = new(text.Length + 8);
            int last = 0;
            foreach (Match match in InlineMathRegex().Matches(text))
            {
                builder.Append(MarkdownText.Escape(text[last..match.Index]));
                string math = TextUnderscores(match.Groups["math"].Value.Trim());
                builder.Append('$').Append(_inTable ? math.Replace("|", "\\|", StringComparison.Ordinal) : math).Append('$');
                last = match.Index + match.Length;
            }

            return builder.Append(MarkdownText.Escape(text[last..])).ToString();
        }

        private string Link(IElement element)
        {
            string text = Collapse(string.Concat(element.ChildNodes.Select(Inline)));
            string href = element.GetAttribute("href") ?? string.Empty;
            // Cloudflare hides an address of e-mail behind a link to its own page.
            if (text.Length == 0 || href.Length == 0 || href.Contains("/cdn-cgi/l/email-protection", StringComparison.Ordinal))
                return text;

            if (LocalAnchor(href) is { } anchor)
                return $"[{text}](#{Uri.EscapeDataString(anchor)})";

            // A link that runs a script or names another scheme stays text.
            if (Uri.TryCreate(href, UriKind.Absolute, out Uri? own) && own.Scheme is not ("http" or "https" or "mailto" or "file"))
                return text;

            // A web page links to absolute addresses. A file keeps its relative links, so they point at the files of its source.
            string target = href;
            if (_linkBase is not null && Uri.TryCreate(_linkBase, href, out Uri? absolute))
                target = absolute.AbsoluteUri;

            return target.Any(static c => c is ' ' or '(' or ')' or '<' or '>') ? $"{text} ({target})" : $"[{text}]({target})";
        }

        // A link to a place of the same page: "#id", or the address of the page with "#id".
        private string? LocalAnchor(string href)
        {
            int hash = href.IndexOf('#', StringComparison.Ordinal);
            if (hash < 0 || hash == href.Length - 1)
                return null;

            if (hash > 0 && !(_address is not null && Uri.TryCreate(_linkBase, href, out Uri? absolute)
                && absolute.GetLeftPart(UriPartial.Query) == _address.GetLeftPart(UriPartial.Query)))
                return null;

            return Uri.UnescapeDataString(href[(hash + 1)..]);
        }
    }
}
