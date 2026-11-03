using System.Globalization;
using System.Text.RegularExpressions;

using UglyToad.PdfPig;

namespace ProjectRag.Core.Documents.Pdf;

/// <summary>
/// Turns a PDF file into Markdown, the intermediate form of the pipeline.
/// </summary>
/// <remarks>
/// <para>
/// The importer reads the glyphs, the thin rules and the link annotations of each page, see <see cref="PdfLayout"/>. It drops the running headers and footers.
/// Then it groups the lines into blocks: headings from the bookmarks, paragraphs, lists, code, display formulas, tables and footnotes.
/// </para>
/// <list type="bullet">
/// <item>A paragraph joins its lines. A word with a hyphen at the end of a line loses the hyphen when the document uses the joined word elsewhere.
/// A run of a monospace font becomes a code span, and a run of a math font becomes an inline formula <c>$…$</c>.</item>
/// <item>A block of monospace lines becomes fenced code with its indentation.</item>
/// <item>A display formula becomes <c>$$…$$</c> in LaTeX, see <see cref="MathWriter"/>. A large brace over several lines becomes <c>cases</c>.
/// A number such as "(8.3)" at the right becomes <c>\tag</c> and the anchor <c>equation-8.3</c>.</item>
/// <item>The lines between wide rules with columns become a table: a pipe table when the columns are clear, or else a fenced block of text.</item>
/// <item>A link annotation becomes a Markdown link to the anchor of the block at its target. A caption "Table 8.20:" gives its table the anchor <c>table-8.20</c>.</item>
/// <item>Each page starts with the mark <c>&lt;!-- rag:page N --&gt;</c>. A paragraph that runs over a page break keeps the mark inside.</item>
/// </list>
/// <para>
/// The importer reads one column. A page with two columns comes out in the order of the lines across both columns.
/// </para>
/// </remarks>
public static partial class PdfMarkdownImporter
{
    /// <summary>
    /// Reads a PDF file.
    /// </summary>
    /// <param name="bytes">The bytes of the file.</param>
    /// <param name="fallbackTitle">The title when the file names none.</param>
    /// <returns>The document in Markdown.</returns>
    public static DocumentText Read(byte[] bytes, string fallbackTitle)
    {
        using PdfDocument pdf = PdfPigSource.Open(bytes);
        string title = PdfPigSource.Normalize(pdf.Information.Title ?? string.Empty);
        IReadOnlyList<PdfPageModel> pages = PdfPigSource.ReadPages(pdf);
        if (!FileNameRegex().IsMatch(title))
            return Convert(title.Length == 0 ? CoverTitle(pages) ?? fallbackTitle : title, pages, PdfPigSource.ReadBookmarks(pdf));

        // A word processor may write the name of the file as the title, such as "GL3PGM.PDF". The first heading then names the document.
        DocumentText text = Convert(fallbackTitle, pages, PdfPigSource.ReadBookmarks(pdf));
        return text with { Title = MarkdownStructureReader.FindTitle(text.Content) ?? fallbackTitle };
    }

    /// <summary>
    /// Finds the title on the cover of a file without a title: the lines of the first page in its largest font, such as "Khronos Data Format Specification".
    /// </summary>
    /// <param name="pages">The pages of the file.</param>
    /// <returns>The title, or <see langword="null"/> for an empty first page, a first page without a larger font than the text or a title over 200 characters.</returns>
    internal static string? CoverTitle(IReadOnlyList<PdfPageModel> pages)
    {
        if (pages.Count == 0)
            return null;

        List<Line> lines = PdfLayout.BuildLines(pages[0], MathFonts(pages));
        if (lines.Count == 0)
            return null;

        double size = lines.Max(static line => line.Size);
        if (size < BodySize(pages) * 1.2)
            return null;

        string title = PdfPigSource.Normalize(string.Join(' ', lines.Where(line => line.Size >= size - 0.5).Select(static line => line.PlainText)));
        return title.Length is > 0 and <= 200 ? title : null;
    }

    /// <summary>
    /// Converts the pages of a document into Markdown.
    /// </summary>
    internal static DocumentText Convert(string title, IReadOnlyList<PdfPageModel> pages, IReadOnlyList<PdfBookmark> bookmarks)
    {
        Func<Glyph, bool> isMath = MathFonts(pages);
        double bodySize = BodySize(pages);
        List<List<Line>> lines = [.. pages.Select(page => PdfLayout.BuildLines(page, isMath))];
        RemoveRunningLines(pages, lines, bodySize);
        for (int i = 0; i < pages.Count; i++)
        {
            MarkTables(pages[i], lines[i]);
            MarkFootnotes(lines[i], bodySize);
        }

        Dictionary<int, List<PdfBookmark>> unmatched = MarkHeadings(pages, lines, UsefulOutline(pages, bookmarks), bodySize);
        HashSet<string> vocabulary = Vocabulary(lines);
        List<PdfBlock> blocks = [];
        for (int i = 0; i < pages.Count; i++)
            blocks.AddRange(BuildBlocks(pages[i].Number, lines[i], unmatched.GetValueOrDefault(pages[i].Number) ?? []));

        // A turned page holds a figure or a wide table, and a page of contents lists the headings of other pages: their bold lines are no headings.
        HashSet<int> listPages = [.. pages.Where((page, index) => page.Turned || IsContentsPage(lines[index])).Select(static page => page.Number)];
        MarkBoldHeadings(blocks, listPages);
        JoinPageBreaks(blocks);
        Dictionary<(int Page, int Index), string> linkTargets = AnchorLinkTargets(pages, blocks);
        PdfMarkdownWriter writer = new(vocabulary, pages.ToDictionary(static page => page.Number), linkTargets);
        return new DocumentText(title, writer.Write(blocks));
    }

    // The fonts of Computer Modern are math only in a document whose body uses another font.
    private static Func<Glyph, bool> MathFonts(IReadOnlyList<PdfPageModel> pages)
    {
        string body = pages.SelectMany(static page => page.Glyphs).GroupBy(static glyph => glyph.Font).MaxBy(static group => group.Count())?.Key ?? string.Empty;
        bool bodyIsComputerModern = body.StartsWith("CM", StringComparison.Ordinal);
        return glyph => glyph.IsMath && !(bodyIsComputerModern && (glyph.Font.StartsWith("CMR", StringComparison.Ordinal) || glyph.Font.StartsWith("CMBX", StringComparison.Ordinal)
            || glyph.Font.StartsWith("CMTI", StringComparison.Ordinal) || glyph.Font.StartsWith("CMSL", StringComparison.Ordinal) || glyph.Font.StartsWith("CMSS", StringComparison.Ordinal)));
    }

    private static double BodySize(IReadOnlyList<PdfPageModel> pages) =>
        pages.SelectMany(static page => page.Glyphs).GroupBy(static glyph => Math.Round(glyph.Size * 2) / 2).MaxBy(static group => group.Count())?.Key ?? 10;

