using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using UglyToad.PdfPig.Outline;

namespace ProjectRag.Core.Documents;

/// <summary>
/// Reads the text of a PDF file with PdfPig and turns its bookmarks into Markdown headings.
/// </summary>
/// <remarks>
/// <para>
/// The text of each page comes in the order of the content stream. The reader normalizes it to the form NFKC, so the ligature "ﬁ" becomes "fi"
/// and the full text search finds the word. The reader drops the running headers and footers among the first and the last two lines of a page:
/// a line that repeats on many pages, where a number counts as any number, and a line in capitals with a page number, such as "6.5. BUFFER DATA 80".
/// </para>
/// <para>
/// A bookmark becomes a heading of its level. The reader looks for the title of the bookmark on its page and puts the heading there.
/// When the page does not hold the title, the heading goes before the rest of the page.
/// So the chunker splits a specification along its chapters, and each chunk keeps the path of its section.
/// </para>
/// <para>
/// A line of the page that looks like Markdown structure, for example <c># define</c> in a text about the preprocessor, gets a backslash before it.
/// </para>
/// </remarks>
internal static partial class PdfTextReader
{
    private const int EdgeLines = 2;
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// Reads a PDF file.
    /// </summary>
    /// <param name="bytes">The bytes of the file.</param>
    /// <param name="fallbackTitle">The title when the file names none.</param>
    /// <returns>The text with the bookmarks as headings.</returns>
    public static DocumentText Read(byte[] bytes, string fallbackTitle)
    {
        using PdfDocument pdf = PdfDocument.Open(bytes);
        string title = Normalize(pdf.Information.Title ?? string.Empty);
        if (title.Length == 0)
            title = fallbackTitle;

        ContentOrderTextExtractor.Options options = new()
        {
            ReplaceWhitespaceWithSpace = true,
            SeparateParagraphsWithDoubleNewline = true,
        };

        List<string[]> pages = [];
        foreach (Page page in pdf.GetPages())
        {
            string text = TextCleanup.Clean(ContentOrderTextExtractor.GetText(page, options).Normalize(NormalizationForm.FormKC));
            pages.Add([.. text.Split('\n').Select(static line => line.TrimEnd())]);
        }

        RemoveRunningLines(pages);

        Dictionary<int, List<Mark>> marks = [];
        if (pdf.TryGetBookmarks(out Bookmarks? bookmarks))
        {
            foreach (BookmarkNode root in bookmarks.Roots)
                CollectMarks(root, marks);
        }

        StringBuilder builder = new();
        for (int i = 0; i < pages.Count; i++)
        {
            string text = string.Join('\n', pages[i].Select(EscapeLine)).Trim();
            AppendPage(builder, text, marks.GetValueOrDefault(i + 1));
        }

        return new DocumentText(title, builder.ToString().Trim());
    }

    private static void CollectMarks(BookmarkNode node, Dictionary<int, List<Mark>> marks)
    {
        string title = Normalize(node.Title ?? string.Empty);
        if (node is DocumentBookmarkNode document && title.Length > 0)
        {
            if (!marks.TryGetValue(document.PageNumber, out List<Mark>? list))
                marks[document.PageNumber] = list = [];

            list.Add(new Mark(title, Math.Clamp(node.Level + 1, 1, 6)));
        }

        foreach (BookmarkNode child in node.Children)
            CollectMarks(child, marks);
    }

    private static void AppendPage(StringBuilder builder, string text, List<Mark>? marks)
    {
        if (builder.Length > 0)
            builder.Append("\n\n");

        int cursor = 0;
        foreach (Mark mark in marks ?? [])
        {
            Match match = FindTitle(text, mark.Title, cursor);
            if (match.Success)
            {
                builder.Append(text, cursor, match.Index - cursor);
                cursor = match.Index + match.Length;
            }

            builder.Append("\n\n").Append('#', mark.Level).Append(' ').Append(mark.Title).Append("\n\n");
        }

        builder.Append(text, cursor, text.Length - cursor);
    }

    private static Match FindTitle(string text, string title, int start)
    {
        string pattern = string.Join(@"\s+", title.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Regex.Escape));
        try
        {
            return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, MatchTimeout).Match(text, start);
        }
        catch (RegexMatchTimeoutException)
        {
            return Match.Empty;
        }
    }

    private static void RemoveRunningLines(List<string[]> pages)
    {
        if (pages.Count < 4)
            return;

        Dictionary<string, int> counts = new(StringComparer.Ordinal);
        foreach (string[] lines in pages)
        {
            foreach (string key in EdgeIndexes(lines).Select(index => LineKey(lines[index])).Distinct(StringComparer.Ordinal))
                counts[key] = counts.GetValueOrDefault(key) + 1;
        }

        int threshold = Math.Max(3, (int)Math.Ceiling(pages.Count * 0.3));
        for (int page = 0; page < pages.Count; page++)
        {
            string[] lines = pages[page];
            HashSet<int> drop = [.. EdgeIndexes(lines).Where(index => counts[LineKey(lines[index])] >= threshold || PageHeaderRegex().IsMatch(lines[index]))];
            if (drop.Count > 0)
                pages[page] = [.. lines.Where((_, index) => !drop.Contains(index))];
        }
    }

    private static IEnumerable<int> EdgeIndexes(string[] lines)
    {
        List<int> filled = [.. Enumerable.Range(0, lines.Length).Where(index => !string.IsNullOrWhiteSpace(lines[index]))];
        return filled.Take(EdgeLines).Concat(filled.Skip(Math.Max(EdgeLines, filled.Count - EdgeLines))).Distinct();
    }

    private static string LineKey(string line) => WhitespaceRegex().Replace(DigitsRegex().Replace(line.Trim(), "#"), " ").ToUpperInvariant();

    private static string EscapeLine(string line) => StructureRegex().IsMatch(line) ? string.Create(CultureInfo.InvariantCulture, $"\\{line}") : line;

    private static string Normalize(string text) => WhitespaceRegex().Replace(TextCleanup.Clean(text.Normalize(NormalizationForm.FormKC)), " ").Trim();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    // A running header of a chapter: no lowercase letter, at least three capitals and a page number at the start or at the end.
    [GeneratedRegex(@"^\s*(?=(?:[^A-Z]*[A-Z]){3})(?:\d+\s+[^a-z]+|[^a-z]+\s+\d+)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex PageHeaderRegex();

    [GeneratedRegex(@"\d+")]
    private static partial Regex DigitsRegex();

    // An ATX heading, a code fence or a setext underline.
    [GeneratedRegex(@"^ {0,3}(#{1,6}([ \t]|$)|```|~~~|=+[ \t]*$|-+[ \t]*$)")]
    private static partial Regex StructureRegex();

    private readonly record struct Mark(string Title, int Level);
}
