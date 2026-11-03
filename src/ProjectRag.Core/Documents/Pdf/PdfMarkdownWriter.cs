using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ProjectRag.Core.Documents.Pdf;

/// <summary>
/// Writes the blocks of a PDF file as Markdown, see <see cref="PdfMarkdownImporter"/>.
/// </summary>
/// <param name="vocabulary">The words of the document. A hyphen at the end of a line goes when the joined word is one of them.</param>
/// <param name="pages">The pages by number, for their links.</param>
/// <param name="linkTargets">The anchors of the link targets by the page and the index of the link.</param>
internal sealed partial class PdfMarkdownWriter(HashSet<string> vocabulary, Dictionary<int, PdfPageModel> pages, Dictionary<(int Page, int Index), string> linkTargets)
{
    private enum SegmentKind
    {
        Text,
        Code,
        Math,
        Footnote,
        Mark,
    }

    /// <summary>
    /// Writes the blocks.
    /// </summary>
    /// <param name="blocks">The blocks in the order of the document.</param>
    /// <returns>The Markdown.</returns>
    public string Write(List<PdfBlock> blocks)
    {
        StringBuilder builder = new();
        int page = 0;
        HashSet<string> footnotes = [.. blocks.Where(static block => block.Kind == PdfBlockKind.Footnote).Select(FootnoteId).OfType<string>()];
        for (int i = 0; i < blocks.Count; i++)
        {
            PdfBlock block = blocks[i];
            if (block.Page != page)
            {
                page = block.Page;
                builder.Append(MarkdownStructureReader.PageMark(page)).Append("\n\n");
            }

            // The anchor of a caption below a table goes before the table, so a link reaches the table and its caption.
            if (block.Kind == PdfBlockKind.Table && i + 1 < blocks.Count && CaptionAnchor(blocks[i + 1]) is { } tableAnchor)
                block.Anchors.Insert(0, tableAnchor);
            else if (CaptionAnchor(block) is { } captionAnchor && !(i > 0 && blocks[i - 1].Kind == PdfBlockKind.Table))
                block.Anchors.Insert(0, captionAnchor);

            string text = WriteBlock(block, footnotes);
            if (text.Length == 0)
                continue;

            foreach (string anchor in block.Anchors.Distinct(StringComparer.Ordinal))
                builder.Append(MarkdownStructureReader.AnchorMark(anchor)).Append('\n');

            builder.Append(text).Append("\n\n");
        }

        return builder.ToString().TrimEnd() + "\n";
    }

    private static string? FootnoteId(PdfBlock block) =>
        FootnoteNumberRegex().Match(block.Lines[0].PlainText) is { Success: true } match ? string.Create(CultureInfo.InvariantCulture, $"p{block.Page}-{match.Groups["number"].Value}") : null;

    private static string? CaptionAnchor(PdfBlock block)
    {
        if (block.Kind != PdfBlockKind.Paragraph || CaptionRegex().Match(block.Lines[0].PlainText) is not { Success: true } match)
            return null;

        return $"{match.Groups["kind"].Value.ToLowerInvariant()}-{match.Groups["number"].Value.TrimEnd('.')}";
    }

    private string WriteBlock(PdfBlock block, HashSet<string> footnotes) => block.Kind switch
    {
        PdfBlockKind.Heading => $"{new string('#', block.HeadingLevel)} {EscapeHeading(block.HeadingTitle ?? string.Join(' ', block.Lines.Select(static line => line.PlainText)))}",
        PdfBlockKind.Code => WriteCode(block.Lines),
        PdfBlockKind.Math => WriteMath(block),
        PdfBlockKind.Table => WriteTable(block, footnotes),
        PdfBlockKind.Footnote => WriteFootnote(block, footnotes),
        PdfBlockKind.ListItem => WriteListItem(block, footnotes),
        _ => EscapeStart(WriteInline(block, footnotes)),
    };

    private string WriteListItem(PdfBlock block, HashSet<string> footnotes)
    {
        string text = WriteInline(block, footnotes);
        // A bullet becomes the bullet of Markdown. A number or a letter keeps its text.
        Match bullet = BulletRegex().Match(text);
        return bullet.Success ? $"- {text[bullet.Length..]}" : EscapeStart(text);
    }

