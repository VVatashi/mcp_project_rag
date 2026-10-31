using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

using Markdig;
using Markdig.Extensions.Footnotes;
using Markdig.Extensions.Mathematics;
using Markdig.Extensions.Tables;
using Markdig.Extensions.Yaml;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace ProjectRag.Core.Documents;

/// <summary>
/// Reads the structure of a Markdown document with Markdig: the sections, the blocks, the anchors, the pages and the links.
/// </summary>
/// <remarks>
/// <para>
/// Markdown is the intermediate form of every document. An importer of another format, such as <see cref="Pdf.PdfMarkdownImporter"/>,
/// writes Markdown with two kinds of service marks. Each mark is an HTML comment, so any Markdown viewer hides it:
/// </para>
/// <list type="bullet">
/// <item><c>&lt;!-- rag:page 12 --&gt;</c> starts the page 12.</item>
/// <item><c>&lt;!-- rag:anchor name --&gt;</c> gives the next block the anchor <c>name</c> for links.</item>
/// </list>
/// <para>
/// A heading gets the anchor of GitHub, for example <c>#8142-coordinate-wrapping</c>. A link <c>[text](#anchor)</c> or <c>[text](other.md#anchor)</c>
/// becomes a <see cref="DocumentLink"/>. A mention such as "section 8.14.2" or "table 8.20" becomes a link too, when the document has the target.
/// </para>
/// </remarks>
public static partial class MarkdownStructureReader
{
    /// <summary>The prefix of the service marks.</summary>
    public const string MarkPrefix = "<!-- rag:";

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePreciseSourceLocation()
        .UseYamlFrontMatter()
        .UsePipeTables()
        .UseGridTables()
        .UseMathematics()
        .UseFootnotes()
        .UseAutoIdentifiers(Markdig.Extensions.AutoIdentifiers.AutoIdentifierOptions.GitHub)
        .Build();

    /// <summary>
    /// Writes the mark of the start of a page.
    /// </summary>
    /// <param name="page">The number of the page.</param>
    /// <returns>The mark.</returns>
    public static string PageMark(int page) => string.Create(CultureInfo.InvariantCulture, $"{MarkPrefix}page {page} -->");

    /// <summary>
    /// Writes the mark of an anchor of the next block.
    /// </summary>
    /// <param name="name">The name of the anchor: letters, digits, '.', '_' and '-'.</param>
    /// <returns>The mark.</returns>
    public static string AnchorMark(string name) => $"{MarkPrefix}anchor {name} -->";

    /// <summary>
    /// Reads the structure of a document.
    /// </summary>
    /// <param name="document">The document.</param>
    /// <param name="path">The path of the document in its source. Relative links resolve against it.</param>
    /// <returns>The structure.</returns>
    public static DocumentStructure Read(DocumentText document, string path)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(path);
        string content = document.Content;
        MarkdownDocument markdown = Markdown.Parse(content, Pipeline);
        Builder builder = new(document.Title, content);
        foreach (Block block in markdown)
            builder.Add(block);

        builder.Finish();
        foreach (LinkInline link in markdown.Descendants<LinkInline>())
            builder.AddLink(link, path);

