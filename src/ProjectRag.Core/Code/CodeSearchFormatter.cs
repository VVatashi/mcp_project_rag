using System.Globalization;
using System.Text;

using ProjectRag.Core.Code.Graph;
using ProjectRag.Core.Documents;

namespace ProjectRag.Core.Code;

/// <summary>
/// Writes the answer of the search in tiers as compact text for the MCP tool.
/// </summary>
public static class CodeSearchFormatter
{
    /// <summary>The most lines of a chunk that the text shows.</summary>
    public const int MaxBodyLines = 12;

    private const int MaxLineLength = 200;

    /// <summary>
    /// Writes the answer in three sections. A section without items stays out.
    /// </summary>
    /// <remarks>
    /// <c>Symbols:</c> has the lines of <see cref="CodeGraphFormatter.Symbols"/>. <c>Exact matches:</c> groups the lines by file like the tool code_grep.
    /// <c>Similar code:</c> gives <c>path:lineStart-lineEnd  heading  (score)</c> for each chunk, then its first lines as <c>  n| text</c>.
    /// The notes follow at the end.
    /// </remarks>
    /// <param name="result">The answer.</param>
    /// <param name="prefixSource">Tells that a path starts with the name of its source and a colon, for a search over all code sources.</param>
    /// <returns>The text.</returns>
    public static string Format(CodeSearchResult result, bool prefixSource)
    {
        ArgumentNullException.ThrowIfNull(result);
        StringBuilder text = new();
        if (result.Symbols.Count > 0)
        {
            text.AppendLine("Symbols:");
            text.Append(CodeGraphFormatter.Symbols(result.Symbols));
        }

        if (result.Matches.Count > 0)
        {
            Separate(text);
            text.AppendLine("Exact matches:");
            long documentId = -1;
            foreach (CodeMatch match in result.Matches)
            {
                if (match.DocumentId != documentId)
                {
                    documentId = match.DocumentId;
                    text.AppendLine(prefixSource ? $"{match.Source}:{match.Path}" : match.Path);
                }

                text.AppendLine(CultureInfo.InvariantCulture, $"  {match.Line}: {(match.TextStart > 0 ? "…" : string.Empty)}{match.Text}");
            }
        }

        if (result.Chunks.Count > 0)
        {
            Separate(text);
            text.AppendLine("Similar code:");
            foreach (CodeChunkHit hit in result.Chunks)
                AppendChunk(text, hit, prefixSource);
        }

        if (text.Length == 0)
            text.AppendLine("No results.");

        foreach (string note in result.Notes)
            text.AppendLine(CultureInfo.InvariantCulture, $"Note: {note}");

        return text.ToString();
    }

    private static void Separate(StringBuilder text)
    {
        if (text.Length > 0)
            text.AppendLine();
    }

    private static void AppendChunk(StringBuilder text, CodeChunkHit hit, bool prefixSource)
    {
        string path = prefixSource ? $"{hit.Source}:{hit.Path}" : hit.Path;
        string tail = HeadingTail(hit.Heading, hit.Path);
        text.AppendLine(CultureInfo.InvariantCulture, $"{path}:{hit.LineStart}-{hit.LineEnd}  {(tail.Length > 0 ? tail + "  " : string.Empty)}({hit.Score.ToString("0.####", CultureInfo.InvariantCulture)})");
        IReadOnlyList<string> lines = CodeText.SplitLines(hit.Text);
        for (int i = 0; i < Math.Min(lines.Count, MaxBodyLines); i++)
        {
            string line = lines[i].TrimEnd();
            if (line.Length > MaxLineLength)
                line = line[..MaxLineLength] + "…";

            text.AppendLine(CultureInfo.InvariantCulture, $"  {hit.LineStart + i}| {line}");
        }

        if (lines.Count > MaxBodyLines)
            text.AppendLine("  …");
    }

    // The heading starts with the path, which the first line shows already.
    private static string HeadingTail(string heading, string path)
    {
        string prefix = path + TextChunk.HeadingSeparator;
        if (heading.StartsWith(prefix, StringComparison.Ordinal))
            return heading[prefix.Length..];

        return heading == path ? string.Empty : heading;
    }
}