    private string WriteFootnote(PdfBlock block, HashSet<string> footnotes)
    {
        string text = WriteInline(block, footnotes);
        return FootnoteId(block) is { } id && FootnoteNumberRegex().Match(text) is { Success: true } match
            ? $"[^{id}]: {text[match.Length..].TrimStart()}"
            : EscapeStart(text);
    }

    // The inline text of a block: the lines joined with spaces, a hyphen at a line end resolved, and the page marks of a block over a page break.
    private string WriteInline(PdfBlock block, HashSet<string> footnotes, bool inTable = false)
    {
        List<(SegmentKind Kind, string Text, string? Link)> segments = [];
        for (int i = 0; i < block.Lines.Count; i++)
        {
            Line line = block.Lines[i];
            List<(SegmentKind Kind, string Text, string? Link)> lineSegments = Segments(line, footnotes);
            if (lineSegments.Count == 0)
                continue;

            if (block.Continuations.FirstOrDefault(c => c.Line == i) is { Page: > 0 } continuation)
                segments.Add((SegmentKind.Mark, $" {MarkdownStructureReader.PageMark(continuation.Page)} ", null));
            else if (segments.Count > 0)
                Join(segments, lineSegments);

            foreach ((SegmentKind Kind, string Text, string? Link) segment in lineSegments)
                Add(segments, segment);
        }

        StringBuilder builder = new();
        string? link = null;
        foreach ((SegmentKind kind, string text, string? target) in segments)
        {
            if (target != link)
            {
                if (link is not null)
                    builder.Append("](#").Append(link).Append(')');

                if (target is not null)
                    builder.Append('[');

                link = target;
            }

            builder.Append(kind switch
            {
                // A cell of a pipe table ends at a pipe, also inside a code span or a formula. The text escapes its pipes itself.
                SegmentKind.Code => inTable ? CodeSpan(text).Replace("|", "\\|", StringComparison.Ordinal) : CodeSpan(text),
                // An empty formula would write "$$", the fence of a display formula.
                SegmentKind.Math when IsEmptyMath(text) => " ",
                SegmentKind.Math => inTable ? $"${text.Trim().Replace("|", "\\|", StringComparison.Ordinal)}$" : $"${text.Trim()}$",
                SegmentKind.Footnote => $"[^{text}]",
                SegmentKind.Mark => text,
                _ => EscapeText(text),
            });
        }

        if (link is not null)
            builder.Append("](#").Append(link).Append(')');

        // TeX leaves a gap between a formula and the punctuation after it. Markdown needs none.
        return SpaceBeforePunctuationRegex().Replace(MultiSpaceRegex().Replace(builder.ToString(), " "), string.Empty).Trim();
    }

    private static bool IsEmptyMath(string latex) => latex.Replace("\\quad", string.Empty, StringComparison.Ordinal).Trim().Length == 0;

    // Joins the end of a line with the start of the next: a hyphenated word, a name of the API that TeX broke after "_-", or a space.
    private void Join(List<(SegmentKind Kind, string Text, string? Link)> segments, List<(SegmentKind Kind, string Text, string? Link)> next)
    {
        (SegmentKind kind, string text, string? link) = segments[^1];
        (SegmentKind nextKind, string nextText, _) = next[0];
        if (kind == SegmentKind.Code && nextKind == SegmentKind.Code && text.EndsWith("_-", StringComparison.Ordinal))
        {
            segments[^1] = (kind, text[..^1], link);
            return;
        }

        if (kind == SegmentKind.Text && nextKind == SegmentKind.Text && HyphenEndRegex().Match(text) is { Success: true } end && WordStartRegex().Match(nextText) is { Success: true } start)
        {
            string joined = end.Groups["word"].Value + start.Value;
            segments[^1] = (kind, vocabulary.Contains(joined) ? text[..^1] : text, link);
            return;
        }

        Add(segments, (SegmentKind.Text, " ", null));
    }