    // A line at the top or the bottom of a page that repeats on many pages is a running header or footer. A number counts as any number.
    private static void RemoveRunningLines(IReadOnlyList<PdfPageModel> pages, List<List<Line>> lines, double bodySize)
    {
        Dictionary<string, List<int>> places = new(StringComparer.Ordinal);
        HashSet<(string Key, int Page)> small = [];
        for (int i = 0; i < pages.Count; i++)
        {
            List<Line> page = lines[i];
            foreach (Line line in page.Where(line => IsEdge(pages[i], page, line)))
            {
                // A small header stands at the top and holds no code. A line of code or a footnote in a small font is no header.
                string key = LineKey(line.PlainText);
                if (line.Size < bodySize * 0.9 && page.IndexOf(line) < 2 && !line.Items.Any(static item => (item.Style & GlyphStyle.Mono) != 0))
                    small.Add((key, i));

                if (!places.TryGetValue(key, out List<int>? indexes))
                    places[key] = indexes = [];

                if (indexes.Count == 0 || indexes[^1] != i)
                    indexes.Add(i);
            }
        }

        // A header of the whole document repeats on many pages. A header of a chapter, such as "Chapter 12 Expressions", repeats on a run of pages:
        // the even and the odd pages may differ, so the run allows a gap of one page.
        // The first page of a run keeps its line, so the head of a table that repeats on each page stays once.
        int threshold = Math.Max(3, (int)Math.Ceiling(pages.Count * 0.3));
        HashSet<(string Key, int Page)> running = [];
        foreach ((string key, List<int> indexes) in places)
        {
            bool everywhere = indexes.Count >= threshold;
            int start = 0;
            for (int i = 1; i <= indexes.Count; i++)
            {
                if (i < indexes.Count && indexes[i] - indexes[i - 1] <= 2)
                    continue;

                // A run of four pages or more is a running line. A line of a word or less, such as the "}" at the end of a listing, is no header.
                // A header of a short chapter in a font smaller than the text, such as "7. Depth Buffering", needs two pages.
                int smallPages = indexes[start..i].Count(page => small.Contains((key, page)));
                bool run = i - start >= 4 || (smallPages >= 2 && i - start >= 2);
                for (int j = everywhere ? start : start + 1; j < i && (everywhere || (run && key.Count(char.IsLetter) >= 3)); j++)
                    running.Add((key, indexes[j]));

                start = i;
            }
        }

        // A name with a number that stands at the edges of many pages, such as "ECMA-334", marks the first and the last line of a page as a running line,
        // also on the first page of a chapter and in a short chapter. A word without a number, such as "the", is no mark.
        Dictionary<string, int> words = new(StringComparer.Ordinal);
        for (int i = 0; i < pages.Count; i++)
        {
            List<Line> page = lines[i];
            foreach (string word in page.Where(line => IsEdge(pages[i], page, line)).SelectMany(static line => EdgeWords(line.PlainText)).Where(static word => word.Contains('#', StringComparison.Ordinal)).Distinct(StringComparer.Ordinal))
                words[word] = words.GetValueOrDefault(word) + 1;
        }

        HashSet<string> markers = [.. words.Where(pair => pair.Value >= threshold).Select(static pair => pair.Key)];
        for (int i = 0; i < pages.Count; i++)
        {
            PdfPageModel page = pages[i];
            List<Line> pageLines = lines[i];
            HashSet<Line> edges = [.. pageLines.Where(line => IsEdge(page, pageLines, line))];
            Line? first = pageLines.FirstOrDefault();
            Line? last = pageLines.LastOrDefault();
            pageLines.RemoveAll(line => edges.Contains(line) && (running.Contains((LineKey(line.PlainText), i)) || PageHeaderRegex().IsMatch(line.PlainText)
                || ((line == first || line == last) && EdgeWords(line.PlainText).Any(markers.Contains))));
        }
    }

    // A running line is among the first two or the last two lines, in the top quarter or the bottom sixth of the page.
    private static bool IsEdge(PdfPageModel page, List<Line> lines, Line line)
    {
        int index = lines.IndexOf(line);
        return (index < 2 && line.Bottom > page.Height * 0.75) || (index >= lines.Count - 2 && line.Top < page.Height * 0.17);
    }

    // The words of a line with three letters or more, with their digits replaced, so "ECMA-334" and "ECMA-335" count as one word.
    private static IEnumerable<string> EdgeWords(string text) =>
        text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(static word => word.Count(char.IsLetter) >= 3).Select(static word => DigitsRegex().Replace(word, "#").ToUpperInvariant());

    // A page number in small Roman numerals, such as "iv" of the front matter, counts as a number too.
    // A footer may set its dash in the text font on one page and in the font Symbol on the next: "1995-1998" and "1995−1998".
    // The words go in their sorted order. An even page may put the number or the parts of a footer in another order: "30 Copyright…" and "Copyright… 29".
    private static string LineKey(string text) =>
        string.Concat(text.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(static word => DigitsRegex().Replace(RomanNumberRegex().Replace(word, "#"), "#").Replace('−', '-').Replace('–', '-').ToUpperInvariant())
            .Order(StringComparer.Ordinal));

    // The lines between wide rules form a table when many of them have columns: gaps of more than a character.
    private static void MarkTables(PdfPageModel page, List<Line> lines)
    {
        if (lines.Count == 0)
            return;

        double textWidth = lines.Max(static line => line.Right) - lines.Min(static line => line.Left);
        // The widest rule at each height. The edges of its cells tell the columns of a table with a border around each cell.
        List<(double Y, double[] Edges)> rules = [.. JoinRules(page.Rules).Where(rule => rule.Rule.Width >= textWidth * 0.3)
            .GroupBy(static rule => rule.Rule.Y).Select(static group => group.MaxBy(static rule => rule.Rule.Width))
            .OrderByDescending(static rule => rule.Rule.Y).Select(static rule => (rule.Rule.Y, rule.Edges))];
        IReadOnlyList<Bar> bars = page.Bars ?? [];
        int start = 0;
        while (start < rules.Count - 1)
        {
            // Two or more rows with a border around each cell and the same columns form a table with a grid.
            // A block of another layout may also draw cells, such as an instruction of SPIR-V with its text in one wide cell. So one such row is not enough.
            // The vertical borders of the cells tell a row from the text between two tables with the same columns.
            int end = start;
            while (end + 1 < rules.Count && rules[start].Edges.Length >= 3 && SameEdges(rules[start].Edges, rules[end + 1].Edges)
                && lines.Any(line => line.Baseline < rules[end].Y && line.Baseline > rules[end + 1].Y)
                && rules[start].Edges.All(x => bars.Any(bar => Math.Abs(bar.X - x) < 1.5 && bar.Top >= rules[end].Y - 2 && bar.Bottom <= rules[end + 1].Y + 2)))
                end++;

            if (end - start >= 2)
            {
                for (int row = start; row < end; row++)
                {
                    foreach (Line line in lines.Where(line => line.Baseline < rules[row].Y && line.Baseline > rules[row + 1].Y))
                        (line.InTable, line.CellEdges, line.CellRow) = (true, rules[start].Edges, row);
                }

                start = end;
                continue;
            }

            end = start;
            while (end + 1 < rules.Count && IsTabular(lines.Where(line => line.Baseline < rules[end].Y && line.Baseline > rules[end + 1].Y).ToList()))
                end++;

            if (end > start)
            {
                List<Line> table = [.. lines.Where(line => line.Baseline < rules[start].Y && line.Baseline > rules[end].Y)];
                foreach (Line line in table)
                    line.InTable = true;

                ExtendTable(lines, table);
            }

            start = end + 1;
        }
    }

