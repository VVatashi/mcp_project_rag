namespace ProjectRag.Core.Documents;

/// <summary>
/// The kind of a block of a document.
/// </summary>
public enum BlockKind
{
    /// <summary>A paragraph. The chunker may split it between sentences.</summary>
    Paragraph,

    /// <summary>An item of a list. The chunker may split it between sentences.</summary>
    ListItem,

    /// <summary>A quote. The chunker may split it between sentences.</summary>
    Quote,

    /// <summary>A footnote. The chunker may split it between sentences.</summary>
    Footnote,

    /// <summary>A block of code. The chunker keeps it whole when it can.</summary>
    Code,

    /// <summary>A display formula. The chunker keeps it whole when it can.</summary>
    Math,

    /// <summary>A table. The chunker keeps it whole when it can.</summary>
    Table,

    /// <summary>A block of HTML.</summary>
    Html,
}

/// <summary>
/// A block of a document: a range of its Markdown content.
/// </summary>
/// <param name="Kind">The kind.</param>
/// <param name="Start">The index of the first character.</param>
/// <param name="End">The index after the last character.</param>
/// <param name="Section">The index of the section in <see cref="DocumentStructure.Sections"/>.</param>
public sealed record DocumentBlock(BlockKind Kind, int Start, int End, int Section)
{
    /// <summary>
    /// Gets a value that tells whether a break inside the block harms its meaning: code, a formula or a table.
    /// </summary>
    public bool IsAtomic => Kind is BlockKind.Code or BlockKind.Math or BlockKind.Table;
}

/// <summary>
/// A section of a document: the text from a heading to the next heading of the same or a higher level.
/// </summary>
/// <param name="Index">The index of the section in <see cref="DocumentStructure.Sections"/>. The section 0 holds the text before the first heading.</param>
/// <param name="Parent">The index of the parent section, or -1 for a top section.</param>
/// <param name="Level">The level of the heading, from 1. The section 0 has the level 0.</param>
/// <param name="Title">The text of the heading, or the title of the document for the section 0.</param>
/// <param name="Number">The number at the start of the heading, for example <c>8.14.2</c>, or <see langword="null"/>.</param>
/// <param name="Anchor">The anchor of the heading, or <see langword="null"/> for the section 0.</param>
/// <param name="Path">The titles from the document down to this section, joined with " &gt; ".</param>
/// <param name="Start">The index of the first character of the heading.</param>
/// <param name="End">The index after the last character of the section with its subsections.</param>
public sealed record DocumentSection(int Index, int Parent, int Level, string Title, string? Number, string? Anchor, string Path, int Start, int End);

/// <summary>
/// The kind of a link between parts of documents.
/// </summary>
public enum LinkKind
{
    /// <summary>A Markdown link, or a link annotation of a PDF file.</summary>
    Link,

    /// <summary>A mention in the text, such as "see section 8.14" or "table 8.20".</summary>
    Mention,
}

/// <summary>
/// A link from a place of a document to an anchor of the same or another document of the source.
/// </summary>
/// <param name="Kind">The kind.</param>
/// <param name="Start">The index of the first character of the link text.</param>
/// <param name="End">The index after the last character.</param>
/// <param name="Label">The text of the link.</param>
/// <param name="TargetPath">The path of the target document in the source, or <see langword="null"/> for the same document.</param>
/// <param name="TargetAnchor">The anchor of the target, or <see langword="null"/> for the start of the target document.</param>
public sealed record DocumentLink(LinkKind Kind, int Start, int End, string Label, string? TargetPath, string? TargetAnchor);

/// <summary>
/// The structure of a document in its Markdown form: sections, blocks, anchors, pages and links.
/// </summary>
/// <param name="Title">The title.</param>
/// <param name="Content">The Markdown content. All offsets point into it.</param>
/// <param name="Sections">The sections in the order of the text. The section 0 holds the text before the first heading.</param>
/// <param name="Blocks">The blocks in the order of the text.</param>
/// <param name="Anchors">The offsets of the anchors by name: the identifiers of the headings and the anchors of the importer.</param>
/// <param name="Links">The links in the order of the text.</param>
/// <param name="Pages">The page marks of the importer: the offset where a page starts and its number. Empty for a document without pages.</param>
/// <param name="Hidden">The ranges of the service marks, which the text of a chunk leaves out.</param>
public sealed record DocumentStructure(
    string Title,
    string Content,
    IReadOnlyList<DocumentSection> Sections,
    IReadOnlyList<DocumentBlock> Blocks,
    IReadOnlyDictionary<string, int> Anchors,
    IReadOnlyList<DocumentLink> Links,
    IReadOnlyList<(int Offset, int Page)> Pages,
    IReadOnlyList<(int Start, int End)> Hidden)
{
    /// <summary>
    /// Gets the page that holds an offset, or <see langword="null"/> for a document without pages.
    /// </summary>
    /// <param name="offset">The offset in the content.</param>
    /// <returns>The number of the page.</returns>
    public int? PageAt(int offset)
    {
        int? page = null;
        foreach ((int start, int number) in Pages)
        {
            if (start > offset)
                break;

            page = number;
        }

        return page ?? (Pages.Count > 0 ? Pages[0].Page : null);
    }
}