    private static void Add(List<(SegmentKind Kind, string Text, string? Link)> segments, (SegmentKind Kind, string Text, string? Link) segment)
    {
        // Neighbor runs of one kind and one link merge, so a code span or a formula does not break at a line end.
        if (segments.Count > 0 && segments[^1].Kind == segment.Kind && segments[^1].Link == segment.Link && segment.Kind is SegmentKind.Text or SegmentKind.Code or SegmentKind.Math)
        {
            string separator = segment.Kind == SegmentKind.Math && segments[^1].Text.Length > 0 ? " " : string.Empty;
            segments[^1] = (segment.Kind, segments[^1].Text + separator + segment.Text, segment.Link);
            return;
        }

        if (segments.Count > 0 && segments[^1].Kind == SegmentKind.Text && segments[^1].Text == " " && segment.Kind == SegmentKind.Text && segment.Link == segments[^1].Link)
        {
            segments[^1] = (SegmentKind.Text, " " + segment.Text, segment.Link);
            return;
        }

        segments.Add(segment);
    }

    // The runs of a line: text, code (a monospace font), formulas (a math font) and footnote marks. A link wraps the items under its area.
    private List<(SegmentKind Kind, string Text, string? Link)> Segments(Line line, HashSet<string> footnotes)
    {
        List<(SegmentKind Kind, string Text, string? Link)> segments = [];
        PdfPageModel? page = pages.GetValueOrDefault(line.Page);
        int i = 0;
        while (i < line.Items.Count)
        {
            Item item = line.Items[i];
            string? link = LinkAt(page, line.Page, item);
            SegmentKind kind = item.IsMath ? SegmentKind.Math : (item.Style & GlyphStyle.Mono) != 0 ? SegmentKind.Code : SegmentKind.Text;
            int end = i + 1;
            while (end < line.Items.Count && KindOf(line.Items[end]) == kind && LinkAt(page, line.Page, line.Items[end]) == link
                && (kind != SegmentKind.Text || !HasFootnote(line.Items[end - 1], line.Page, footnotes)))
                end++;

            List<Item> run = line.Items.GetRange(i, end - i);
            if (i > 0 && PdfLayout.NeedsSpace(line.Items[i - 1], item))
                segments.Add((SegmentKind.Text, " ", null));

            switch (kind)
            {
                case SegmentKind.Math:
                    segments.Add((SegmentKind.Math, MathWriter.Write(run), link));
                    break;
                case SegmentKind.Code:
                    segments.Add((SegmentKind.Code, MonoText(run), link));
                    break;
                default:
                    segments.Add((SegmentKind.Text, TextOf(run), link));
                    if (FootnoteOf(run[^1], line.Page, footnotes) is { } footnote)
                        segments.Add((SegmentKind.Footnote, footnote, null));

                    break;
            }

            i = end;
        }

        // A short italic word between two runs of code is a parameter of a prototype: "double l, double r".
        for (int k = segments.Count - 2; k > 0; k--)
        {
            if (segments[k].Kind == SegmentKind.Text && segments[k].Text.Trim().Length is > 0 and <= 2 && segments[k - 1].Kind == SegmentKind.Code
                && segments.Skip(k + 1).SkipWhile(static segment => segment.Kind == SegmentKind.Text && segment.Text == " ").FirstOrDefault().Kind == SegmentKind.Code)
                segments[k] = (SegmentKind.Code, segments[k].Text, segments[k].Link);
        }

        List<(SegmentKind Kind, string Text, string? Link)> merged = [];
        foreach ((SegmentKind Kind, string Text, string? Link) segment in segments)
        {
            // A space between two runs of code joins them into one code span.
            if (merged.Count > 1 && segment.Kind == SegmentKind.Code && merged[^1] is { Kind: SegmentKind.Text, Text: " " } && merged[^2].Kind == SegmentKind.Code && merged[^2].Link == segment.Link)
            {
                merged.RemoveAt(merged.Count - 1);
                merged[^1] = (SegmentKind.Code, $"{merged[^1].Text} {segment.Text}", segment.Link);
                continue;
            }

            if (merged.Count > 0 && segment.Kind == SegmentKind.Code && merged[^1].Kind == SegmentKind.Code && merged[^1].Link == segment.Link)
            {
                merged[^1] = (SegmentKind.Code, merged[^1].Text + segment.Text, segment.Link);
                continue;
            }

            merged.Add(segment);
        }

        return merged;
    }

    private static SegmentKind KindOf(Item item) => item.IsMath ? SegmentKind.Math : (item.Style & GlyphStyle.Mono) != 0 ? SegmentKind.Code : SegmentKind.Text;