    private static bool SameEdges(double[] first, double[] second) =>
        first.Length == second.Length && first.Zip(second).All(static pair => Math.Abs(pair.First - pair.Second) < 1);

    // Some tables draw a border for each cell. The borders of one row touch each other at one height and together make one wide rule.
    private static List<(Rule Rule, double[] Edges)> JoinRules(IReadOnlyList<Rule> rules)
    {
        // The rules of one height, in the order from left to right. A file may draw them in any order.
        List<List<Rule>> heights = [];
        foreach (Rule rule in rules.OrderBy(static rule => rule.Y))
        {
            if (heights.Count > 0 && rule.Y - heights[^1][0].Y < 0.5)
                heights[^1].Add(rule);
            else
                heights.Add([rule]);
        }

        List<(Rule Rule, double[] Edges)> joined = [];
        foreach (List<Rule> height in heights)
        {
            List<Rule> span = [];
            foreach (Rule rule in height.OrderBy(static rule => rule.Left))
            {
                // A rule that touches or overlaps the rules before it goes on with them.
                if (span.Count > 0 && rule.Left > span.Max(static part => part.Right) + 1)
                {
                    joined.Add(Join(span));
                    span = [];
                }

                span.Add(rule);
            }

            if (span.Count > 0)
                joined.Add(Join(span));
        }

        return joined;
    }

    // The ends of the rules of a span are the edges of its cells. A repeated border or an outer frame adds no edge of its own.
    private static (Rule Rule, double[] Edges) Join(List<Rule> span)
    {
        List<double> edges = [];
        foreach (double x in span.SelectMany(static rule => new[] { rule.Left, rule.Right }).Order())
        {
            if (edges.Count == 0 || x - edges[^1] > 1)
                edges.Add(x);
        }

        return (new Rule(span.Min(static rule => rule.Left), span.Max(static rule => rule.Right), span[0].Y), [.. edges]);
    }

    // A row past the last rule still belongs to the table when its cells start at the columns of the table and it follows closely.
    private static void ExtendTable(List<Line> lines, List<Line> table)
    {
        if (table.Count == 0)
            return;

        List<double> columns = [.. table.SelectMany(static line => Segments(line)).Select(static segment => segment[0].Left)];
        int index = lines.IndexOf(table[^1]) + 1;
        Line previous = table[^1];
        while (index < lines.Count && lines[index] is { InTable: false } next && previous.Baseline - next.Baseline < next.Size * 2.5)
        {
            List<List<Item>> segments = Segments(next);
            if (segments.Count < 2 || !segments.All(segment => columns.Any(column => Math.Abs(column - segment[0].Left) < 8)))
                break;

            next.InTable = true;
            previous = next;
            index++;
        }
    }

    private static bool IsTabular(List<Line> lines) =>
        lines.Count > 0 && (lines.Count(static line => Segments(line).Count >= 2) * 2 >= lines.Count || lines.All(static line => line.Kind != LineKind.Text || line.Items.Count <= 3));

    /// <summary>
    /// Splits a line at its wide gaps: the cells of a table row.
    /// </summary>
    internal static List<List<Item>> Segments(Line line)
    {
        List<List<Item>> segments = [[line.Items[0]]];
        for (int i = 1; i < line.Items.Count; i++)
        {
            Item previous = line.Items[i - 1];
            Item item = line.Items[i];
            double gap = item.Left - Math.Max(previous.Right, previous.Sub.Concat(previous.Sup).Select(static script => script.Right).DefaultIfEmpty(previous.Right).Max());
            if (gap > Math.Max(item.Size, previous.Size) * 1.2)
                segments.Add([item]);
            else
                segments[^1].Add(item);
        }

        return segments;
    }

    // The smaller lines at the bottom of a page that start with a number are footnotes.
    private static void MarkFootnotes(List<Line> lines, double bodySize)
    {
        int first = lines.Count;
        for (int i = lines.Count - 1; i >= 0; i--)
        {
            // A footnote may hold code or a formula, so only the size and a table end it.
            Line line = lines[i];
            if (line.InTable || line.Size > bodySize * 0.93)
                break;

            if (i < lines.Count - 1 && lines[i].Baseline - lines[i + 1].Baseline > line.Size * 2.5)
                break;

            first = i;
        }

        if (first < lines.Count && FootnoteStartRegex().IsMatch(lines[first].PlainText))
        {
            for (int i = first; i < lines.Count; i++)
                lines[i].IsFootnote = true;
        }
    }

    // A bookmark marks its heading line: the line near the top of the target whose text matches the title, with or without its number.
    // A chapter of LaTeX sets "Chapter 8" and its name on two lines far below the target, so a chapter searches farther and drops the line "Chapter 8".
    // A bookmark without a matching line becomes a heading of its own at its place.
    // A file of a word processor may keep its internal bookmarks, such as "OLE_LINK6", on the first page or on no page.
    // Such an outline names no sections, so the headings come from the fonts. A useful outline has titles that reach two pages at least.
    private static List<PdfBookmark> UsefulOutline(IReadOnlyList<PdfPageModel> pages, IReadOnlyList<PdfBookmark> bookmarks)
    {
        HashSet<int> numbers = [.. pages.Select(static page => page.Number)];
        List<PdfBookmark> titles = [.. bookmarks.Where(bookmark => numbers.Contains(bookmark.Page) && !InternalBookmarkRegex().IsMatch(bookmark.Title))];
        return titles.Select(static bookmark => bookmark.Page).Distinct().Count() >= Math.Min(2, pages.Count) ? titles : [];
    }

