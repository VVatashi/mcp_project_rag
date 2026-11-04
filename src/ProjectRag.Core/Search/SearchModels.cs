namespace ProjectRag.Core.Search;

/// <summary>
/// A request of the search.
/// </summary>
public sealed class SearchRequest
{
    /// <summary>
    /// Gets or sets the query: a question or keywords.
    /// </summary>
    public string Query { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the names of the sources to search, or <see langword="null"/> for all sources.
    /// </summary>
    public IReadOnlyList<string>? Sources { get; set; }

    /// <summary>
    /// Gets or sets the categories of the documents to search, such as <c>csharp</c> or <c>vulkan</c>. <see langword="null"/> or an empty list searches all categories.
    /// </summary>
    public IReadOnlyList<string>? Categories { get; set; }

    /// <summary>
    /// Gets or sets the number of results, or <see langword="null"/> for the default of the options.
    /// </summary>
    public int? Limit { get; set; }

    /// <summary>
    /// Gets or sets a value that tells whether the reranker reorders the candidates, or <see langword="null"/> for the default of the options.
    /// </summary>
    public bool? Rerank { get; set; }
}

/// <summary>
/// The result of a search.
/// </summary>
/// <param name="Mode">The legs of the search that worked: <c>hybrid</c>, <c>vector</c> or <c>text</c>.</param>
/// <param name="Reranked">A value that tells whether the reranker ordered the hits.</param>
/// <param name="Notes">Messages about the legs that did not work.</param>
/// <param name="Hits">The hits, the best first.</param>
public sealed record SearchResult(string Mode, bool Reranked, IReadOnlyList<string> Notes, IReadOnlyList<SearchHit> Hits);

/// <summary>
/// A chunk that the search found.
/// </summary>
public sealed class SearchHit
{
    /// <summary>Gets the key of the chunk.</summary>
    public long ChunkId { get; init; }

    /// <summary>Gets the key of the document.</summary>
    public long DocumentId { get; init; }

    /// <summary>Gets the name of the source.</summary>
    public string Source { get; init; } = string.Empty;

    /// <summary>
    /// Gets the category of the document.
    /// </summary>
    public string Category { get; init; } = string.Empty;

    /// <summary>Gets the path of the file relative to the folder of the source.</summary>
    public string Path { get; init; } = string.Empty;

    /// <summary>Gets the title of the document.</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>Gets the path of the section.</summary>
    public string Heading { get; init; } = string.Empty;

    /// <summary>Gets the text of the chunk.</summary>
    public string Body { get; init; } = string.Empty;

    /// <summary>Gets the index of the first character of the chunk in the content of the document.</summary>
    public int CharStart { get; init; }

    /// <summary>Gets the index after the last character.</summary>
    public int CharEnd { get; init; }

    /// <summary>Gets the first page of the chunk, or <see langword="null"/> for a document without pages.</summary>
    public int? PageStart { get; init; }

    /// <summary>Gets the last page of the chunk.</summary>
    public int? PageEnd { get; init; }

    /// <summary>Gets the key of the section of the chunk.</summary>
    public long? SectionId { get; init; }

    /// <summary>Gets the number of links of the chunk. The tool read_chunk resolves them.</summary>
    public long LinkCount { get; init; }

    /// <summary>Gets the rank of the chunk in the vector search, from 1, or <see langword="null"/> when the vector search did not find it.</summary>
    public int? VectorRank { get; init; }

    /// <summary>Gets the rank of the chunk in the full text search, from 1, or <see langword="null"/> when the full text search did not find it.</summary>
    public int? TextRank { get; init; }

    /// <summary>Gets the score of the fusion.</summary>
    public double Score { get; init; }

    /// <summary>Gets the score of the reranker, or <see langword="null"/> without the reranker.</summary>
    public double? RerankScore { get; set; }

    /// <summary>
    /// Gets the other chunks with almost the same text, such as a paragraph that the core and the compatibility profile of OpenGL share. The search shows the text once.
    /// </summary>
    public IReadOnlyList<HitCopy> Copies { get; set; } = [];
}

/// <summary>
/// A chunk with almost the same text as a hit.
/// </summary>
/// <param name="ChunkId">The key of the chunk.</param>
/// <param name="DocumentId">The key of the document.</param>
/// <param name="Path">The path of the document.</param>
/// <param name="Heading">The path of the section.</param>
public sealed record HitCopy(long ChunkId, long DocumentId, string Path, string Heading);