    private string? LinkAt(PdfPageModel? page, int number, Item item)
    {
        if (page is null)
            return null;

        double x = (item.Left + item.Right) / 2;
        double y = item.Baseline + (item.Size * 0.3);
        for (int i = 0; i < page.Links.Count; i++)
        {
            PageLink link = page.Links[i];
            if (x >= link.Left && x <= link.Right && y >= link.Bottom && y <= link.Top && linkTargets.TryGetValue((number, i), out string? anchor))
                return anchor;
        }

        return null;
    }

    private static bool HasFootnote(Item item, int page, HashSet<string> footnotes) => FootnoteOf(item, page, footnotes) is not null;

    // A raised number after a word of the text is the mark of a footnote on the same page.
    private static string? FootnoteOf(Item item, int page, HashSet<string> footnotes)
    {
        if (item.Sup.Count == 0 || !item.Sup.All(static script => script.Text.All(char.IsDigit)))
            return null;

        string id = string.Create(CultureInfo.InvariantCulture, $"p{page}-{string.Concat(item.Sup.Select(static script => script.Text))}");
        return footnotes.Contains(id) ? id : null;
    }

    private static string TextOf(List<Item> run)
    {
        StringBuilder builder = new();
        Item? previous = null;
        foreach (Item item in run)
        {
            if (previous is not null && PdfLayout.NeedsSpace(previous, item))
                builder.Append(' ');

            builder.Append(item.Text);
            // A script of the text that is no footnote stays as text: "2nd", "x2".
            if (item.Sup.Count > 0 && !item.Sup.All(static script => script.Text.All(char.IsDigit)))
                builder.Append(PdfLayout.SupText(item));

            builder.Append(string.Concat(item.Sub.Select(static script => script.Text)));
            previous = item;
        }

        return builder.ToString();
    }

    private static string MonoText(List<Item> run)
    {
        StringBuilder builder = new();
        Item? previous = null;
        double previousRight = 0;
        foreach (Item item in run)
        {
            if (previous is not null)
            {
                double width = PdfLayout.MonoWidth(previous);
                // The gap after a variable starts at the end of its scripts: "s_c, t_c". The mark of a footnote that the text leaves out
                // fills its gap too, unless the script is a piece of another line that reaches past the next item.
                double scripts = previous.Sub.Concat(previous.Sup).Select(static script => script.Right).DefaultIfEmpty(previousRight).Max();
                double from = scripts <= item.Left + 0.5 ? Math.Max(previousRight, scripts) : previousRight;
                int spaces = (int)Math.Round((item.Left - from) / width);
                // An italic parameter after a monospace type keeps its space: "double l".
                if (spaces == 0 && item.Left - from > Math.Max(previous.Size, item.Size) * 0.1 && PdfLayout.NeedsSpace(previous, item))
                    spaces = 1;

                // Beside a word of another font the gap is a space between words, not a column of the monospace grid.
                if (((previous.Style & item.Style) & GlyphStyle.Mono) == 0)
                    spaces = Math.Min(spaces, 1);

                if (spaces > 0)
                    builder.Append(' ', Math.Min(spaces, 40));
            }

            builder.Append(item.Text);
            previousRight = item.Right;
            // A variable may carry a short script, such as the c of s_c or the i of T_i. A long script is a piece of another line.
            List<Item> sub = [.. item.Sub];
            string subText = string.Concat(sub.Select(static script => script.Text));
            if (subText.Length is > 0 and <= 3)
            {
                builder.Append('_').Append(subText);
                previousRight = Math.Max(previousRight, sub.Max(static script => script.Right));
            }

            // A prime is an apostrophe as in the formulas. A mark of a footnote, such as "1" or "†", stays out of a name.
            string supText = string.Concat(item.Sup.Select(static script => script.Text)).Replace("″", "''", StringComparison.Ordinal).Replace('′', '\'');
            if (supText.Length > 0 && supText.All(static c => c == '\''))
            {
                builder.Append(supText);
                previousRight = Math.Max(previousRight, item.Sup.Max(static script => script.Right));
            }
            else if (supText.Length is > 0 and <= 3 && supText.All(char.IsLetterOrDigit) && !supText.All(char.IsDigit))
            {
                builder.Append('^').Append(supText);
                previousRight = Math.Max(previousRight, item.Sup.Max(static script => script.Right));
            }

            previous = item;
        }

        return builder.ToString();
    }