    private static Dictionary<int, List<PdfBookmark>> MarkHeadings(IReadOnlyList<PdfPageModel> pages, List<List<Line>> lines, List<PdfBookmark> bookmarks, double bodySize)
    {
        Dictionary<int, List<PdfBookmark>> unmatched = [];
        Dictionary<int, int> indexes = pages.Select(static (page, index) => (page.Number, index)).ToDictionary(static pair => pair.Number, static pair => pair.index);
        if (bookmarks.Count > 0)
        {
            foreach (PdfBookmark bookmark in bookmarks)
            {
                if (!indexes.TryGetValue(bookmark.Page, out int index))
                    continue;

                string title = Compact(bookmark.Title);
                string name = Compact(LeadingNumberRegex().Replace(bookmark.Title, string.Empty, 1));
                double reach = bookmark.Level == 0 ? 300 : 80;
                List<Line> candidates = [.. lines[index].Where(line => line.HeadingLevel == 0 && !line.IsFootnote && (bookmark.Top is not { } top || (line.Baseline <= top + 4 && line.Baseline >= top - reach)))];
                Line? heading = candidates.FirstOrDefault(line => Compact(line.PlainText) is { Length: > 0 } text
                        && (title.StartsWith(text, StringComparison.Ordinal) || text.StartsWith(title, StringComparison.Ordinal) || (name.Length > 3 && (name.StartsWith(text, StringComparison.Ordinal) || text == name))))
                    ?? candidates.FirstOrDefault(line => line.Items.Any(static item => (item.Style & GlyphStyle.Bold) != 0) && line.Size >= bodySize && bookmark.Level > 0);
                if (heading is null)
                {
                    if (!unmatched.TryGetValue(bookmark.Page, out List<PdfBookmark>? list))
                        unmatched[bookmark.Page] = list = [];

                    list.Add(bookmark);
                    continue;
                }

                foreach (Line chapter in candidates.Where(line => line != heading && ChapterRegex().IsMatch(line.PlainText) && line.Baseline > heading.Baseline))
                    chapter.HeadingLevel = -2;

                heading.HeadingLevel = Math.Clamp(bookmark.Level + 1, 1, 6);
                heading.HeadingTitle = bookmark.Title;
                // A long title wraps onto the next line with the same font.
                int next = lines[index].IndexOf(heading) + 1;
                while (next < lines[index].Count && lines[index][next] is { HeadingLevel: 0 } wrapped && Math.Abs(wrapped.Size - heading.Size) < 0.5
                    && heading.Baseline - wrapped.Baseline < heading.Size * 1.6 && title.Contains(Compact(wrapped.PlainText), StringComparison.Ordinal) && Compact(wrapped.PlainText).Length > 0)
                {
                    wrapped.HeadingLevel = -1;
                    next++;
                }
            }

            return unmatched;
        }

        // Without an outline, a short bold or a much larger line is a heading. A larger size gives a higher level.
        // A larger line in a regular font is a note, such as "This clause contains only informative text".
        List<double> sizes = [.. lines.SelectMany(static page => page).Where(line => line.Size > bodySize * 1.15 && line.Kind == LineKind.Text).Select(static line => Math.Round(line.Size)).Distinct().OrderDescending().Take(5)];
        Dictionary<Line, (int Level, int Dots)> headings = [];
        foreach (List<Line> page in lines)
        {
            if (IsContentsPage(page))
                continue;

            foreach (Line line in page.Where(static line => line.Kind == LineKind.Text && !line.IsFootnote))
            {
                string text = line.PlainText;
                if (line.InTable || text.Length > 120 || text.EndsWith('.') || !char.IsLetterOrDigit(text[0]))
                    continue;

                bool bold = line.Items.All(static item => (item.Style & GlyphStyle.Bold) != 0);
                int rank = sizes.IndexOf(Math.Round(line.Size));
                Match number = SectionNumberRegex().Match(text);
                int level = rank >= 0 && (bold || line.Size >= bodySize * 1.4) ? rank + 1 : 0;
                if (level > 0 || (bold && number.Success))
                    headings[line] = (level, number.Success ? number.Groups["number"].Value.Count(static c => c == '.') : -1);
            }
        }

        // A numbered heading takes its level from the depth of its number, below the level of the shortest numbers.
        List<(int Level, int Dots)> numbered = [.. headings.Values.Where(static candidate => candidate.Dots >= 0)];
        if (numbered.Count > 0)
        {
            int minDots = numbered.Min(static candidate => candidate.Dots);
            int top = numbered.Where(candidate => candidate.Dots == minDots && candidate.Level > 0).Select(static candidate => candidate.Level).DefaultIfEmpty(sizes.Count + 1).Min();
            foreach (Line line in headings.Keys.ToList().Where(line => headings[line].Dots >= 0))
                headings[line] = (Math.Min(6, top + headings[line].Dots - minDots), headings[line].Dots);
        }

        // A long title wraps onto the next line with the same font and no number.
        foreach (List<Line> page in lines)
        {
            Line? previous = null;
            int level = 0;
            foreach (Line line in page)
            {
                if (!headings.TryGetValue(line, out (int Level, int Dots) candidate))
                {
                    previous = null;
                    continue;
                }

                bool wraps = previous is not null && level == candidate.Level && candidate.Dots < 0
                    && Math.Abs(previous.Size - line.Size) < 0.5 && previous.Baseline - line.Baseline < line.Size * 1.6;
                line.HeadingLevel = wraps ? -1 : candidate.Level;
                if (!wraps)
                    level = candidate.Level;

                previous = line;
            }
        }

        return unmatched;
    }

    private static string Compact(string text) => string.Concat(text.Where(char.IsLetterOrDigit)).ToUpperInvariant();

    private static HashSet<string> Vocabulary(List<List<Line>> lines)
    {
        HashSet<string> words = new(StringComparer.OrdinalIgnoreCase);
        foreach (Line line in lines.SelectMany(static page => page).Where(static line => line.Kind == LineKind.Text))
        {
            foreach (Match match in WordRegex().Matches(line.PlainText))
                words.Add(match.Value);
        }

        return words;
    }

