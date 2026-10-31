using System.Text;
using System.Text.RegularExpressions;

namespace ProjectRag.Core.Documents;

/// <summary>
/// Splits the structure of a document into chunks along its sections and blocks.
/// </summary>
/// <remarks>
/// <para>
/// A chunk stays inside one section. It collects whole blocks up to <see cref="ChunkingOptions.MaxChars"/>.
/// A chunk shorter than <see cref="ChunkingOptions.MinChars"/> at the end of a section takes in the next section when that section lies inside the section of the chunk
/// or shares its parent. The heading of that section then stays in the text of the chunk.
/// A chunk with code, a formula or a table may grow to <see cref="ChunkingOptions.AtomicMaxChars"/>, so such a block stays whole.
/// A caption stays with its table or figure, and a short lead-in that ends with a colon stays with the block after it.
/// </para>
/// <para>
/// A paragraph over the limit splits between sentences, never inside a word, a code span, a formula or a link.
/// The next chunk then starts with the last sentence of the previous one. A block of code, a formula or a table over the larger limit splits between lines.
/// The next chunk then repeats the last lines, the header of the table and the fence of the code or the formula.
/// The overlap holds at most <see cref="ChunkingOptions.OverlapChars"/> characters.
/// </para>
/// </remarks>
public sealed partial class DocumentChunker
{
    private static readonly HashSet<string> Abbreviations = new(StringComparer.OrdinalIgnoreCase)
    {
        "e.g", "i.e", "etc", "vs", "cf", "fig", "figs", "eq", "eqs", "sec", "no", "approx", "resp", "al", "ch", "p", "pp",
        "т.е", "т.д", "т.п", "т.к", "см", "рис", "табл", "др", "ср", "гл", "стр",
    };

    private readonly int _maxChars;
    private readonly int _atomicMaxChars;
    private readonly int _overlapChars;
    private readonly int _minChars;