    // Code keeps its indentation: the distance from the leftmost line in characters of the font.
    private static string WriteCode(List<Line> lines)
    {
        Dictionary<int, double> lefts = lines.GroupBy(static line => line.Page).ToDictionary(static group => group.Key, static group => group.Min(static line => line.Left));
        double width = lines.SelectMany(static line => line.Items).Where(static item => (item.Style & GlyphStyle.Mono) != 0).Select(PdfLayout.MonoWidth).DefaultIfEmpty(6).Average();
        List<string> body = [];
        for (int i = 0; i < lines.Count; i++)
        {
            // An empty line between two lines of code shows as a larger gap.
            if (i > 0 && lines[i - 1].Baseline - lines[i].Baseline > lines[i].Size * 1.8)
                body.Add(string.Empty);

            int indent = (int)Math.Round((lines[i].Left - lefts[lines[i].Page]) / width);
            body.Add(new string(' ', Math.Clamp(indent, 0, 40)) + MonoText(lines[i].Items));
        }

        string code = string.Join('\n', body);
        string fence = code.Contains("```", StringComparison.Ordinal) ? "~~~~" : "```";
        return $"{fence}\n{code}\n{fence}";
    }

    // A display formula: one row, rows in "gathered", or a large brace with the rows on its right as "cases". A number at the right becomes a tag.
    private static string WriteMath(PdfBlock block)
    {
        List<Line> lines = block.Lines;
        string? tag = null;
        foreach (Line line in lines)
        {
            if (line.Items.Count > 1 && EquationNumberRegex().Match(PdfLayout.PlainText(line.Items.TakeLast(Math.Min(7, line.Items.Count)))) is { Success: true } number)
            {
                // The number takes the last items of the line up to its opening parenthesis.
                int start = line.Items.FindLastIndex(static item => item.Text == "(");
                if (start > 0 && line.Items[start].Left - line.Items[start - 1].Right > line.Size * 1.5)
                {
                    tag = number.Groups["number"].Value;
                    line.Items.RemoveRange(start, line.Items.Count - start);
                }
            }
        }

        if (tag is not null)
            block.Anchors.Add($"equation-{tag}");

        List<Line> rows = [.. lines.Where(static line => line.Items.Count > 0)];
        string latex = Cases(rows) ?? (rows.Count == 1 ? MathWriter.Write(rows[0].Items) : $"\\begin{{gathered}}\n{string.Join(" \\\\\n", rows.Select(static row => MathWriter.Write(row.Items)))}\n\\end{{gathered}}");
        if (tag is not null)
            latex += $" \\tag{{{tag}}}";

        if (latex.Length == 0)
            return string.Empty;

        // LaTeX that a renderer cannot read becomes plain text: the reader still sees the symbols.
        return MathWriter.IsBalanced(latex)
            ? $"$$\n{latex}\n$$"
            : $"```text\n{string.Join('\n', rows.Select(static row => row.PlainText))}\n```";
    }

    private static string? Cases(List<Line> rows)
    {
        foreach (Line row in rows)
        {
            Item? brace = row.Items.FirstOrDefault(static item => item.IsTall && item.Text is "{" or "\\{");
            if (brace is null)
                continue;

            // The rows on the right of the brace inside its height are the cases. A wide gap in a case separates the value from its condition.
            // An item belongs to the right side when its middle lies right of the middle of the brace: the outline of a brace is wide.
            double middle = (brace.Left + brace.Right) / 2;
            List<Line> cases = [.. rows.Where(other => other.Baseline <= brace.Top && other.Baseline >= brace.Bottom && other.Items.Any(item => item != brace && (item.Left + item.Right) / 2 > middle))];
            if (cases.Count < 2)
                return null;

            string before = MathWriter.Write([.. row.Items.Where(item => item != brace && (item.Left + item.Right) / 2 <= middle)]);
            IEnumerable<string> lines = cases.Select(other =>
            {
                List<Item> right = [.. other.Items.Where(item => item != brace && (item.Left + item.Right) / 2 > middle)];
                int split = right.FindIndex(static item => !item.IsMath && item.Text.Any(char.IsLetter));
                return split > 0
                    ? $"{MathWriter.Write(right[..split])} & {MathWriter.Text(right[split..])}"
                    : MathWriter.Write(right);
            });
            return $"{before} \\begin{{cases}} {string.Join(" \\\\ ", lines)} \\end{{cases}}".Trim();
        }

        return null;
    }