    private static List<PdfBlock> BuildBlocks(int page, List<Line> lines, List<PdfBookmark> headings)
    {
        List<PdfBlock> blocks = [];
        lines.RemoveAll(static line => line.HeadingLevel == -2);
        // A box of a note has its label, such as "NOTE", in a column left of its text. The label sits between the lines of the text and is no part of it.
        for (int i = lines.Count - 1; i >= 0; i--)
        {
            Line line = lines[i];
            bool beside = (i == 0 || line.Right < lines[i - 1].Left - 2) && (i == lines.Count - 1 || line.Right < lines[i + 1].Left - 2);
            if (beside && line.HeadingLevel == 0 && NoteLabelRegex().IsMatch(line.PlainText))
                lines.RemoveAt(i);
        }
        foreach (Line line in lines.Where(static line => line.Kind == LineKind.Math))
        {
            foreach (Item brace in line.Items.Where(static item => item.IsTall && item.Text is "{" or "\\{"))
            {
                foreach (Line other in lines.Where(other => other.Baseline <= brace.Top && other.Baseline >= brace.Bottom && other.Left > brace.Left))
                    other.Kind = LineKind.Math;
            }
        }

        // The left edge of the text of the page: a paragraph whose first line starts to the right of it is indented.
        double margin = lines.Where(static line => line.Kind == LineKind.Text && !line.InTable).Select(static line => Math.Round(line.Left)).GroupBy(static left => left)
            .OrderByDescending(static group => group.Count()).Select(static group => group.Key).DefaultIfEmpty(0).First();
        double right = lines.Where(static line => line.Kind == LineKind.Text && !line.InTable).Select(static line => line.Right).DefaultIfEmpty(0).Max();

        // TeX indents or centers a display formula. A line of a formula at the margin right after a line of text ends the sentence of that text.
        // After a full line the sentence goes on at the indent of its list item, and a tall inline formula widens the gap between the lines.
        for (int i = 1; i < lines.Count; i++)
        {
            Line line = lines[i];
            Line previous = lines[i - 1];
            if (line.Kind != LineKind.Math || previous.Kind != LineKind.Text || line.InTable)
                continue;

            double gap = previous.Baseline - line.Baseline;
            bool atMargin = Math.Abs(line.Left - margin) < line.Size && gap < line.Size * 1.6 && !line.Items.Any(static item => item.IsTall);
            // A display formula with its left edge near the margin, as in the style fleqn, starts at another edge than the text of a list item.
            bool listIndent = ListMarkerRegex().IsMatch(previous.PlainText) && line.Left > previous.Left + (line.Size * 0.5) && line.Left - previous.Left < line.Size * 3;
            bool goesOn = previous.Right >= right - (line.Size * 2) && listIndent && gap < (line.Size * 1.6) + Reach(previous, line) && !previous.PlainText.TrimEnd().EndsWith(':');
            if (atMargin || goesOn)
                line.Kind = LineKind.Text;
        }

        // A lone line of code inside a paragraph is a long name of the API that the paragraph wraps. A statement of code ends otherwise.
        for (int i = 1; i < lines.Count - 1; i++)
        {
            if (lines[i].Kind == LineKind.Code && lines[i - 1].Kind == LineKind.Text && lines[i + 1].Kind == LineKind.Text && !lines[i].InTable
                && lines[i - 1].Baseline - lines[i].Baseline < lines[i].Size * 1.6 && lines[i].Baseline - lines[i + 1].Baseline < lines[i].Size * 1.6
                && lines[i].PlainText.TrimEnd() is not [.., '{' or '}' or ';'])
                lines[i].Kind = LineKind.Text;
        }

        // An item of a list goes on at the indent of its text, also with a long identifier in monospace on the next line.
        // A listing under an item, after a line that ends with a colon or with more than one word per line, stays code.
        double? bulletLeft = null;
        for (int i = 0; i < lines.Count; i++)
        {
            Line line = lines[i];
            if (line.Kind == LineKind.Text && BulletStartRegex().IsMatch(line.PlainText))
            {
                bulletLeft = line.PlainText.TrimEnd().EndsWith(':') ? null : line.Left;
                continue;
            }

            bool goesOn = bulletLeft is { } left && line.Left > left + (line.Size * 0.3) && lines[i - 1].Baseline - line.Baseline < line.Size * 1.6 && !line.InTable;
            if (goesOn && line.Kind == LineKind.Code && !line.PlainText.Trim().Contains(' ', StringComparison.Ordinal))
            {
                line.Kind = LineKind.Text;
                bulletLeft = null;
                continue;
            }

            if (!goesOn || line.Kind != LineKind.Text || line.PlainText.TrimEnd().EndsWith(':'))
                bulletLeft = null;
        }

        ExtendCode(lines);

        Queue<PdfBookmark> pending = new(headings.OrderByDescending(static bookmark => bookmark.Top ?? double.MaxValue));
        foreach (Line line in lines)
        {
            // A heading without a line of its own goes before the first line below its target.
            while (pending.Count > 0 && (pending.Peek().Top ?? double.MaxValue) >= line.Top - 2)
            {
                PdfBookmark bookmark = pending.Dequeue();
                blocks.Add(new PdfBlock { Kind = PdfBlockKind.Heading, Page = page, Lines = [], HeadingLevel = Math.Clamp(bookmark.Level + 1, 1, 6), HeadingTitle = bookmark.Title, SyntheticTop = bookmark.Top });
            }

            if (line.HeadingLevel < 0)
            {
                blocks[^1].Lines.Add(line);
                continue;
            }

            PdfBlockKind kind = line switch
            {
                { HeadingLevel: > 0 } => PdfBlockKind.Heading,
                { IsFootnote: true } => PdfBlockKind.Footnote,
                { InTable: true } => PdfBlockKind.Table,
                { Kind: LineKind.Code } => PdfBlockKind.Code,
                { Kind: LineKind.Math } => PdfBlockKind.Math,
                _ when ListMarkerRegex().IsMatch(line.PlainText) => PdfBlockKind.ListItem,
                _ => PdfBlockKind.Paragraph,
            };

            PdfBlock? last = blocks.Count > 0 ? blocks[^1] : null;
            if (last is not null && Continues(last, kind, line, margin, right))
            {
                last.Lines.Add(line);
                continue;
            }

            blocks.Add(new PdfBlock { Kind = kind, Page = page, Lines = [line], HeadingLevel = Math.Max(0, line.HeadingLevel), HeadingTitle = line.HeadingTitle });
        }

        foreach (PdfBookmark bookmark in pending)
            blocks.Add(new PdfBlock { Kind = PdfBlockKind.Heading, Page = page, Lines = [], HeadingLevel = Math.Clamp(bookmark.Level + 1, 1, 6), HeadingTitle = bookmark.Title, SyntheticTop = bookmark.Top });

        return blocks;
    }

