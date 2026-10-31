using System.Text;
using System.Text.RegularExpressions;

namespace ProjectRag.Core.Documents;

/// <summary>
/// Splits the structure of a document into chunks along its sections and blocks.
/// </summary>
/// <remarks>
/// <para>
/// A chunk never crosses a section. It collects whole blocks up to <see cref="ChunkingOptions.MaxChars"/>.
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
        foreach (IGrouping<int, DocumentBlock> section in structure.Blocks.GroupBy(static block => block.Section))
        {
            List<Piece> current = [];
            bool currentAtomic = false;
            foreach (List<DocumentBlock> unit in Units(structure.Content, [.. section]))
            {
                bool unitAtomic = unit.Any(static block => block.IsAtomic);
                int unitLength = unit[^1].End - unit[0].Start;
                int limit = currentAtomic || unitAtomic ? _atomicMaxChars : _maxChars;
                if (current.Count > 0 && unit[^1].End - current[0].Start <= limit)
                {
                    current.Add(new Piece(unit[0].Start, unit[^1].End));
                    currentAtomic |= unitAtomic;
                    continue;
                }

                writer.Add(current, section.Key);
                current = [];
                currentAtomic = false;
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

            writer.Add(current, section.Key);
        }

        return writer.Chunks;
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
            if (current is { } open && block.End - open.Start <= limit)
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

    // A text splits after a sentence. Without a sentence end in reach it splits at a space, and the next piece repeats the words before the cut.
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
            if (cut > pieceStart)
            {
                // The next piece starts with the last sentence of this piece when that sentence is short.
                int previousSentence = LastSentenceEnd(content, pieceStart, cut - 1, protectedRanges);
                next = previousSentence > pieceStart && cut - previousSentence <= _overlapChars ? SkipSpace(content, previousSentence) : SkipSpace(content, cut);
                next = Math.Min(next, end);
            }
            else
            {
                cut = LastSpace(content, pieceStart + (_maxChars / 2), limit, protectedRanges);
                if (cut <= pieceStart)
                    cut = char.IsLowSurrogate(content[limit]) ? limit - 1 : limit;

                int back = LastSpace(content, Math.Max(pieceStart + 1, cut - _overlapChars), cut - 1, protectedRanges);
                next = Math.Min(back > pieceStart ? SkipSpace(content, back) : cut, end);
            }

            pieces.Add(new Piece(pieceStart, cut));
            // The overlap must not keep the piece from moving on.
            pieceStart = Math.Max(next, pieceStart + 1);
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

        List<Piece> pieces = [];
        int index = body;
        int pieceStart = block.Start;
        int overhead = (prefix?.Length ?? 0) + (suffix?.Length ?? 0) + 1;
        while (index < lines.Count)
        {
            int startIndex = index;
            int length = pieces.Count == 0 ? lines[index].Start - block.Start : overhead;
            while (index < lines.Count && (index == startIndex || length + lines[index].End - lines[index].Start <= _atomicMaxChars))
            {
                length += lines[index].End - lines[index].Start;
                index++;
            }

            bool isLast = index >= lines.Count;
            pieces.Add(new Piece(pieceStart, lines[index - 1].End, pieces.Count == 0 ? null : prefix, isLast ? null : suffix));
            if (isLast)
                break;

            // The next piece repeats the last lines that fit into the overlap, at least the last line when it fits.
            int back = index;
            int overlap = 0;
            while (back - 1 > startIndex && overlap + lines[back - 1].End - lines[back - 1].Start <= _overlapChars)
            {
                back--;
                overlap += lines[back].End - lines[back].Start;
            }

            index = back;
            pieceStart = lines[index].Start;
        }

        return pieces;
    }

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

        return start;
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

    private static int LastSpace(string content, int start, int limit, List<(int Start, int End)> protectedRanges)
    {
        for (int i = Math.Min(limit, content.Length) - 1; i > start; i--)
        {
            if (char.IsWhiteSpace(content[i]) && !IsProtected(i, protectedRanges))
                return i;
        }

        return start;
    }

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
            foreach ((int hiddenStart, int hiddenEnd) in structure.Hidden)
            {
                if (hiddenEnd <= position || hiddenStart >= end)
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
