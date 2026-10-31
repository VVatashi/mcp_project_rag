namespace ProjectRag.Core.Documents;

/// <summary>
/// A document without its content.
/// </summary>
public sealed class DocumentSummary
{
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

    public int ParserVersion { get; init; }
}