    // The pseudocode of a specification sets the keywords in monospace, the names of commands in the text font, the variables in math italics
    // and the braces as math symbols. Such a line is not mostly monospace, so a run of code takes in the lines next to it that look like code.
    private static void ExtendCode(List<Line> lines)
    {
        // A loop of pseudocode may hold no line that is mostly monospace, as "for (int i = 0; i < count; i++) {" with a variable in math italics.
        // A run of lines that look like code, from a line that opens a brace to the line of the closing brace, is code.
        for (int i = 0; i < lines.Count; i++)
        {
            if (lines[i].Kind == LineKind.Code || lines[i].InTable || !lines[i].PlainText.TrimEnd().EndsWith('{') || !LooksLikeCode(lines[i], lines[i]))
                continue;

            int end = i + 1;
            while (end < lines.Count && !IsClosingBrace(lines[end]) && LooksLikeCode(lines[end], lines[end - 1]))
                end++;

            if (end < lines.Count && IsClosingBrace(lines[end]) && LooksLikeCode(lines[end], lines[end - 1]))
            {
                for (int j = i; j <= end; j++)
                    lines[j].Kind = LineKind.Code;
            }
        }

        for (int i = 0; i < lines.Count; i++)
        {
            if (lines[i].Kind != LineKind.Code || lines[i].InTable)
                continue;

            for (int j = i - 1; j >= 0 && LooksLikeCode(lines[j], lines[j + 1]); j--)
                lines[j].Kind = LineKind.Code;

            for (int j = i + 1; j < lines.Count && LooksLikeCode(lines[j], lines[j - 1]); j++)
                lines[j].Kind = LineKind.Code;
        }

        // A comment of the pseudocode is one word with hyphens in the text font, such as "append-shader-info-log", between two lines of code.
        for (int i = 1; i < lines.Count - 1; i++)
        {
            if (lines[i].Kind != LineKind.Code && lines[i - 1].Kind == LineKind.Code && lines[i + 1].Kind == LineKind.Code && IsNear(lines[i], lines[i - 1]) && IsNear(lines[i], lines[i + 1])
                && !lines[i].InTable && !lines[i].PlainText.Contains(' ', StringComparison.Ordinal) && !lines[i].Items.Any(static item => item.IsMath))
                lines[i].Kind = LineKind.Code;
        }

        // A call that wraps puts its arguments on the next line. Outside code such a line goes on with the sentence of the call.
        foreach (Line line in lines.Where(static line => line.Kind == LineKind.Math && IsArgumentList(line)))
            line.Kind = LineKind.Text;

        // A closing brace alone is code, not a formula. A table at the top of a page may split it from its listing.
        foreach (Line line in lines.Where(static line => line.Kind == LineKind.Math && !line.InTable && IsClosingBrace(line) && !line.Items.Any(static item => item.IsTall || item.IsLatex)))
            line.Kind = LineKind.Code;
    }

    // A tall inline formula, such as a fraction, widens the gap between two lines by the part that reaches out of its line.
    private static double Reach(Line upper, Line lower) =>
        Math.Max(0, lower.Top - (lower.Baseline + (lower.Size * 0.75))) + Math.Max(0, upper.Baseline - (upper.Size * 0.25) - upper.Bottom);

    private static bool IsNear(Line line, Line neighbor) => Math.Abs(neighbor.Baseline - line.Baseline) < line.Size * 2.2;

    private static bool IsClosingBrace(Line line) => line.PlainText.Trim() is ['}', ..] text && text.All(static c => c is '}' or ';' or ' ');

    private static bool LooksLikeCode(Line line, Line neighbor)
    {
        if (line.Kind == LineKind.Code)
            return true;

        if (line.InTable || line.IsFootnote || line.HeadingLevel != 0 || !IsNear(line, neighbor) || line.Items.Any(static item => item.IsTall || item.IsLatex))
            return false;

        string text = line.PlainText.Trim();
        if (text.Length == 0 || text.All(static c => c is '{' or '}' or '(' or ')' or '[' or ']' or ';' or ' '))
            return true;

        // A comment of a listing may be set in the text font, such as "/* or */" or "// the default". A wrapped address such as "//www.khronos.org" is no comment.
        if (IsArgumentList(line) || CodeCommentRegex().IsMatch(text))
            return true;

        if (text.EndsWith('.'))
            return false;

        // A line of code holds some monospace and at most one word of the text font, the name of a command. A sentence holds more words.
        int mono = 0;
        int words = 0;
        Item? previous = null;
        foreach (Item item in line.Items)
        {
            bool isMono = (item.Style & GlyphStyle.Mono) != 0;
            bool isWord = !isMono && !item.IsMath && item.Text.Any(char.IsLetter);
            if (isMono)
                mono++;

            bool previousWord = previous is not null && (previous.Style & GlyphStyle.Mono) == 0 && !previous.IsMath && previous.Text.Any(char.IsLetter);
            if (isWord && (!previousWord || PdfLayout.NeedsSpace(previous!, item)))
                words++;

            previous = item;
        }

        return mono > 0 && words <= 1;
    }

    // The arguments of a call in math italics: "index, count, length, name);". A formula holds operators, scripts or numbers alone.
    private static bool IsArgumentList(Line line)
    {
        string text = line.PlainText.Trim();
        return text.Contains(',', StringComparison.Ordinal) && text[^1] is ',' or ';' or ')' && !line.Items.Any(static item => item.IsTall || item.IsLatex || item.Sub.Count > 0 || item.Sup.Count > 0)
            && text.All(static c => char.IsLetterOrDigit(c) || c is ',' or '(' or ')' or ';' or '&' or '*' or '[' or ']' or '_' or ' ')
            && ArgumentRegex().IsMatch(text);
    }

    private static bool Continues(PdfBlock block, PdfBlockKind kind, Line line, double margin, double right)
    {
        if (block.Lines.Count == 0)
            return false;

        Line previous = block.Lines[^1];
        double gap = previous.Baseline - line.Baseline;
        switch (block.Kind)
        {
            case PdfBlockKind.Heading:
                return false;
            case PdfBlockKind.Table:
                return kind == PdfBlockKind.Table;
            case PdfBlockKind.Code:
                // An empty line inside a listing leaves a gap of about two lines.
                return kind == PdfBlockKind.Code && gap < line.Size * 3.5;
            case PdfBlockKind.Math:
                return kind == PdfBlockKind.Math && gap < line.Size * 3;
            case PdfBlockKind.Footnote:
                return kind == PdfBlockKind.Footnote && !FootnoteStartRegex().IsMatch(line.PlainText);
        }

        // A paragraph or a list item goes on while the lines keep the leading, the size and the left edge. An indented first line starts a paragraph.
        if (kind is not (PdfBlockKind.Paragraph) || gap > (line.Size * 1.6) + Reach(previous, line) || Math.Abs(previous.Size - line.Size) > 1)
            return false;

        // The arguments of a call that wraps go on with the call.
        if (previous.PlainText.TrimEnd().EndsWith(',') && IsArgumentList(line))
            return true;

        double indent = line.Left - previous.Left;
        if (block.Kind == PdfBlockKind.ListItem)
            return indent > -line.Size * 0.5;

        // TeX indents the first line of a paragraph from the margin. A continued line starts at the margin, or where the previous line started.
        // Inside a framed box every line starts at the indent of the box, and a full previous line shows that the paragraph goes on.
        bool indented = line.Left - margin > line.Size * 0.8;
        bool previousFull = previous.Right - previous.Left >= (right - margin) * 0.8;
        return !indented || (previousFull && Math.Abs(indent) < line.Size * 0.5) || (indent > -line.Size * 0.5 && previous.Left - margin > line.Size * 0.8 && block.Lines.Count > 1);
    }

