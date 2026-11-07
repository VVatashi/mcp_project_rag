using ProjectRag.Core.Documents;

namespace ProjectRag.Core.Code.Chunks;

/// <summary>
/// Splits the text of any file by its lines: the fallback chunker for the languages without a chunker of their own.
/// </summary>
/// <remarks>
/// <para>
/// The text splits at blank lines into blocks. The chunker packs blocks into a chunk while the chunk stays within <see cref="CodeChunkingOptions.MaxChars"/>.
/// A block over the limit splits by lines, and each part repeats <see cref="CodeChunkingOptions.OverlapLines"/> lines of the part before it.
/// A line over the limit splits by characters, but never inside a surrogate pair.
/// </para>
/// <para>
/// The size of a chunk counts each digit as <see cref="DocumentChunker.DigitWeight"/> characters, like the chunker of documents.
/// A tokenizer gives each digit a token of its own, so a text of numbers has about one token per character and would not fit the slot of the embedding server.
/// </para>
/// <para>
/// A chunk starts at the first character of a line and ends after the last character of a line, without the line end.
/// The heading is the path of the file.
/// </para>
/// </remarks>
public sealed class LineCodeChunker : ICodeChunker
{
    /// <inheritdoc/>
    public int Version => 2;

    /// <inheritdoc/>
    public bool Supports(string language) => true;

    /// <inheritdoc/>
    public IReadOnlyList<CodeChunk> Split(string path, string content, CodeChunkingOptions options)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(options);
        int max = Math.Max(DocumentChunker.DigitWeight, options.MaxChars);
        int overlap = Math.Max(0, options.OverlapLines);
        int[] weights = Weigh(content);
        List<Line> lines = ReadLines(content);
        List<Unit> units = [];
        int index = 0;
        while (index < lines.Count)
        {
            if (lines[index].IsBlank)
            {
                index++;
                continue;
            }

            int last = index;
            while (last + 1 < lines.Count && !lines[last + 1].IsBlank)
                last++;

            AddBlock(units, content, weights, lines, index, last, max, overlap);
            index = last + 1;
        }

        return Pack(units, weights, path, max);
    }

    // The sum of the weights of the first i characters, so the size of a range is a difference of two entries.
    private static int[] Weigh(string content)
    {
        int[] weights = new int[content.Length + 1];
        for (int i = 0; i < content.Length; i++)
            weights[i + 1] = weights[i] + (char.IsAsciiDigit(content[i]) ? DocumentChunker.DigitWeight : 1);

        return weights;
    }

    private static int Size(int[] weights, int start, int end) => weights[end] - weights[start];

    private static List<Line> ReadLines(string content)
    {
        List<Line> lines = [];
        int start = 0;
        while (start < content.Length)
        {
            int end = content.IndexOf('\n', start);
            int lineEnd = end < 0 ? content.Length : end;
            lines.Add(new Line(start, lineEnd, content.AsSpan(start, lineEnd - start).Trim().IsEmpty));
            start = lineEnd + 1;
        }

        return lines;
    }

    // A block that fits is one unit. A longer block splits into parts by lines, and a longer line splits by characters.
    private static void AddBlock(List<Unit> units, string content, int[] weights, List<Line> lines, int first, int last, int max, int overlap)
    {
        if (Size(weights, lines[first].Start, lines[last].End) <= max)
        {
            units.Add(new Unit(lines[first].Start, lines[last].End, first + 1, last + 1, 0));
            return;
        }

        int from = first;
        int previousEnd = -1;
        while (from <= last)
        {
            if (Size(weights, lines[from].Start, lines[from].End) > max)
            {
                AddLongLine(units, content, weights, lines[from], from + 1, max);
                previousEnd = -1;
                from++;
                continue;
            }

            int to = from;
            while (to < last && Size(weights, lines[from].Start, lines[to + 1].End) <= max)
                to++;

            units.Add(new Unit(lines[from].Start, lines[to].End, from + 1, to + 1, previousEnd < 0 ? 0 : Math.Max(0, previousEnd - lines[from].Start)));
            if (to == last)
                return;

            // The overlap shrinks until the next line fits with it, so every part ends after the part before it.
            int next = Math.Max(from + 1, to - overlap + 1);
            while (next <= to && Size(weights, lines[next].Start, lines[to + 1].End) > max)
                next++;

            previousEnd = next <= to ? lines[to].End : -1;
            from = next;
        }
    }

    private static void AddLongLine(List<Unit> units, string content, int[] weights, Line line, int number, int max)
    {
        int start = line.Start;
        while (start < line.End)
        {
            int end = start;
            while (end < line.End && Size(weights, start, end + 1) <= max)
                end++;

            if (end < line.End && end - 1 > start && char.IsHighSurrogate(content[end - 1]))
                end--;

            units.Add(new Unit(start, end, number, number, 0));
            start = end;
        }
    }

    private static List<CodeChunk> Pack(List<Unit> units, int[] weights, string path, int max)
    {
        List<CodeChunk> chunks = [];
        int index = 0;
        while (index < units.Count)
        {
            Unit first = units[index];
            Unit last = first;
            index++;
            while (index < units.Count && Size(weights, first.Start, units[index].End) <= max)
            {
                last = units[index];
                index++;
            }

            chunks.Add(new CodeChunk(path, first.Start, last.End, first.LineStart, last.LineEnd, first.OverlapChars));
        }

        return chunks;
    }

    private readonly record struct Line(int Start, int End, bool IsBlank);

    private readonly record struct Unit(int Start, int End, int LineStart, int LineEnd, int OverlapChars);
}
