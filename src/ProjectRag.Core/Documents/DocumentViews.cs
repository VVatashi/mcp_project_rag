namespace ProjectRag.Core.Documents;

/// <summary>
/// A document without its content.
/// </summary>
public sealed class DocumentSummary
{
    /// <summary>
    /// Gets the category, such as <c>csharp</c>: from a rule of the source, or the name of the source.
    /// </summary>
    public string Category { get; init; } = string.Empty;

    /// <summary>
    /// Gets the key of the document.
    /// </summary>
    public long DocumentId { get; init; }

    /// <summary>
    /// Gets the key of the source.
    /// </summary>
    public int SourceId { get; init; }

    /// <summary>
    /// Gets the name of the source.
    /// </summary>
    public string Source { get; init; } = string.Empty;

    /// <summary>
    /// Gets the path of the file relative to the folder of the source, with forward slashes.
    /// </summary>
    public string Path { get; init; } = string.Empty;

    /// <summary>
    /// Gets the title.
    /// </summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>
    /// Gets the size of the file in bytes.
    /// </summary>
    public long SizeBytes { get; init; }

    /// <summary>
    /// Gets the time of the last write of the file.
    /// </summary>
    public DateTime ModifiedAt { get; init; }

    /// <summary>
    /// Gets the time when the indexer read the file.
    /// </summary>
    public DateTime IndexedAt { get; init; }

    /// <summary>
    /// Gets the number of characters of the content.
    /// </summary>
    public int ContentLength { get; init; }

    /// <summary>
    /// Gets the number of chunks.
    /// </summary>
    public long ChunkCount { get; init; }
}

/// <summary>
/// A part of the content of a document.
/// </summary>
/// <param name="Document">The document.</param>
/// <param name="Text">The part of the content.</param>
/// <param name="Offset">The index of the first character of the part in the content.</param>
/// <param name="TotalLength">The number of characters of the whole content.</param>
public sealed record DocumentContent(DocumentSummary Document, string Text, int Offset, int TotalLength);

/// <summary>
/// A chunk of a document as the dashboard shows it.
/// </summary>
public sealed class ChunkView
{
    /// <summary>
    /// Gets the key of the chunk.
    /// </summary>
    public long ChunkId { get; init; }

    /// <summary>
    /// Gets the number of the chunk in the document.
    /// </summary>
    public int Ordinal { get; init; }

    /// <summary>
    /// Gets the path of the section.
    /// </summary>
    public string Heading { get; init; } = string.Empty;

    /// <summary>
    /// Gets the text.
    /// </summary>
    public string Body { get; init; } = string.Empty;

    /// <summary>
    /// Gets the index of the first character in the content of the document.
    /// </summary>
    public int CharStart { get; init; }

    /// <summary>
    /// Gets the index after the last character.
    /// </summary>
    public int CharEnd { get; init; }

    /// <summary>
    /// Gets a value that tells whether the chunk has a vector of the current embedding model.
    /// </summary>
    public bool HasEmbedding { get; init; }

    /// <summary>
    /// Gets the key of the section.
    /// </summary>
    public long? SectionId { get; init; }

    /// <summary>
    /// Gets the first page, or <see langword="null"/> for a document without pages.
    /// </summary>
    public int? PageStart { get; init; }

    /// <summary>
    /// Gets the last page.
    /// </summary>
    public int? PageEnd { get; init; }

    /// <summary>
    /// Gets the number of characters at the start that the previous chunk also holds.
    /// </summary>
    public int OverlapChars { get; init; }
}

/// <summary>
/// The state of a file at the last run of the indexer.
/// </summary>
internal sealed class DocumentFileState
{
    public long DocumentId { get; init; }

    public string Path { get; init; } = string.Empty;

    public long SizeBytes { get; init; }

    public DateTime ModifiedAt { get; init; }

    public string ContentHash { get; init; } = string.Empty;

    public string ParserVersion { get; init; } = string.Empty;
}

/// <summary>
/// A category of the documents with its counts.
/// </summary>
public sealed class CategoryInfo
{
    /// <summary>Gets the name of the category, such as <c>vulkan</c>.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Gets the number of documents.</summary>
    public long DocumentCount { get; init; }

    /// <summary>Gets the number of chunks.</summary>
    public long ChunkCount { get; init; }

    /// <summary>Gets the number of chunks with a vector of the current embedding model.</summary>
    public long EmbeddedCount { get; init; }
}

/// <summary>
/// A page of the chunks of a document.
/// </summary>
/// <param name="Items">The chunks of the page in their order.</param>
/// <param name="Total">The number of the chunks that match.</param>
/// <param name="EmbeddedCount">The number of the chunks of the document with a vector of the current model.</param>
public sealed record ChunkPage(IReadOnlyList<ChunkView> Items, long Total, long EmbeddedCount);

/// <summary>
/// A display formula of a document.
/// </summary>
/// <param name="Latex">The LaTeX of the formula.</param>
/// <param name="Offset">The index of the formula in the content.</param>
/// <param name="Page">The page of the formula, or <see langword="null"/> for a document without pages.</param>
public sealed record FormulaView(string Latex, int Offset, int? Page);