    // A short line of bold text alone, without an end of a sentence, is a heading below the outline, such as "Errors" or "Texture-Combined Samplers".
    // A page of the table of contents lists the headings with their page numbers at the ends of the lines.
    private static bool IsContentsPage(List<Line> page)
    {
        List<Line> texts = [.. page.Where(static line => line.Kind == LineKind.Text && !line.IsFootnote && !line.InTable)];
        return texts.Count >= 5 && texts.Count(static line => PageNumberEndRegex().IsMatch(line.PlainText)) * 3 >= texts.Count;
    }

    private static void MarkBoldHeadings(List<PdfBlock> blocks, HashSet<int> listPages)
    {
        // The labels of a diagram are bold lines too, but they come in groups. A heading stands alone between other blocks.
        // A bold line with a number, such as "4.1.10. Implicit Conversions", is a heading when its number goes on from the number of a heading before it:
        // a child of "4.1" or a sibling of "4.1.9". Its level follows from that heading. Numbered labels of a figure or rows of a table go on from no heading.
        bool[] candidates = [.. blocks.Select(IsBoldLine)];
        string?[] numbers = [.. blocks.Select((block, index) => candidates[index] ? SectionNumber(block.Lines[0].PlainText) : null)];
        List<(string Number, int Level)> numbered = [];
        int level = 0;
        for (int i = 0; i < blocks.Count; i++)
        {
            PdfBlock block = blocks[i];
            if (block.Kind == PdfBlockKind.Heading)
            {
                level = block.HeadingLevel;
                if (SectionNumber(block.HeadingTitle ?? string.Join(' ', block.Lines.Select(static line => line.PlainText))) is { } heading)
                    numbered.Add((heading, level));

                continue;
            }

            // The second half of a name of the API that a hyphen broke is no heading.
            bool continues = i > 0 && blocks[i - 1].Lines.Count > 0 && blocks[i - 1].Lines[^1].PlainText.TrimEnd().EndsWith('-');
            if (!candidates[i] || continues || level is 0 || listPages.Contains(block.Page))
                continue;

            // A line of the contents ends with a page number of two digits or more, or with dot leaders. "Transform Feedback 2" is a title.
            string text = block.Lines[0].PlainText.Trim();
            if (numbers[i] is { } number && NumberedLevel(numbered, number) is { } numberedLevel && !ContentsEndRegex().IsMatch(text))
            {
                level = Math.Min(6, numberedLevel);
                numbered.Add((number, level));
                block.Kind = PdfBlockKind.Heading;
                block.HeadingLevel = level;
                block.HeadingTitle = text;
                continue;
            }

            // A line that starts with a digit and goes on from no heading, such as the label "2 Rasterization" of a figure, is no heading.
            // A name such as "H.264 Picture Parameter Sets" counts as a label. A numbered heading next to a label is no part of its group.
            if (char.IsDigit(text[0]))
                continue;

            bool before = i > 0 && candidates[i - 1] && blocks[i - 1].Kind != PdfBlockKind.Heading;
            bool after = i < blocks.Count - 1 && candidates[i + 1] && (numbers[i + 1] is not { } next || NumberedLevel(numbered, next) is null);
            if (before || after || level >= 6)
                continue;

            block.Kind = PdfBlockKind.Heading;
            block.HeadingLevel = level + 1;
            block.HeadingTitle = text;
        }
    }

    // The level of a numbered line from the headings before it: one level below "4.1" for "4.1.10", the level of "4.1.9" for "4.1.10".
    // A number that goes on from no heading gives no level.
    private static int? NumberedLevel(List<(string Number, int Level)> numbered, string number)
    {
        int parent = numbered.FindLastIndex(heading => number.StartsWith(heading.Number + ".", StringComparison.Ordinal));
        int sibling = numbered.FindLastIndex(heading => ParentNumber(heading.Number) is { } shared && shared == ParentNumber(number));
        if (parent < 0 && sibling < 0)
            return null;

        return parent >= 0 && parent > sibling ? numbered[parent].Level + 1 : numbered[sibling].Level;
    }

    private static string? SectionNumber(string text) => SectionNumberRegex().Match(text.Trim()) is { Success: true } match ? match.Groups["number"].Value : null;

    private static string? ParentNumber(string number) => number.LastIndexOf('.') is var dot and > 0 ? number[..dot] : null;

    private static bool IsBoldLine(PdfBlock block)
    {
        if (block.Kind != PdfBlockKind.Paragraph || block.Lines.Count != 1)
            return false;

        // A heading below the outline may carry a number, such as "4.1.10. Implicit Conversions" of a specification whose outline stops at the second level.
        Line line = block.Lines[0];
        string text = line.PlainText.Trim();
        return text.Length is >= 3 and <= 80 && text[^1] is not ('.' or ':' or ',' or ';' or '-') && (char.IsLetter(text[0]) || SectionNumberRegex().IsMatch(text))
            && !text.Contains("://", StringComparison.Ordinal)
            && !text.Contains('=', StringComparison.Ordinal) && WordRegex().IsMatch(text) && !ChapterRegex().IsMatch(text)
            && line.Items.All(static item => (item.Style & GlyphStyle.Bold) != 0 && !item.IsMath);
    }

    // A paragraph at the end of a page without an end of a sentence goes on at the start of the next page when that line is not indented.
    private static void JoinPageBreaks(List<PdfBlock> blocks)
    {
        for (int i = blocks.Count - 1; i > 0; i--)
        {
            PdfBlock previous = blocks[i - 1];
            PdfBlock block = blocks[i];
            // A listing goes on at the top of the next page. Code holds no mark of a page, so its lines stay with the page of its start.
            if (block.Kind == PdfBlockKind.Code && previous.Kind == PdfBlockKind.Code && block.Page == previous.Page + 1 && block.Anchors.Count == 0)
            {
                previous.Lines.AddRange(block.Lines);
                blocks.RemoveAt(i);
                continue;
            }

            if (block.Page == previous.Page || block.Kind != PdfBlockKind.Paragraph || previous.Kind is not (PdfBlockKind.Paragraph or PdfBlockKind.ListItem))
                continue;

            string end = previous.Lines[^1].PlainText.TrimEnd();
            string start = block.Lines[0].PlainText.TrimStart();
            bool indented = block.Lines.Count > 1 && block.Lines[0].Left - block.Lines[1].Left > block.Lines[0].Size * 0.8;
            // A prototype starts with a type in monospace and ends with ");", such as "void PolygonOffset( float factor, float units );".
            // The next prototype on the next page starts with a type too.
            bool prototype = end.EndsWith(");", StringComparison.Ordinal) && (StartsWithMono(previous.Lines[0]) || StartsWithMono(block.Lines[0]));
            if (end.Length == 0 || start.Length == 0 || indented || end[^1] is '.' or ':' or '!' or '?' || prototype)
                continue;

            previous.Continuations.Add((previous.Lines.Count, block.Page));
            previous.Continuations.AddRange(block.Continuations.Select(continuation => (continuation.Line + previous.Lines.Count, continuation.Page)));
            previous.Lines.AddRange(block.Lines);
            previous.Anchors.AddRange(block.Anchors);
            blocks.RemoveAt(i);
        }
    }