        builder.AddMentions(markdown);
        builder.AddInlineMarks(markdown);
        return builder.Build();
    }

    /// <summary>
    /// Finds the title of a Markdown text: the field <c>title</c> of the front matter, or else the first heading of level 1.
    /// </summary>
    /// <param name="content">The text.</param>
    /// <returns>The title, or <see langword="null"/> when the text has none.</returns>
    public static string? FindTitle(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        MarkdownDocument markdown = Markdown.Parse(content, Pipeline);
        foreach (Block block in markdown)
        {
            if (block is YamlFrontMatterBlock front && FrontMatterTitle(content, front) is { } title)
                return title;

            if (block is HeadingBlock { Level: 1 } heading && PlainText(heading.Inline).Trim() is { Length: > 0 } text)
                return text;
        }

        return null;
    }

    /// <summary>
    /// Writes the text of inlines without their Markdown syntax: the label of a link, the code of a code span, the TeX of a formula.
    /// </summary>
    /// <param name="inline">The inlines.</param>
    /// <returns>The text.</returns>
    internal static string PlainText(ContainerInline? inline)
    {
        StringBuilder builder = new();
        AppendText(inline, builder);
        return WhitespaceRegex().Replace(builder.ToString(), " ");
    }

    private static void AppendText(Inline? inline, StringBuilder builder)
    {
        switch (inline)
        {
            case null:
                return;
            case LiteralInline literal:
                builder.Append(literal.Content.AsSpan());
                break;
            case CodeInline code:
                builder.Append(code.Content);
                break;
            case MathInline math:
                builder.Append(math.Content.AsSpan());
                break;
            case LineBreakInline:
                builder.Append(' ');
                break;
            case HtmlInline:
                break;
            case ContainerInline container:
                foreach (Inline child in container)
                    AppendText(child, builder);
                break;
        }
    }

    private static string? FrontMatterTitle(string content, YamlFrontMatterBlock block)
    {
        foreach (string line in content[block.Span.Start..(block.Span.End + 1)].Split('\n'))
        {
            if (line.StartsWith("title:", StringComparison.Ordinal) && line["title:".Length..].Trim().Trim('"', '\'') is { Length: > 0 } title)
                return title;
        }

        return null;
    }

    // The leading number of a heading: "8.14.2 Coordinate Wrapping", "4.3.5. Uniform Variables", "A.1 Extensions", "II.22.2 Assembly".
    [GeneratedRegex(@"^(?<number>(?:\d+|(?:[IVX]{1,4}|[A-Z])(?=\.\d))(?:\.\d+)*)\.?\s+\S")]
    private static partial Regex NumberRegex();

    [GeneratedRegex(@"^<!-- rag:(?<kind>page|anchor) (?<value>[^ >]+) -->$")]
    private static partial Regex MarkRegex();

    // A mention of a part of the document: "section 8.14.2", "Table 8.20", "equation 3.4", "chapter 7", "раздел 2.3", "таблица 4".
    [GeneratedRegex(@"\b(?<kind>sections?|chapters?|tables?|figures?|equations?|listings?|appendix|раздел[а-я]*|глав[а-я]*|таблиц[а-я]*|рисун[а-я]*)\s+(?<number>(?:\d+|[A-Z])(?:\.\d+)*)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MentionRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    private sealed class Builder(string title, string content)
    {
        private readonly List<DocumentSection> _sections = [new DocumentSection(0, -1, 0, title, null, null, title, 0, content.Length)];
        private readonly List<DocumentBlock> _blocks = [];
        private readonly Dictionary<string, int> _anchors = new(StringComparer.Ordinal);
        private readonly List<DocumentLink> _links = [];
        private readonly List<(int Offset, int Page)> _pages = [];
        private readonly List<(int Start, int End)> _hidden = [];
        private readonly List<string> _pendingAnchors = [];
        private readonly List<int> _stack = [0];
        private readonly List<(int Start, int End)> _headings = [];
        private readonly List<(int Start, int End)> _footnotes = [];

        public void Add(Block block)
        {
            int start = block.Span.Start;
            int end = Math.Min(content.Length, block.Span.End + 1);
            if (block is not HtmlBlock)
                FlushAnchors(start);

            switch (block)
            {
                case YamlFrontMatterBlock:
                    _hidden.Add((start, end));
                    break;
                case HeadingBlock heading:
                    AddSection(heading, start);
                    _headings.Add((start, end));
                    break;
                case HtmlBlock html when TryMark(content[start..end].Trim(), start):
                    _hidden.Add((start, end));
                    break;
                case HtmlBlock:
                    AddBlock(BlockKind.Html, start, end);
                    break;
                case MathBlock:
                    AddBlock(BlockKind.Math, start, end);
                    break;
                case CodeBlock:
                    AddBlock(BlockKind.Code, start, end);
                    break;
                case Table:
                    AddBlock(BlockKind.Table, start, end);
                    break;
                case ListBlock list:
                    foreach (Block item in list)
                        AddContainer(item, BlockKind.ListItem);
                    break;
                case QuoteBlock quote:
                    AddContainer(quote, BlockKind.Quote);
                    break;
                case FootnoteGroup group:
                    // Markdig moves the footnotes to the end of the document. Each keeps the place of its text, see Finish.
                    foreach (Block footnote in group)
                        _footnotes.Add((footnote.Span.Start, Math.Min(content.Length, footnote.Span.End + 1)));
                    break;
                case ThematicBreakBlock or LinkReferenceDefinitionGroup:
                    break;
                default:
                    AddBlock(BlockKind.Paragraph, start, end);
                    break;
            }
        }

        public void Finish()
        {
            FlushAnchors(content.Length);
            // A footnote belongs to the section that holds its text.
            foreach ((int start, int end) in _footnotes)
            {
                int section = _sections.FindLastIndex(candidate => candidate.Start <= start);
                if (end > start)
                    _blocks.Add(new DocumentBlock(BlockKind.Footnote, start, end, Math.Max(0, section)));
            }

            _blocks.Sort(static (a, b) => a.Start.CompareTo(b.Start));
            // A section ends where the next section of the same or a higher level starts.
            for (int i = 1; i < _sections.Count; i++)
            {
                int end = content.Length;
                for (int j = i + 1; j < _sections.Count; j++)
                {
                    if (_sections[j].Level <= _sections[i].Level)
                    {
                        end = _sections[j].Start;
                        break;
                    }
                }

                _sections[i] = _sections[i] with { End = end };
            }
        }

        public void AddLink(LinkInline link, string path)
        {
            if (link.IsImage || string.IsNullOrEmpty(link.Url) || link.Url.Contains("://", StringComparison.Ordinal) || link.Url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
                return;

            string url = Uri.UnescapeDataString(link.Url);
            int hash = url.IndexOf('#', StringComparison.Ordinal);
            string target = hash < 0 ? url : url[..hash];
            string? anchor = hash < 0 ? null : url[(hash + 1)..];
            string? targetPath = target.Length == 0 ? null : ResolvePath(path, target);
            if (targetPath is null && anchor is null)
                return;

            _links.Add(new DocumentLink(LinkKind.Link, link.Span.Start, Math.Min(content.Length, link.Span.End + 1), PlainText(link).Trim(), targetPath, anchor));
        }

        public void AddMentions(MarkdownDocument markdown)
        {
            // A mention inside a link adds nothing: the link already points at the target.
            List<(int Start, int End)> linked = [.. _links.Select(static link => (link.Start, link.End))];
            HashSet<int> blockStarts = [.. _blocks.Select(static block => block.Start)];
            Dictionary<string, string> sections = new(StringComparer.Ordinal);
            foreach (DocumentSection section in _sections)
            {
                if (section is { Number: { } number, Anchor: { } anchor })
                    sections.TryAdd(number, anchor);
            }

            foreach (LiteralInline literal in markdown.Descendants<LiteralInline>())
            {
                int position = literal.Span.Start;
                if (literal.Parent is LinkInline || literal.FirstParentOfType<LinkInline>() is not null || InHeading(position))
                    continue;

                string text = literal.Content.ToString();
                foreach (Match match in MentionRegex().Matches(text))
                {
                    int start = literal.Span.Start + match.Index;
                    // A caption such as "Table 8.20: …" names its own table. A mention at the start of a block is no link.
                    if (blockStarts.Contains(start) || linked.Any(range => start >= range.Start && start < range.End))
                        continue;

                    string number = match.Groups["number"].Value;
                    // A section number needs its section. A table, a figure or an equation needs the anchor that the importer wrote for its caption.
                    string? anchor = CaptionKind(match.Groups["kind"].Value) is { } caption
                        ? $"{caption}-{number}"
                        : sections.GetValueOrDefault(number);
                    if (anchor is not null && _anchors.ContainsKey(anchor))
                        _links.Add(new DocumentLink(LinkKind.Mention, start, start + match.Length, match.Value, null, anchor));
                }
            }

            _links.Sort(static (a, b) => a.Start.CompareTo(b.Start));
        }

        public void AddInlineMarks(MarkdownDocument markdown)
        {
            // An importer puts the mark of a page inside a paragraph that runs over the page break.
            foreach (HtmlInline html in markdown.Descendants<HtmlInline>())
            {
                int start = html.Span.Start;
                if (TryMark(html.Tag.Trim(), start))
                {
                    _hidden.Add((start, Math.Min(content.Length, html.Span.End + 1)));
                    FlushAnchors(start);
                }
            }

            _pages.Sort(static (a, b) => a.Offset.CompareTo(b.Offset));
            _hidden.Sort(static (a, b) => a.Start.CompareTo(b.Start));
        }

        public DocumentStructure Build() => new(title, content, _sections, _blocks, _anchors, _links, _pages, _hidden);

        /// <summary>
        /// Gets the kind of a caption from the word of a mention: <c>table</c>, <c>figure</c>, <c>equation</c> or <c>listing</c>.
        /// An importer writes the anchor <c>kind-number</c> before a caption, for example <c>table-8.20</c>. A word of a section gives <see langword="null"/>.
        /// </summary>
        private static string? CaptionKind(string word)
        {
            string lower = word.ToLowerInvariant();
            return lower switch
            {
                _ when lower.StartsWith("table", StringComparison.Ordinal) || lower.StartsWith("таблиц", StringComparison.Ordinal) => "table",
                _ when lower.StartsWith("figure", StringComparison.Ordinal) || lower.StartsWith("рисун", StringComparison.Ordinal) => "figure",
                _ when lower.StartsWith("equation", StringComparison.Ordinal) => "equation",
                _ when lower.StartsWith("listing", StringComparison.Ordinal) => "listing",
                _ => null,
            };
        }

        private static string? ResolvePath(string path, string target)
        {
            // The paths of a source use forward slashes and stay inside the source.
            // A link that starts with a slash starts at the root of the source.
            string directory = target.StartsWith('/') || !path.Contains('/', StringComparison.Ordinal) ? string.Empty : path[..path.LastIndexOf('/')];
            List<string> parts = [.. directory.Split('/', StringSplitOptions.RemoveEmptyEntries)];
            foreach (string part in target.Replace('\\', '/').Split('/'))
            {
                if (part is "" or ".")
                    continue;

                if (part == "..")
                {
                    if (parts.Count == 0)
                        return null;

                    parts.RemoveAt(parts.Count - 1);
                }
                else
                {
                    parts.Add(part);
                }
            }

            return parts.Count == 0 ? null : string.Join('/', parts);
        }

        // The headings come in the order of the text, so a binary search finds the heading before a place.
        private bool InHeading(int position)
        {
            int low = 0;
            int high = _headings.Count - 1;
            while (low <= high)
            {
                int middle = (low + high) / 2;
                if (_headings[middle].End <= position)
                    low = middle + 1;
                else if (_headings[middle].Start > position)
                    high = middle - 1;
                else
                    return true;
            }

            return false;
        }

        private bool TryMark(string text, int start)
        {
            Match match = MarkRegex().Match(text);
            if (!match.Success)
                return false;

            string value = match.Groups["value"].Value;
            if (match.Groups["kind"].Value == "page")
            {
                if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int page))
                    _pages.Add((start, page));
            }
            else
            {
                _pendingAnchors.Add(value);
            }

            return true;
        }

        private void FlushAnchors(int offset)
        {
            foreach (string anchor in _pendingAnchors)
                _anchors.TryAdd(anchor, offset);

            _pendingAnchors.Clear();
        }

        private void AddSection(HeadingBlock heading, int start)
        {
            string text = PlainText(heading.Inline).Trim();
            while (_stack.Count > 1 && _sections[_stack[^1]].Level >= heading.Level)
                _stack.RemoveAt(_stack.Count - 1);

            int parent = _stack[^1];
            // The first heading of a file often repeats its title, so the path does not repeat it.
            string parentPath = _sections[parent].Path;
            bool repeatsTitle = parent == 0 && string.Equals(text, title, StringComparison.OrdinalIgnoreCase);
            string sectionPath = repeatsTitle || text.Length == 0 ? parentPath : $"{parentPath}{TextChunk.HeadingSeparator}{text}";
            string? number = NumberRegex().Match(text) is { Success: true } match ? match.Groups["number"].Value : null;
            string? anchor = heading.GetAttributes().Id;
            if (anchor is not null)
                _anchors.TryAdd(anchor, start);

            _sections.Add(new DocumentSection(_sections.Count, parent, heading.Level, text.Length == 0 ? title : text, number, anchor, sectionPath, start, content.Length));
            _stack.Add(_sections.Count - 1);
        }

        // A list item or a quote with code, a formula or a table gives its parts as blocks of their own kinds, so the code stays atomic.
        // The first part starts at the start of the container and keeps its marker, such as "1." or ">".
        private void AddContainer(Block container, BlockKind kind)
        {
            int start = container.Span.Start;
            int end = Math.Min(content.Length, container.Span.End + 1);
            if (container is not ContainerBlock children || !children.Descendants().Any(static block => block is CodeBlock or Table))
            {
                AddBlock(kind, start, end);
                return;
            }

            bool first = true;
            foreach (Block child in children)
            {
                int childStart = first ? start : child.Span.Start;
                int childEnd = Math.Min(content.Length, child.Span.End + 1);
                first = false;
                switch (child)
                {
                    case ListBlock list:
                        foreach (Block item in list)
                            AddContainer(item, BlockKind.ListItem);
                        break;
                    case QuoteBlock quote:
                        AddContainer(quote, BlockKind.Quote);
                        break;
                    case MathBlock:
                        AddBlock(BlockKind.Math, childStart, childEnd);
                        break;
                    case CodeBlock:
                        AddBlock(BlockKind.Code, childStart, childEnd);
                        break;
                    case Table:
                        AddBlock(BlockKind.Table, childStart, childEnd);
                        break;
                    default:
                        AddBlock(kind, childStart, childEnd);
                        break;
                }
            }
        }

        private void AddBlock(BlockKind kind, int start, int end)
        {
            if (end > start)
                _blocks.Add(new DocumentBlock(kind, start, end, _stack[^1]));
        }
    }
}
