namespace ProjectRag.Core.Documents;

/// <summary>
/// A section of a stored document.
/// </summary>
public sealed class SectionView
{
    /// <summary>Gets the key of the section.</summary>
    public long SectionId { get; init; }

    /// <summary>Gets the key of the parent section, or <see langword="null"/> for a top section.</summary>
    public long? ParentId { get; init; }

    /// <summary>Gets the number of the section in the document, from 0.</summary>
    public int Ordinal { get; init; }

    /// <summary>Gets the level of the heading. The section 0 holds the text before the first heading and has the level 0.</summary>
    public int Level { get; init; }

    /// <summary>Gets the number at the start of the heading, for example 8.14.2.</summary>
    public string? Number { get; init; }

    /// <summary>Gets the title.</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>Gets the path from the document down to the section.</summary>
    public string Path { get; init; } = string.Empty;

    /// <summary>Gets the index of the first character of the heading in the content.</summary>
    public int CharStart { get; init; }

    /// <summary>Gets the index after the last character of the section with its subsections.</summary>
    public int CharEnd { get; init; }

    /// <summary>Gets the first page, or <see langword="null"/> for a document without pages.</summary>
    public int? PageStart { get; init; }

    /// <summary>Gets the last page.</summary>
    public int? PageEnd { get; init; }

    /// <summary>Gets the number of chunks of the section without its subsections.</summary>
    public long ChunkCount { get; init; }
}

/// <summary>
/// An anchor of a stored document: a place that a link can name.
/// </summary>
public sealed class AnchorView
{
    /// <summary>Gets the name of the anchor.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Gets the index of the character at the anchor in the content.</summary>
    public int CharOffset { get; init; }

    /// <summary>Gets the key of the first chunk that ends after the anchor, or <see langword="null"/> when no chunk does.</summary>
    public long? ChunkId { get; init; }
}

/// <summary>
/// A link of a chunk with its resolved target.
/// </summary>
public sealed class ChunkLinkView
{
    /// <summary>Gets the kind: <c>link</c> for a Markdown link or a link of a PDF file, <c>mention</c> for a mention in the text.</summary>
    public string Kind { get; init; } = string.Empty;

    /// <summary>Gets the text of the link.</summary>
    public string Label { get; init; } = string.Empty;

    /// <summary>Gets the path of the target document, or <see langword="null"/> for the same document.</summary>
    public string? TargetPath { get; init; }

    /// <summary>Gets the anchor of the target.</summary>
    public string? TargetAnchor { get; init; }

    /// <summary>Gets the key of the target document, or <see langword="null"/> when the source has no such document.</summary>
    public long? TargetDocumentId { get; init; }

    /// <summary>Gets the key of the chunk at the target, or <see langword="null"/> when the target does not resolve.</summary>
    public long? TargetChunkId { get; init; }

    /// <summary>Gets the path of the section of the chunk at the target.</summary>
    public string? TargetHeading { get; init; }

    /// <summary>Gets the first page of the chunk at the target.</summary>
    public int? TargetPage { get; init; }
}

/// <summary>
/// A link from another chunk to a chunk.
/// </summary>
public sealed class ChunkBacklinkView
{
    /// <summary>Gets the key of the chunk with the link.</summary>
    public long ChunkId { get; init; }

    /// <summary>Gets the key of the document of that chunk.</summary>
    public long DocumentId { get; init; }

    /// <summary>Gets the path of that document.</summary>
    public string Path { get; init; } = string.Empty;

    /// <summary>Gets the path of the section of that chunk.</summary>
    public string Heading { get; init; } = string.Empty;

    /// <summary>Gets the text of the link.</summary>
    public string Label { get; init; } = string.Empty;

    /// <summary>Gets the kind of the link.</summary>
    public string Kind { get; init; } = string.Empty;
}

/// <summary>
/// A chunk with its place in the document and its links, for an agent that follows a hit.
/// </summary>
public sealed class ChunkDetail
{
    /// <summary>Gets the key of the chunk.</summary>
    public long ChunkId { get; init; }

    /// <summary>Gets the key of the document.</summary>
    public long DocumentId { get; init; }

    /// <summary>Gets the name of the source.</summary>
    public string Source { get; init; } = string.Empty;

    /// <summary>Gets the path of the document.</summary>
    public string Path { get; init; } = string.Empty;

    /// <summary>Gets the title of the document.</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>Gets the number of the chunk in the document.</summary>
    public int Ordinal { get; init; }

    /// <summary>Gets the path of the section.</summary>
    public string Heading { get; init; } = string.Empty;

    /// <summary>Gets the Markdown text.</summary>
    public string Body { get; init; } = string.Empty;

    /// <summary>Gets the index of the first character in the content of the document.</summary>
    public int CharStart { get; init; }

    /// <summary>Gets the index after the last character.</summary>
    public int CharEnd { get; init; }

    /// <summary>Gets the first page, or <see langword="null"/> for a document without pages.</summary>
    public int? PageStart { get; init; }

    /// <summary>Gets the last page.</summary>
    public int? PageEnd { get; init; }

    /// <summary>Gets the number of characters at the start that the previous chunk also holds.</summary>
    public int OverlapChars { get; init; }

    /// <summary>Gets the key of the section.</summary>
    public long? SectionId { get; init; }

    /// <summary>Gets the number of the section, for example 8.14.2.</summary>
    public string? SectionNumber { get; init; }

    /// <summary>Gets a value that tells whether the chunk has a vector of the current embedding model.</summary>
    public bool HasEmbedding { get; init; }

    /// <summary>Gets the key of the previous chunk of the document, or <see langword="null"/> for the first chunk.</summary>
    public long? PreviousChunkId { get; init; }

    /// <summary>Gets the key of the next chunk of the document, or <see langword="null"/> for the last chunk.</summary>
    public long? NextChunkId { get; init; }

    /// <summary>Gets the links of the chunk with their targets.</summary>
    public IReadOnlyList<ChunkLinkView> Links { get; set; } = [];

    /// <summary>Gets the links of other chunks to this chunk.</summary>
    public IReadOnlyList<ChunkBacklinkView> Backlinks { get; set; } = [];
}
