namespace ProjectRag.Core.Documents;

/// <summary>
/// Splits a Markdown or plain text document into chunks along its sections.
/// </summary>
/// <remarks>
/// <para>
/// A heading starts a section. The chunker knows the ATX headings (<c># Title</c>) and the setext headings under a line of one paragraph line.
/// A fenced code block is one block, so a heading inside it starts no section.
/// </para>
/// <para>
/// A chunk never crosses a section. It collects whole blocks (paragraphs, lists, tables, code blocks) up to the size limit.
/// A larger block splits at line ends, and a longer line splits at a space. Each chunk keeps the path of its section,
/// because a part of a section without its headings often loses its meaning for the search.
/// </para>
/// </remarks>
public sealed class MarkdownChunker
{
    /// <summary>The separator of the headings in <see cref="TextChunk.Heading"/>.</summary>
    public const string HeadingSeparator = " > ";

    private readonly int _maxChars;

    /// <summary>
    /// Makes a chunker.
    /// </summary>
    /// <param name="maxChars">The most characters in the body of a chunk.</param>
    public MarkdownChunker(int maxChars)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxChars, 100);
        _maxChars = maxChars;
    }

    /// <summary>
    /// Finds the title of a Markdown text: the field <c>title</c> of the front matter, or else the first heading of level 1.
    /// </summary>
    /// <param name="content">The text with LF line ends.</param>
    /// <returns>The title, or <see langword="null"/> when the text has none.</returns>
    public static string? FindTitle(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        int position = SkipFrontMatter(content, out string? title);
        if (title is not null)
            return title;

        bool isFence = false;
        char fenceChar = '\0';
        int fenceLength = 0;
        foreach (Range line in Lines(content, position))
        {
            ReadOnlySpan<char> text = content.AsSpan(line);
            if (isFence)
            {
                isFence = !IsFenceClose(text, fenceChar, fenceLength);
                continue;
            }

            if (TryFenceOpen(text, out fenceChar, out fenceLength))
                isFence = true;
            else if (TryAtxHeading(text, out int level, out string heading) && level == 1 && heading.Length > 0)
                return heading;
        }

        return null;
    }

    /// <summary>
    /// Splits a document into chunks.
    /// </summary>
    /// <param name="document">The document. Its title starts the heading of every chunk.</param>
    /// <returns>The chunks in the order of the text. An empty document gives no chunks.</returns>
    public IReadOnlyList<TextChunk> Split(DocumentText document)
    {
        ArgumentNullException.ThrowIfNull(document);
        string content = document.Content;
        List<Section> sections = [];
        List<(int Level, string Text)> headings = [];
        Section section = new(document.Title);
        sections.Add(section);

        int paragraphStart = -1;
        int paragraphEnd = -1;
        int paragraphLines = 0;
        int fenceStart = -1;
        char fenceChar = '\0';
        int fenceLength = 0;

        void EndParagraph()
        {
            if (paragraphStart >= 0)
                section.Blocks.Add(new Block(paragraphStart, paragraphEnd));

            paragraphStart = -1;
            paragraphLines = 0;
        }

        void StartSection(int level, string text)
        {
            headings.RemoveAll(heading => heading.Level >= level);
            headings.Add((level, text));
            section = new Section(HeadingPath(document.Title, headings));
            sections.Add(section);
        }

        foreach (Range line in Lines(content, SkipFrontMatter(content, out _)))
        {
            (int start, int length) = line.GetOffsetAndLength(content.Length);
            int end = start + length;
            ReadOnlySpan<char> text = content.AsSpan(start, length);
            if (fenceStart >= 0)
            {
                if (IsFenceClose(text, fenceChar, fenceLength))
                {
                    section.Blocks.Add(new Block(fenceStart, end));
                    fenceStart = -1;
                }

                continue;
            }

            if (TryFenceOpen(text, out fenceChar, out fenceLength))
            {
                EndParagraph();
                fenceStart = start;
            }
            else if (TryAtxHeading(text, out int level, out string heading))
            {
                EndParagraph();
                StartSection(level, heading);
            }
            else if (paragraphLines == 1 && TrySetextUnderline(text, out level))
            {
                string title = content[paragraphStart..paragraphEnd].Trim();
                paragraphStart = -1;
                paragraphLines = 0;
                StartSection(level, title);
            }
            else if (text.IsWhiteSpace())
            {
                EndParagraph();
            }
            else
            {
                if (paragraphStart < 0)
                    paragraphStart = start;

                paragraphEnd = end;
                paragraphLines++;
            }
        }

        // A code block without its closing fence runs to the end of the document.
        if (fenceStart >= 0)
            section.Blocks.Add(new Block(fenceStart, content.Length));

        EndParagraph();

        List<TextChunk> chunks = [];
        foreach (Section part in sections)
            AddChunks(content, part, chunks);

        return chunks;
    }

    private void AddChunks(string content, Section section, List<TextChunk> chunks)
    {
        int chunkStart = -1;
        int chunkEnd = -1;
        foreach (Block block in section.Blocks)
        {
            foreach (Block piece in SplitBlock(content, block))
            {
                if (chunkStart >= 0 && piece.End - chunkStart > _maxChars)
                {
                    AddChunk(content, section.Heading, chunkStart, chunkEnd, chunks);
                    chunkStart = -1;
                }

                if (chunkStart < 0)
                    chunkStart = piece.Start;

                chunkEnd = piece.End;
            }
        }

        if (chunkStart >= 0)
            AddChunk(content, section.Heading, chunkStart, chunkEnd, chunks);
    }

    private static void AddChunk(string content, string heading, int start, int end, List<TextChunk> chunks)
    {
        while (end > start && char.IsWhiteSpace(content[end - 1]))
            end--;

        while (start < end && content[start] == '\n')
            start++;

        if (end > start)
            chunks.Add(new TextChunk(chunks.Count, heading, content[start..end], start, end));
    }

    // A block over the limit splits at line ends. A line over the limit splits at the last space before the limit, or at the limit itself.
    private IEnumerable<Block> SplitBlock(string content, Block block)
    {
        if (block.End - block.Start <= _maxChars)
        {
            yield return block;
            yield break;
        }

        int pieceStart = block.Start;
        int pieceEnd = block.Start;
        foreach (Range line in Lines(content, block.Start, block.End))
        {
            (int start, int length) = line.GetOffsetAndLength(content.Length);
            int end = start + length;
            if (end - pieceStart <= _maxChars)
            {
                pieceEnd = end;
                continue;
            }

            if (pieceEnd > pieceStart)
                yield return new Block(pieceStart, pieceEnd);

            pieceStart = start;
            while (end - pieceStart > _maxChars)
            {
                int cut = FindCut(content, pieceStart, pieceStart + _maxChars);
                yield return new Block(pieceStart, cut);
                pieceStart = cut;
            }

            pieceEnd = end;
        }

        if (pieceEnd > pieceStart)
            yield return new Block(pieceStart, pieceEnd);
    }

    private static int FindCut(string content, int start, int limit)
    {
        for (int i = limit; i > start + ((limit - start) / 2); i--)
        {
            if (char.IsWhiteSpace(content[i - 1]))
                return i;
        }

        // A hard cut keeps a surrogate pair whole.
        return char.IsLowSurrogate(content[limit]) ? limit - 1 : limit;
    }

    private static string HeadingPath(string title, List<(int Level, string Text)> headings)
    {
        List<string> parts = [title];
        for (int i = 0; i < headings.Count; i++)
        {
            string text = headings[i].Text;
            // The first heading of a file often repeats its title.
            if (i == 0 && string.Equals(text, title, StringComparison.OrdinalIgnoreCase))
                continue;

            if (text.Length > 0)
                parts.Add(text);
        }

        return string.Join(HeadingSeparator, parts);
    }

    // Front matter is a block of YAML between two lines "---" at the start of the file. The closing line may also be "...".
    private static int SkipFrontMatter(string content, out string? title)
    {
        title = null;
        if (!content.StartsWith("---\n", StringComparison.Ordinal))
            return 0;

        string? found = null;
        foreach (Range line in Lines(content, 4))
        {
            (int start, int length) = line.GetOffsetAndLength(content.Length);
            ReadOnlySpan<char> text = content.AsSpan(start, length).TrimEnd();
            if (text is "---" or "...")
            {
                title = found;
                return Math.Min(start + length + 1, content.Length);
            }

            if (text.StartsWith("title:", StringComparison.Ordinal))
            {
                string value = text["title:".Length..].Trim().Trim('"').Trim('\'').ToString();
                if (value.Length > 0)
                    found = value;
            }
        }

        return 0;
    }

    private static IEnumerable<Range> Lines(string content, int start) => Lines(content, start, content.Length);

    private static IEnumerable<Range> Lines(string content, int start, int end)
    {
        int position = start;
        while (position < end)
        {
            int newline = content.IndexOf('\n', position, end - position);
            int lineEnd = newline < 0 ? end : newline;
            yield return new Range(position, lineEnd);
            position = lineEnd + 1;
        }
    }

    private static int Indent(ReadOnlySpan<char> text)
    {
        int indent = 0;
        while (indent < text.Length && text[indent] == ' ')
            indent++;

        return indent;
    }

    private static bool TryAtxHeading(ReadOnlySpan<char> text, out int level, out string heading)
    {
        level = 0;
        heading = string.Empty;
        int indent = Indent(text);
        if (indent > 3)
            return false;

        ReadOnlySpan<char> rest = text[indent..];
        while (level < rest.Length && rest[level] == '#')
            level++;

        if (level is 0 or > 6 || (level < rest.Length && rest[level] is not (' ' or '\t')))
            return false;

        ReadOnlySpan<char> value = rest[level..].Trim();
        // A closing sequence of '#' after a space is no part of the heading.
        int closing = value.Length;
        while (closing > 0 && value[closing - 1] == '#')
            closing--;

        if (closing == 0 || value[closing - 1] is ' ' or '\t')
            value = value[..closing].TrimEnd();

        heading = value.ToString();
        return true;
    }

    private static bool TrySetextUnderline(ReadOnlySpan<char> text, out int level)
    {
        level = 0;
        int indent = Indent(text);
        ReadOnlySpan<char> rest = text[indent..].TrimEnd();
        if (indent > 3 || rest.Length == 0)
            return false;

        char marker = rest[0];
        if (marker is not ('=' or '-'))
            return false;

        foreach (char c in rest)
        {
            if (c != marker)
                return false;
        }

        level = marker == '=' ? 1 : 2;
        return true;
    }

    private static bool TryFenceOpen(ReadOnlySpan<char> text, out char fenceChar, out int fenceLength)
    {
        fenceChar = '\0';
        fenceLength = 0;
        int indent = Indent(text);
        if (indent > 3 || indent >= text.Length || text[indent] is not ('`' or '~'))
            return false;

        char marker = text[indent];
        int length = 0;
        while (indent + length < text.Length && text[indent + length] == marker)
            length++;

        // The info string of a backtick fence holds no backtick.
        if (length < 3 || (marker == '`' && text[(indent + length)..].Contains('`')))
            return false;

        (fenceChar, fenceLength) = (marker, length);
        return true;
    }

    private static bool IsFenceClose(ReadOnlySpan<char> text, char fenceChar, int fenceLength)
    {
        int indent = Indent(text);
        if (indent > 3)
            return false;

        ReadOnlySpan<char> rest = text[indent..].TrimEnd();
        if (rest.Length < fenceLength)
            return false;

        foreach (char c in rest)
        {
            if (c != fenceChar)
                return false;
        }

        return true;
    }

    private readonly record struct Block(int Start, int End);

    private sealed class Section(string heading)
    {
        public string Heading { get; } = heading;

        public List<Block> Blocks { get; } = [];
    }
}
