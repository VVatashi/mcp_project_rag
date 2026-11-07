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
/// A chunk starts at the first character of a line and ends after the last character of a line, without the line end.
/// The heading is the path of the file.
/// </para>
/// </remarks>
public sealed class LineCodeChunker : ICodeChunker
{
    /// <inheritdoc/>
    public int Version => 1;

    /// <inheritdoc/>
    public bool Supports(string language) => true;

    /// <inheritdoc/>
    public IReadOnlyList<CodeChunk> Split(string path, string content, CodeChunkingOptions options)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(options);
        int max = Math.Max(2, options.MaxChars);
        int overlap = Math.Max(0, options.OverlapLines);
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

            AddBlock(units, content, lines, index, last, max, overlap);
            index = last + 1;
        }

        return Pack(units, path, max);
    }

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
    private static void AddBlock(List<Unit> units, string content, List<Line> lines, int first, int last, int max, int overlap)
    {
        if (lines[last].End - lines[first].Start <= max)
        {
            units.Add(new Unit(lines[first].Start, lines[last].End, first + 1, last + 1, 0));
            return;
        }

        int from = first;
        int previousEnd = -1;
        while (from <= last)
        {
            if (lines[from].Length > max)
            {
                AddLongLine(units, content, lines[from], from + 1, max);
                previousEnd = -1;
                from++;
                continue;
            }

            int to = from;
            while (to < last && lines[to + 1].End - lines[from].Start <= max)
                to++;

            units.Add(new Unit(lines[from].Start, lines[to].End, from + 1, to + 1, previousEnd < 0 ? 0 : Math.Max(0, previousEnd - lines[from].Start)));
            if (to == last)
                return;

            // The overlap shrinks until the next line fits with it, so every part ends after the part before it.
            int next = Math.Max(from + 1, to - overlap + 1);
            while (next <= to && lines[to + 1].End - lines[next].Start > max)
                next++;

            previousEnd = next <= to ? lines[to].End : -1;
            from = next;
        }
    }

    private static void AddLongLine(List<Unit> units, string content, Line line, int number, int max)
    {
        int start = line.Start;
        while (start < line.End)
        {
            int end = Math.Min(start + max, line.End);
            if (end < line.End && char.IsHighSurrogate(content[end - 1]))
                end--;

            units.Add(new Unit(start, end, number, number, 0));
            start = end;
        }
    }

    private static List<CodeChunk> Pack(List<Unit> units, string path, int max)
    {
        List<CodeChunk> chunks = [];
        int index = 0;
        while (index < units.Count)
        {
            Unit first = units[index];
            Unit last = first;
            index++;
            while (index < units.Count && units[index].End - first.Start <= max)
            {
                last = units[index];
                index++;
            }

            chunks.Add(new CodeChunk(path, first.Start, last.End, first.LineStart, last.LineEnd, first.OverlapChars));
        }

        return chunks;
    }

    private readonly record struct Line(int Start, int End, bool IsBlank)
    {
        public int Length => End - Start;
    }

    private readonly record struct Unit(int Start, int End, int LineStart, int LineEnd, int OverlapChars);
}
