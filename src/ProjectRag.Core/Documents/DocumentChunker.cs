using System.Text;
using System.Text.RegularExpressions;

namespace ProjectRag.Core.Documents;

/// <summary>
/// Splits the structure of a document into chunks along its sections and blocks.
/// </summary>
/// <remarks>
/// <para>
/// A chunk stays inside one section. It collects whole blocks up to <see cref="ChunkingOptions.MaxChars"/>.
/// A chunk shorter than <see cref="ChunkingOptions.MinChars"/> at the end of a section takes in the next small section when that section lies inside the section of the chunk
/// or shares its parent. The heading of that section then stays in the text of the chunk.
/// A section whose subsections fit with it under <see cref="ChunkingOptions.MaxChars"/> becomes one chunk, such as the short reference page of one function.
/// Both rules need <see cref="ChunkingOptions.MinChars"/> above 0.
/// A chunk with code, a formula or a table may grow to <see cref="ChunkingOptions.AtomicMaxChars"/>, so such a block stays whole.
/// A digit in such a chunk counts as three characters.
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

    // The size of a digit of code, a formula or a table in characters.
    internal const int DigitWeight = 3;

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
        // The end of a section that fits whole into one chunk. Its subsections join the chunk of the section.
        int wholeEnd = -1;
        foreach (IGrouping<int, DocumentBlock> section in structure.Blocks.GroupBy(static block => block.Section))
        {
            // A short chunk goes on into a nearby small section. Any other chunk ends with its section.
            // A large section starts a chunk of its own, so its heading names the chunk and the hits of the search.
            bool inWhole = section.First().Start < wholeEnd;
            bool small = section.Last().End - section.First().Start < _minChars;
            if (!inWhole && (current.Count == 0 || current[^1].End - current[0].Start >= _minChars || !small || !IsNear(structure.Sections, currentSection, section.Key)))
            {
                writer.Add(current, currentSection);
                current = [];
                currentAtomic = false;
                currentSection = section.Key;
                wholeEnd = -1;
                if (_minChars > 0 && Whole(structure, section.Key, section.First()) is int whole)
                {
                    currentSection = whole;
                    wholeEnd = structure.Sections[whole].End;
                }
            }

            foreach (List<DocumentBlock> unit in Units(structure.Content, [.. section]))
            {
                bool unitAtomic = unit.Any(static block => block.IsAtomic);
                int unitSize = Size(structure.Content, unit[0].Start, unit[^1].End, unitAtomic);
                bool atomic = currentAtomic || unitAtomic;
                int limit = atomic ? _atomicMaxChars : _maxChars;
                // The repeated header of a table or the fence of code or a formula counts toward the size of the chunk.
                if (current.Count > 0 && Size(structure.Content, current[0].Start, unit[^1].End, atomic) + PrefixSize(current[0].Prefix) <= limit)
                {
                    current.Add(new Piece(unit[0].Start, unit[^1].End));
                    currentAtomic |= unitAtomic;
                    continue;
                }

                // A new chunk keeps the section that the start of the chunk chose, such as a whole section above this one.
                if (current.Count > 0)
                {
                    writer.Add(current, currentSection);
                    current = [];
                    currentAtomic = false;
                    currentSection = section.Key;
                }

                if (unitSize <= (unitAtomic ? _atomicMaxChars : _maxChars))
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

    // The largest section that starts with this block and fits into one chunk. A heading right above a subheading has no blocks of its own,
    // so the search goes up through such headings: "# Title" right above "## A" and "## B" makes one chunk under "Title".
    // The section 0 holds the text before the first heading, so it is no parent of a heading here.
    private int? Whole(DocumentStructure structure, int index, DocumentBlock first)
    {
        int? whole = Fits(structure, index) ? index : null;
        for (int parent = structure.Sections[index].Parent; parent > 0; parent = structure.Sections[parent].Parent)
        {
            if (FirstBlockAt(structure.Blocks, structure.Sections[parent].Start) != FirstBlockAt(structure.Blocks, first.Start))
                break;

            if (Fits(structure, parent))
                whole = parent;
        }

        return whole;
    }

    // A section with subsections fits into one chunk when all its blocks together stay under the limit.
    // A short reference page, such as the page of one function, then becomes one chunk instead of a chunk for each part.
    private bool Fits(DocumentStructure structure, int index)
    {
        if (index < 0 || index >= structure.Sections.Count)
            return false;

        DocumentSection section = structure.Sections[index];
        // The size of a chunk is never below the count of its characters, so a longer section cannot fit.
        // The sections go in the order of the text, so the first subsection follows its section.
        bool parent = index + 1 < structure.Sections.Count && structure.Sections[index + 1].Parent == index;
        if (section.End - section.Start > _maxChars || !parent)
            return false;

        int first = FirstBlockAt(structure.Blocks, section.Start);
        int last = first;
        bool atomic = false;
        while (last < structure.Blocks.Count && structure.Blocks[last].End <= section.End)
            atomic |= structure.Blocks[last++].IsAtomic;

        return last > first && Size(structure.Content, structure.Blocks[first].Start, structure.Blocks[last - 1].End, atomic) <= _maxChars;
    }

    private static int FirstBlockAt(IReadOnlyList<DocumentBlock> blocks, int start)
    {
        int low = 0;
        int high = blocks.Count;
        while (low < high)
        {
            int middle = (low + high) / 2;
            if (blocks[middle].Start < start)
                low = middle + 1;
            else
                high = middle;
        }

        return low;
    }

    // A section lies near the section of a chunk when it lies inside it, or when both are small sections of one parent.
    // Chapters and their sections of the first level stay apart. A short text under a title, such as the first line of a reference page, joins its first subsection.
    private static bool IsNear(IReadOnlyList<DocumentSection> sections, int chunkSection, int next)
    {
        if (chunkSection < 0 || next < 0 || next >= sections.Count || chunkSection >= sections.Count || (sections[next].Level < 3 && sections[next].Parent != chunkSection))
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
        bool currentAtomic = false;
        foreach (DocumentBlock block in unit)
        {
            bool atomic = currentAtomic || block.IsAtomic;
            if (current is { } open && Size(content, open.Start, block.End, atomic) + PrefixSize(open.Prefix) <= (atomic ? _atomicMaxChars : _maxChars))
            {
                current = open with { End = block.End };
                currentAtomic = atomic;
                continue;
            }

            // A short lead-in, such as "Metal 4 defines the following structures and enumerations:", starts the first piece of the long code after it.
            int? lead = current is { Prefix: null } before && !currentAtomic && block.IsAtomic && before.End - before.Start <= _maxChars / 4 ? before.Start : null;
            List<Piece> parts = Size(content, block.Start, block.End, block.IsAtomic) <= (block.IsAtomic ? _atomicMaxChars : _maxChars)
                ? [new Piece(block.Start, block.End)]
                : block.IsAtomic ? SplitLines(content, block, lead ?? block.Start) : SplitText(content, block.Start, block.End, _maxChars);
            if (current is { } done && parts[0].Start != lead)
                pieces.Add(done);

            pieces.AddRange(parts.Take(parts.Count - 1));
            current = parts[^1];
            currentAtomic = block.IsAtomic;
        }

        if (current is { } last)
            pieces.Add(last);

        return pieces;
    }

    // A text splits after a sentence, and the next piece repeats that sentence when it is short.
    // Without a sentence end in reach the text splits at its last space, and the next piece repeats the words before the cut.
    // Only a text without spaces, such as Chinese, splits at the limit, and the cut keeps a surrogate pair whole.
    private List<Piece> SplitText(string content, int start, int end, int maxChars)
    {
        List<(int Start, int End)> protectedRanges = ProtectedRanges(content, start, end);
        int overlapChars = Math.Min(_overlapChars, maxChars / 2);
        List<Piece> pieces = [];
        int pieceStart = start;
        while (end - pieceStart > maxChars)
        {
            int limit = pieceStart + maxChars;
            int cut = LastSentenceEnd(content, pieceStart, limit, protectedRanges);
            int next;
            if (cut >= 0)
            {
                int previousSentence = LastSentenceEnd(content, pieceStart, cut - 1, protectedRanges);
                next = previousSentence >= 0 && cut - previousSentence <= overlapChars ? SkipSpace(content, previousSentence) : SkipSpace(content, cut);
            }
            else
            {
                cut = LastSpace(content, pieceStart, limit, protectedRanges);
                if (cut < 0)
                    cut = SafeCut(content, limit);

                int back = LastSpace(content, Math.Max(pieceStart, cut - overlapChars), cut - 1, protectedRanges);
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
    // The first piece starts at start, before the block when a lead-in goes with it. The lead-in stays out when it leaves no room for the first line.
    private List<Piece> SplitLines(string content, DocumentBlock block, int start)
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

        int LineSize(int line) => Size(content, lines[line].Start, lines[line].End, atomic: true);

        if (start < block.Start && Size(content, start, lines[Math.Min(body, lines.Count - 1)].End, atomic: true) + (suffix?.Length ?? 0) + 1 > _atomicMaxChars)
            start = block.Start;

        List<Piece> pieces = [];
        int overhead = PrefixSize(prefix) + (suffix?.Length ?? 0) + 1;
        int next = body;
        int overlapFrom = body;
        while (next < lines.Count)
        {
            // A line longer than the limit splits like text. A part of the line may hold only digits, so its size is a third of the free space.
            if (LineSize(next) + overhead > _atomicMaxChars)
            {
                int partMax = Math.Clamp((_atomicMaxChars - overhead) / DigitWeight, 100, _maxChars);
                List<Piece> parts = SplitText(content, lines[next].Start, lines[next].End, partMax);
                for (int k = 0; k < parts.Count; k++)
                {
                    bool isLastPart = k == parts.Count - 1 && next + 1 >= lines.Count;
                    pieces.Add(new Piece(pieces.Count == 0 ? start : parts[k].Start, parts[k].End, pieces.Count == 0 ? null : prefix, isLastPart ? null : suffix));
                }

                next++;
                overlapFrom = next;
                continue;
            }

            // A piece holds the overlap and at least one new line. The first piece counts the fence that closes it.
            int length = pieces.Count == 0 ? Size(content, start, lines[next].Start, atomic: true) + (suffix?.Length ?? 0) + 1 : overhead + Size(content, lines[overlapFrom].Start, lines[next].Start, atomic: true);
            int end = next;
            do
            {
                length += LineSize(end);
                end++;
            }
            while (end < lines.Count && length + LineSize(end) <= _atomicMaxChars);

            bool isLast = end >= lines.Count;
            pieces.Add(new Piece(pieces.Count == 0 ? start : lines[overlapFrom].Start, lines[end - 1].End, pieces.Count == 0 ? null : prefix, isLast ? null : suffix));
            if (isLast)
                break;

            // The next piece repeats the last lines that fit into the overlap and still leave room for its first new line.
            int back = end;
            int overlap = 0;
            while (back - 1 > next && overlap + LineSize(back - 1) <= _overlapChars && overhead + overlap + LineSize(back - 1) + LineSize(end) <= _atomicMaxChars)
            {
                back--;
                overlap += LineSize(back);
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

    // Returns the size of a range for the limits. A digit of code, a formula or a table counts as three characters.
    // A tokenizer such as that of Qwen3 gives each digit a token of its own, and also each space or comma between digits.
    // A table of single digits then holds one token per character.
    private static int Size(string content, int start, int end, bool atomic)
    {
        int size = end - start;
        if (atomic)
        {
            for (int i = start; i < end; i++)
            {
                if (char.IsAsciiDigit(content[i]))
                    size += DigitWeight - 1;
            }
        }

        return size;
    }

    // The repeated header of a table or the fence of code or a formula belongs to such a block, so its digits count the same way.
    private static int PrefixSize(string? prefix) => prefix is null ? 0 : Size(prefix, 0, prefix.Length, atomic: true);

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