    /// <summary>
    /// Makes a chunker.
    /// </summary>
    /// <param name="options">The sizes of the chunks.</param>
    public DocumentChunker(ChunkingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxChars, 100);
        _maxChars = options.MaxChars;
        _atomicMaxChars = Math.Max(options.MaxChars, options.AtomicMaxChars);
        _overlapChars = Math.Clamp(options.OverlapChars, 0, options.MaxChars / 2);
        _minChars = Math.Clamp(options.MinChars, 0, options.MaxChars / 2);
    }

    /// <summary>
    /// Splits a document into chunks.
    /// </summary>
    /// <param name="structure">The structure of the document.</param>
    /// <returns>The chunks in the order of the text.</returns>
    public IReadOnlyList<TextChunk> Split(DocumentStructure structure)
    {
        ArgumentNullException.ThrowIfNull(structure);
        Writer writer = new(structure);
        List<Piece> current = [];
        bool currentAtomic = false;
        int currentSection = -1;
        foreach (IGrouping<int, DocumentBlock> section in structure.Blocks.GroupBy(static block => block.Section))
        {
            // A short chunk goes on into a nearby small section. Any other chunk ends with its section.
            // A large section starts a chunk of its own, so its heading names the chunk and the hits of the search.
            bool small = section.Last().End - section.First().Start < _minChars;
            if (current.Count == 0 || current[^1].End - current[0].Start >= _minChars || !small || !IsNear(structure.Sections, currentSection, section.Key))
            {
                writer.Add(current, currentSection);
                current = [];
                currentAtomic = false;
                currentSection = section.Key;
            }

            foreach (List<DocumentBlock> unit in Units(structure.Content, [.. section]))
            {
                bool unitAtomic = unit.Any(static block => block.IsAtomic);
                int unitLength = unit[^1].End - unit[0].Start;
                int limit = currentAtomic || unitAtomic ? _atomicMaxChars : _maxChars;
                // The repeated header of a table or fence of code counts toward the size of the chunk.
                if (current.Count > 0 && unit[^1].End - current[0].Start + (current[0].Prefix?.Length ?? 0) <= limit)
                {
                    current.Add(new Piece(unit[0].Start, unit[^1].End));
                    currentAtomic |= unitAtomic;
                    continue;
                }

                writer.Add(current, currentSection);
                current = [];
                currentAtomic = false;
                currentSection = section.Key;
                if (unitLength <= (unitAtomic ? _atomicMaxChars : _maxChars))
                {
                    current.Add(new Piece(unit[0].Start, unit[^1].End));
                    currentAtomic = unitAtomic;
                    continue;
                }

                // The unit alone is too large. Its pieces become chunks, and the last piece may collect the next units.
                List<Piece> pieces = SplitUnit(structure.Content, unit);
                foreach (Piece piece in pieces.Take(pieces.Count - 1))
                    writer.Add([piece], section.Key);

                current.Add(pieces[^1]);
                currentAtomic = unitAtomic;
            }
        }

        writer.Add(current, currentSection);
        return writer.Chunks;
    }

    // A section lies near the section of a chunk when it lies inside it, or when both are small sections of one parent.
    // Chapters and their sections of the first level stay apart.
    private static bool IsNear(IReadOnlyList<DocumentSection> sections, int chunkSection, int next)
    {
        if (chunkSection < 0 || next < 0 || next >= sections.Count || chunkSection >= sections.Count || sections[next].Level < 3)
            return false;

        if (sections[next].Parent == sections[chunkSection].Parent)
            return true;

        for (int parent = sections[next].Parent; parent >= 0; parent = sections[parent].Parent)
        {
            if (parent == chunkSection)
                return true;
        }

        return false;
    }

    // A unit is a group of blocks that must stay together: a caption with its table, a lead-in with its formula.
    private static List<List<DocumentBlock>> Units(string content, List<DocumentBlock> blocks)
    {
        List<List<DocumentBlock>> units = [];
        bool glueNext = false;
        foreach (DocumentBlock block in blocks)
        {
            string text = content[block.Start..block.End].Trim();
            bool isCaption = block.Kind == BlockKind.Paragraph && CaptionRegex().IsMatch(text);
            bool previousAtomic = units.Count > 0 && units[^1][^1].IsAtomic;
            if (units.Count > 0 && ((glueNext && (block.IsAtomic || isCaption)) || (isCaption && previousAtomic)))
                units[^1].Add(block);
            else
                units.Add([block]);

            // A lead-in such as "Let (i, j, k) be integers such that" and a caption before a table belong to the next block.
            glueNext = block.Kind == BlockKind.Paragraph && text.Length <= 600 && (text.EndsWith(':') || (isCaption && !previousAtomic));
        }

        return units;
    }

    private List<Piece> SplitUnit(string content, List<DocumentBlock> unit)
    {
        List<Piece> pieces = [];
        Piece? current = null;
        foreach (DocumentBlock block in unit)
        {
            int limit = block.IsAtomic ? _atomicMaxChars : _maxChars;
            if (current is { } open && block.End - open.Start + (open.Prefix?.Length ?? 0) <= limit)
            {
                current = open with { End = block.End };
                continue;
            }

            if (current is { } done)
                pieces.Add(done);

            List<Piece> parts = block.End - block.Start <= limit
                ? [new Piece(block.Start, block.End)]
                : block.IsAtomic ? SplitLines(content, block) : SplitText(content, block.Start, block.End);
            pieces.AddRange(parts.Take(parts.Count - 1));
            current = parts[^1];
        }

        if (current is { } last)
            pieces.Add(last);

        return pieces;
    }

    // A text splits after a sentence, and the next piece repeats that sentence when it is short.
    // Without a sentence end in reach the text splits at its last space, and the next piece repeats the words before the cut.
    // Only a text without spaces, such as Chinese, splits at the limit, and the cut keeps a surrogate pair whole.
    private List<Piece> SplitText(string content, int start, int end)
    {
        List<(int Start, int End)> protectedRanges = ProtectedRanges(content, start, end);
        List<Piece> pieces = [];
        int pieceStart = start;
        while (end - pieceStart > _maxChars)
        {
            int limit = pieceStart + _maxChars;
            int cut = LastSentenceEnd(content, pieceStart, limit, protectedRanges);
            int next;
            if (cut >= 0)
            {
                int previousSentence = LastSentenceEnd(content, pieceStart, cut - 1, protectedRanges);
                next = previousSentence >= 0 && cut - previousSentence <= _overlapChars ? SkipSpace(content, previousSentence) : SkipSpace(content, cut);
            }
            else
            {
                cut = LastSpace(content, pieceStart, limit, protectedRanges);
                if (cut < 0)
                    cut = SafeCut(content, limit);

                int back = LastSpace(content, Math.Max(pieceStart, cut - _overlapChars), cut - 1, protectedRanges);
                next = back >= 0 ? SkipSpace(content, back) : cut;
            }

            pieces.Add(new Piece(pieceStart, cut));
            // The overlap must not keep the piece from moving on.
            pieceStart = Math.Min(Math.Max(next, pieceStart + 1), end);
        }

        if (pieceStart < end)
            pieces.Add(new Piece(pieceStart, end));

        return pieces;
    }

    // Code, a formula or a table splits between lines. Each later piece repeats the header of a table or the fence, and the last lines before the cut.
    private List<Piece> SplitLines(string content, DocumentBlock block)
    {
        List<(int Start, int End)> lines = [];
        int position = block.Start;
        while (position < block.End)
        {
            int newline = content.IndexOf('\n', position, block.End - position);
            int lineEnd = newline < 0 ? block.End : newline + 1;
            lines.Add((position, lineEnd));
            position = lineEnd;
        }

        string first = content[lines[0].Start..lines[0].End];
        string? prefix = null;
        string? suffix = null;
        int body = 0;
        if (block.Kind == BlockKind.Table && lines.Count > 2 && TableRuleRegex().IsMatch(content[lines[1].Start..lines[1].End]))
        {
            prefix = content[lines[0].Start..lines[1].End];
            body = 2;
        }
        else if (block.Kind is BlockKind.Code or BlockKind.Math && FenceRegex().Match(first) is { Success: true } fence)
        {
            prefix = first;
            suffix = fence.Groups["fence"].Value;
            body = 1;
        }

        int Length(int line) => lines[line].End - lines[line].Start;

        List<Piece> pieces = [];
        int overhead = (prefix?.Length ?? 0) + (suffix?.Length ?? 0) + 1;
        int next = body;
        int overlapFrom = body;
        while (next < lines.Count)
        {
            // A line longer than the limit splits like text.
            if (Length(next) + overhead > _atomicMaxChars)
            {
                List<Piece> parts = SplitText(content, lines[next].Start, lines[next].End);
                for (int k = 0; k < parts.Count; k++)
                {
                    bool isLastPart = k == parts.Count - 1 && next + 1 >= lines.Count;
                    pieces.Add(new Piece(pieces.Count == 0 ? block.Start : parts[k].Start, parts[k].End, pieces.Count == 0 ? null : prefix, isLastPart ? null : suffix));
                }

                next++;
                overlapFrom = next;
                continue;
            }

            // A piece holds the overlap and at least one new line.
            int length = pieces.Count == 0 ? lines[next].Start - block.Start : overhead + lines[next].Start - lines[overlapFrom].Start;
            int end = next;
            do
            {
                length += Length(end);
                end++;
            }
            while (end < lines.Count && length + Length(end) <= _atomicMaxChars);

            bool isLast = end >= lines.Count;
            pieces.Add(new Piece(pieces.Count == 0 ? block.Start : lines[overlapFrom].Start, lines[end - 1].End, pieces.Count == 0 ? null : prefix, isLast ? null : suffix));
            if (isLast)
                break;

            // The next piece repeats the last lines that fit into the overlap and still leave room for its first new line.
            int back = end;
            int overlap = 0;
            while (back - 1 > next && overlap + Length(back - 1) <= _overlapChars && overhead + overlap + Length(back - 1) + Length(end) <= _atomicMaxChars)
            {
                back--;
                overlap += Length(back);
            }

            overlapFrom = back;
            next = end;
        }

        // A closing fence alone would make a chunk without content, so the piece before it takes the fence.
        if (pieces.Count > 1 && suffix is not null && pieces[^1].Start == lines[^1].Start && FenceRegex().IsMatch(content[lines[^1].Start..lines[^1].End]))
        {
            pieces[^2] = pieces[^2] with { End = pieces[^1].End, Suffix = null };
            pieces.RemoveAt(pieces.Count - 1);
        }

        return pieces;
    }

    // Returns the index of the space after the last end of a sentence in the range, or -1 when there is none.
    private static int LastSentenceEnd(string content, int start, int limit, List<(int Start, int End)> protectedRanges)
    {
        for (int i = Math.Min(limit, content.Length) - 1; i > start; i--)
        {
            if (!char.IsWhiteSpace(content[i]) || IsProtected(i, protectedRanges))
                continue;

            int end = i;
            while (end > start && content[end - 1] is '"' or '\'' or ')' or ']' or '”' or '»' or '*' or '_')
                end--;

            if (end <= start || content[end - 1] is not ('.' or '!' or '?' or '…' or ';'))
                continue;

            int next = SkipSpace(content, i);
            if (next >= content.Length || !char.IsUpper(content[next]) && !char.IsDigit(content[next]) && content[next] is not ('`' or '$' or '(' or '[' or '"' or '“' or '«' or '*' or '-'))
                continue;

            if (content[end - 1] == '.' && IsAbbreviation(content, start, end - 1))
                continue;

            return i;
        }

        return -1;
    }

    private static bool IsAbbreviation(string content, int start, int dot)
    {
        int wordStart = dot;
        while (wordStart > start && !char.IsWhiteSpace(content[wordStart - 1]) && content[wordStart - 1] is not ('(' or '['))
            wordStart--;

        string word = content[wordStart..dot];
        // A single letter is an initial, and a number with a dot is a part of a list or a section number.
        return word.Length <= 1 || Abbreviations.Contains(word) || word.All(static c => char.IsDigit(c) || c == '.');
    }

    // Returns the index of the last space in the range outside the protected ranges, or -1 when there is none.
    private static int LastSpace(string content, int start, int limit, List<(int Start, int End)> protectedRanges)
    {
        for (int i = Math.Min(limit, content.Length) - 1; i > start; i--)
        {
            if (char.IsWhiteSpace(content[i]) && !IsProtected(i, protectedRanges))
                return i;
        }

        return -1;
    }

    private static int SafeCut(string content, int index) => index > 0 && index < content.Length && char.IsLowSurrogate(content[index]) ? index - 1 : index;

    private static int SkipSpace(string content, int index)
    {
        while (index < content.Length && char.IsWhiteSpace(content[index]))
            index++;

        return index;
    }

    private static bool IsProtected(int index, List<(int Start, int End)> ranges) => ranges.Any(range => index >= range.Start && index < range.End);

    // Code spans, inline formulas, links and service marks hold spaces that are no place for a cut.
    private static List<(int Start, int End)> ProtectedRanges(string content, int start, int end)
    {
        string text = content[start..end];
        return [.. InlineRegex().Matches(text).Select(match => (start + match.Index, start + match.Index + match.Length))];
    }

    [GeneratedRegex(@"^(?:Table|Figure|Listing|Algorithm|Equation|Таблица|Рисунок|Листинг|Алгоритм)\s+[\w.\-]+\s*[:.]", RegexOptions.CultureInvariant)]
    private static partial Regex CaptionRegex();

    [GeneratedRegex(@"^\s*\|?\s*:?-{3,}")]
    private static partial Regex TableRuleRegex();

    [GeneratedRegex(@"^\s{0,3}(?<fence>`{3,}|~{3,}|\$\$)")]
    private static partial Regex FenceRegex();

    [GeneratedRegex(@"`[^`\n]+`|\$\$[^$]+\$\$|\$[^$\n]+\$|\[[^\]\n]*\]\([^)\n]*\)|<!--[^>]*-->")]
    private static partial Regex InlineRegex();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex BlankLinesRegex();

    private readonly record struct Piece(int Start, int End, string? Prefix = null, string? Suffix = null);

    // Turns pieces into chunks: the visible text, the pages and the overlap with the previous chunk.
    private sealed class Writer(DocumentStructure structure)
    {
        private int _previousEnd = -1;

        public List<TextChunk> Chunks { get; } = [];

        public void Add(List<Piece> pieces, int section)
        {
            if (pieces.Count == 0)
                return;

            int start = pieces[0].Start;
            int end = pieces[^1].End;
            StringBuilder body = new();
            if (pieces[0].Prefix is { } prefix)
                body.Append(prefix.TrimEnd('\n')).Append('\n');

            body.Append(Visible(start, end));
            if (pieces[^1].Suffix is { } suffix)
                body.Append('\n').Append(suffix);

            string text = BlankLinesRegex().Replace(body.ToString(), "\n\n").Trim();
            if (text.Length == 0)
                return;

            int overlap = _previousEnd > start ? _previousEnd - start : 0;
            _previousEnd = Math.Max(_previousEnd, end);
            Chunks.Add(new TextChunk(Chunks.Count, structure.Sections[section].Path, text, start, end, section, structure.PageAt(start), structure.PageAt(Math.Max(start, end - 1)), overlap));
        }

        private string Visible(int start, int end)
        {
            StringBuilder builder = new(end - start);
            int position = start;
            // The hidden ranges come sorted, so the search starts at the first range that ends after the start.
            IReadOnlyList<(int Start, int End)> hidden = structure.Hidden;
            int low = 0;
            int high = hidden.Count;
            while (low < high)
            {
                int middle = (low + high) / 2;
                if (hidden[middle].End <= start)
                    low = middle + 1;
                else
                    high = middle;
            }

            for (int i = low; i < hidden.Count && hidden[i].Start < end; i++)
            {
                (int hiddenStart, int hiddenEnd) = hidden[i];
                if (hiddenEnd <= position)
                    continue;

                builder.Append(structure.Content, position, Math.Max(0, hiddenStart - position));
                position = Math.Max(position, hiddenEnd);
            }

            if (position < end)
                builder.Append(structure.Content, position, end - position);

            return builder.ToString();
        }
    }
}