    // A table with clear columns becomes a pipe table. A row with an empty first cell continues the cells of the row above.
    private string WriteTable(PdfBlock block, HashSet<string> footnotes)
    {
        if (block.Lines.All(static line => line.CellEdges is not null))
            return WriteGridTable(block, footnotes);

        List<List<List<Item>>> rows = [.. block.Lines.Select(PdfMarkdownImporter.Segments)];
        List<double> columns = [];
        foreach (List<Item> segment in rows.SelectMany(static row => row))
        {
            double left = segment[0].Left;
            if (!columns.Any(column => Math.Abs(column - left) < 8))
                columns.Add(left);
        }

        columns.Sort();
        int maxCells = rows.Max(static row => row.Count);
        bool clear = columns.Count >= 2 && columns.Count <= 8 && columns.Count == maxCells && rows.Count(row => row.Count == maxCells) * 2 >= rows.Count;
        if (!clear)
            return $"```text\n{string.Join('\n', block.Lines.Select(static line => string.Join("   ", PdfMarkdownImporter.Segments(line).Select(PdfLayout.PlainText))))}\n```";

        List<string[]> cells = [];
        foreach (List<List<Item>> row in rows)
        {
            string[] values = new string[columns.Count];
            foreach (List<Item> segment in row)
            {
                int column = columns.Select((x, index) => (Distance: Math.Abs(x - segment[0].Left), index)).MinBy(static pair => pair.Distance).index;
                Line cellLine = new() { Page = block.Page, Items = segment, Size = segment[0].Size, Baseline = segment[0].Baseline };
                string text = WriteInline(new PdfBlock { Kind = PdfBlockKind.Paragraph, Page = block.Page, Lines = [cellLine] }, footnotes, inTable: true);
                values[column] = values[column] is null ? text : $"{values[column]} {text}";
            }

            if (cells.Count > 0 && string.IsNullOrEmpty(values[0]))
            {
                string[] previous = cells[^1];
                for (int i = 0; i < values.Length; i++)
                {
                    if (!string.IsNullOrEmpty(values[i]))
                        previous[i] = string.IsNullOrEmpty(previous[i]) ? values[i] : $"{previous[i]} {values[i]}";
                }

                continue;
            }

            cells.Add(values);
        }

        StringBuilder builder = new();
        builder.Append("| ").AppendJoin(" | ", cells[0].Select(static value => value ?? string.Empty)).Append(" |\n");
        builder.Append('|').Append(string.Concat(Enumerable.Repeat(" --- |", columns.Count))).Append('\n');
        foreach (string[] row in cells.Skip(1))
            builder.Append("| ").AppendJoin(" | ", row.Select(static value => value ?? string.Empty)).Append(" |\n");

        return builder.ToString().TrimEnd();
    }

    // The borders of the cells give the columns. A cell takes the items whose middle lies between its edges, from all lines of its row.
    // A table over a page break repeats its head on the next page, and the copy goes away.
    private string WriteGridTable(PdfBlock block, HashSet<string> footnotes)
    {
        double[] edges = block.Lines.Select(static line => line.CellEdges!).MaxBy(static cells => cells.Length)!;
        List<string[]> rows = [];
        foreach (IGrouping<(int Page, int Row), Line> row in block.Lines.GroupBy(static line => (line.Page, line.CellRow)))
        {
            string[] values = new string[edges.Length - 1];
            for (int column = 0; column < values.Length; column++)
            {
                List<Line> parts = [];
                foreach (Line line in row)
                {
                    List<Item> items = [.. line.Items.Where(item => Column(edges, (item.Left + item.Right) / 2) == column)];
                    if (items.Count > 0)
                        parts.Add(new Line { Page = line.Page, Items = items, Size = line.Size, Baseline = line.Baseline, Kind = line.Kind });
                }

                values[column] = parts.Count == 0 ? string.Empty : WriteInline(new PdfBlock { Kind = PdfBlockKind.Paragraph, Page = row.Key.Page, Lines = parts }, footnotes, inTable: true);
            }

            if (rows.Count > 0 && values.SequenceEqual(rows[0]))
                continue;

            rows.Add(values);
        }

        StringBuilder builder = new();
        builder.Append("| ").AppendJoin(" | ", rows[0]).Append(" |\n");
        builder.Append('|').Append(string.Concat(Enumerable.Repeat(" --- |", edges.Length - 1))).Append('\n');
        foreach (string[] row in rows.Skip(1))
            builder.Append("| ").AppendJoin(" | ", row).Append(" |\n");

        return builder.ToString().TrimEnd();
    }

