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

    /// <summary>Gets the rank of the chunk in the vector search, from 1, or <see langword="null"/> when the vector search did not find it.</summary>
    public int? VectorRank { get; init; }

    /// <summary>Gets the rank of the chunk in the full text search, from 1, or <see langword="null"/> when the full text search did not find it.</summary>
    public int? TextRank { get; init; }

    /// <summary>Gets the score of the fusion.</summary>
    public double Score { get; init; }

    /// <summary>Gets the score of the reranker, or <see langword="null"/> without the reranker.</summary>
    public double? RerankScore { get; set; }
}