    private static bool StartsWithMono(Line line) => line.Items.Count > 0 && (line.Items[0].Style & GlyphStyle.Mono) != 0;

    // Each link points at the first block at or below its target. That block gets an anchor named after its page and its place.
    private static Dictionary<(int Page, int Index), string> AnchorLinkTargets(IReadOnlyList<PdfPageModel> pages, List<PdfBlock> blocks)
    {
        Dictionary<int, List<PdfBlock>> byPage = blocks.GroupBy(static block => block.Page).ToDictionary(static group => group.Key, static group => group.ToList());
        Dictionary<(int Page, int Index), string> targets = [];
        foreach (PdfPageModel page in pages)
        {
            for (int i = 0; i < page.Links.Count; i++)
            {
                PageLink link = page.Links[i];
                PdfBlock? target = FindTarget(byPage, link.TargetPage, link.TargetTop);
                if (target is null)
                    continue;

                string anchor = target.Anchors.FirstOrDefault(static name => name.StartsWith('p')) ?? string.Create(CultureInfo.InvariantCulture, $"p{target.Page}-{byPage[target.Page].IndexOf(target)}");
                if (!target.Anchors.Contains(anchor))
                    target.Anchors.Add(anchor);

                targets[(page.Number, i)] = anchor;
            }
        }

        return targets;
    }

    private static PdfBlock? FindTarget(Dictionary<int, List<PdfBlock>> byPage, int page, double? top)
    {
        if (!byPage.TryGetValue(page, out List<PdfBlock>? blocks) || blocks.Count == 0)
            return byPage.TryGetValue(page + 1, out List<PdfBlock>? next) && next.Count > 0 ? next[0] : null;

        if (top is not { } y)
            return blocks[0];

        // A block belongs to the page where it starts. A joined paragraph may hold lines of the next page, so the search reads its first line.
        return blocks.FirstOrDefault(block => block.Top <= y + 3) ?? blocks[^1];
    }

    [GeneratedRegex(@"\d+")]
    private static partial Regex DigitsRegex();

    [GeneratedRegex(@"\b[ivxl]+\b")]
    private static partial Regex RomanNumberRegex();

    [GeneratedRegex(@"^\s*(?:\d+|[A-Z])(?:\.\d+)*\.?\s+")]
    private static partial Regex LeadingNumberRegex();

    [GeneratedRegex(@"^\s*(?:Chapter|Appendix|Глава|Приложение)\s+[\dA-Z]+\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ChapterRegex();

    // A running header of a chapter: capitals with a page number, such as "8.14. TEXTURE MINIFICATION 259", or a lone page number.
    [GeneratedRegex(@"^\s*(?:\d+|(?=(?:[^A-Z]*[A-Z]){3})(?:\d+\s+[^a-z]+|[^a-z]+\s+\d+))\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex PageHeaderRegex();

    [GeneratedRegex(@"^\s*(?:\d+|[*†‡§])\s*\S")]
    private static partial Regex FootnoteStartRegex();

    [GeneratedRegex(@"^\s*(?:[•◦▪‣–—-]|\(?\d{1,2}[.)]|\(?[a-z][.)])\s")]
    private static partial Regex ListMarkerRegex();

    // A number may start with a letter of an annex or a Roman number of a part: "B.2", "II.22.2".
    [GeneratedRegex(@"^(?<number>(?:\d+|(?:[IVX]{1,4}|[A-Z])\.\d+)(?:\.\d+)*)\.?\s+\S")]
    private static partial Regex SectionNumberRegex();

    [GeneratedRegex(@"\S\s+\d{1,4}$")]
    private static partial Regex PageNumberEndRegex();

    [GeneratedRegex(@"(?:\s\d{2,4}|\.\s?\.\s?\.\s*\d+)$")]
    private static partial Regex ContentsEndRegex();

    [GeneratedRegex(@"\p{L}{3,}")]
    private static partial Regex WordRegex();

    [GeneratedRegex(@"^\s*[•◦▪‣]\s")]
    private static partial Regex BulletStartRegex();

    [GeneratedRegex(@"^(?:/\*.*\*/$|// )")]
    private static partial Regex CodeCommentRegex();

    // The hidden bookmarks of a word processor: "OLE_LINK6", "_Toc123", "_Ref456".
    [GeneratedRegex(@"^(?:OLE_LINK|_Toc|_Ref|_Hlk|DD)\w*$", RegexOptions.CultureInvariant)]
    private static partial Regex InternalBookmarkRegex();

    [GeneratedRegex(@"^\s*(?:NOTE|WARNING|CAUTION|TIP|IMPORTANT)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NoteLabelRegex();

    [GeneratedRegex(@"(?:^|[\s,(])\p{L}[\p{L}\d]{3,}(?:[\s,)]|$)")]
    private static partial Regex ArgumentRegex();

    // The name of a file as a title: "GL3PGM.PDF", "Microsoft Word - spec.doc".
    [GeneratedRegex(@"^(?:Microsoft Word - )?[\w .()-]+\.(?:pdf|docx?|rtf|fm|ps)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FileNameRegex();
}

/// <summary>
/// The kind of a block of a PDF page.
/// </summary>
internal enum PdfBlockKind
{
    Heading,
    Paragraph,
    ListItem,
    Code,
    Math,
    Table,
    Footnote,
}

/// <summary>
/// A block of a PDF page: the lines of one heading, paragraph, list item, code, formula, table or footnote.
/// </summary>
internal sealed class PdfBlock
{
    public required PdfBlockKind Kind { get; set; }

    public required int Page { get; init; }

    public required List<Line> Lines { get; init; }

    public int HeadingLevel { get; set; }

    public string? HeadingTitle { get; set; }

    /// <summary>Gets the height of the target of a heading that has no line of its own, see <see cref="PdfMarkdownImporter"/>.</summary>
    public double? SyntheticTop { get; init; }

    /// <summary>Gets the top of the block: its first line, or the target of a heading without a line.</summary>
    public double Top => Lines.Count > 0 ? Lines[0].Top : SyntheticTop ?? double.MaxValue;

    /// <summary>Gets the anchors that the Markdown writes before the block.</summary>
    public List<string> Anchors { get; } = [];

    /// <summary>Gets the places where the block goes on at a new page: the index of the first line there and the page.</summary>
    public List<(int Line, int Page)> Continuations { get; } = [];
}