    private static int Column(double[] edges, double x)
    {
        for (int i = 1; i < edges.Length - 1; i++)
        {
            if (x < edges[i])
                return i - 1;
        }

        return edges.Length - 2;
    }

    private static string CodeSpan(string text)
    {
        string trimmed = text.Trim();
        if (trimmed.Length == 0)
            return string.Empty;

        string fence = trimmed.Contains('`', StringComparison.Ordinal) ? "``" : "`";
        string pad = trimmed.StartsWith('`') || trimmed.EndsWith('`') ? " " : string.Empty;
        string lead = text.StartsWith(' ') ? " " : string.Empty;
        string tail = text.EndsWith(' ') ? " " : string.Empty;
        return $"{lead}{fence}{pad}{trimmed}{pad}{fence}{tail}";
    }

    // The text of a PDF file holds no Markdown, so the characters with a meaning in Markdown get a backslash.
    // An underscore inside a word has no meaning, so a name such as gl_Position keeps it without a backslash.
    private static string EscapeText(string text)
    {
        StringBuilder builder = new(text.Length + 8);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            bool inWord = i > 0 && i < text.Length - 1 && char.IsLetterOrDigit(text[i - 1]) && char.IsLetterOrDigit(text[i + 1]);
            if (c is '\\' or '`' or '*' or '[' or ']' or '<' or '>' or '$' or '|' || (c == '_' && !inWord))
                builder.Append('\\');

            builder.Append(c);
        }

        return builder.ToString();
    }

    // A "#" at the end of a heading would close it, so "C#" keeps its sign escaped.
    private static string EscapeHeading(string text)
    {
        string escaped = EscapeText(text).Trim();
        return escaped.EndsWith('#') ? $"{escaped[..^1]}\\#" : escaped;
    }

    // A paragraph that starts like a heading, a quote, a list or a rule would change its kind.
    // Markdown escapes only punctuation, so a number of a list keeps its digits and escapes its dot: "1\. Text".
    private static string EscapeStart(string text)
    {
        Match match = BlockStartRegex().Match(text);
        if (!match.Success)
            return text;

        int digits = match.Groups["digits"].Length;
        return digits > 0 ? $"{text[..digits]}\\{text[digits..]}" : $"\\{text}";
    }

    [GeneratedRegex(@"^(?<number>\d+|[*†‡§])\s*")]
    private static partial Regex FootnoteNumberRegex();

    [GeneratedRegex(@"^(?<kind>Table|Figure|Listing|Equation)\s+(?<number>[\dA-Z]+(?:\.\d+)*)\.?\s*[:.]", RegexOptions.CultureInvariant)]
    private static partial Regex CaptionRegex();

    [GeneratedRegex(@"^[•◦▪‣–—-]\s*")]
    private static partial Regex BulletRegex();

    [GeneratedRegex(@"\((?<number>\d+(?:\.\d+)*)\)\s*$")]
    private static partial Regex EquationNumberRegex();

    [GeneratedRegex(@"(?<word>\p{L}+)-$")]
    private static partial Regex HyphenEndRegex();

    [GeneratedRegex(@"^\p{Ll}+")]
    private static partial Regex WordStartRegex();

    [GeneratedRegex(@"^(?:#|>|[-+=]\s|(?<digits>\d+)[.)]\s)")]
    private static partial Regex BlockStartRegex();

    [GeneratedRegex(@"[ \t]{2,}")]
    private static partial Regex MultiSpaceRegex();

    [GeneratedRegex(@"(?<=[$`]) (?=[.,;:!?)])")]
    private static partial Regex SpaceBeforePunctuationRegex();
}
