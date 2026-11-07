using ProjectRag.Core.Code.Graph;

namespace ProjectRag.Core.Code;

/// <summary>
/// The request of the search in tiers over the code sources.
/// </summary>
public sealed class CodeSearchRequest
{
    /// <summary>
    /// Gets or sets the query: an identifier, a text of code or a description in words. It holds 1 to 500 characters.
    /// </summary>
    public string Query { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the key of the code source, or <see langword="null"/> to search all code sources.
    /// </summary>
    public int? SourceId { get; set; }

    /// <summary>
    /// Gets or sets the name of the code source, used when <see cref="SourceId"/> is missing.
    /// </summary>
    public string? Source { get; set; }

    /// <summary>
    /// Gets or sets a pattern of paths, for example <c>src/**/*.cs</c>. A pattern without a slash matches the name of a file at any depth.
    /// </summary>
    public string? Path { get; set; }

    /// <summary>
    /// Gets or sets the language of the files, for example <c>csharp</c>.
    /// </summary>
    public string? Language { get; set; }

    /// <summary>
    /// Gets or sets a value that tells whether the files of test projects take part, or <see langword="null"/> for the default <see langword="true"/>.
    /// </summary>
    public bool? IncludeTests { get; set; }

    /// <summary>
    /// Gets or sets the number of similar chunks, or <see langword="null"/> for the default of <see cref="CodeTierSearch.DefaultLimit"/>.
    /// </summary>
    public int? Limit { get; set; }

    /// <summary>
    /// Gets or sets a value that tells whether the reranker reorders the similar chunks, or <see langword="null"/> for the setting of the server.
    /// </summary>
    public bool? Rerank { get; set; }
}

/// <summary>
/// A chunk of code that the search found.
/// </summary>
public sealed class CodeChunkHit
{
    /// <summary>Gets the key of the chunk.</summary>
    public long ChunkId { get; init; }

    /// <summary>Gets the key of the document.</summary>
    public long DocumentId { get; init; }

    /// <summary>Gets the key of the source.</summary>
    public int SourceId { get; init; }

    /// <summary>Gets the name of the source.</summary>
    public string Source { get; init; } = string.Empty;

    /// <summary>Gets the path of the file relative to the folder of the source.</summary>
    public string Path { get; init; } = string.Empty;

    /// <summary>Gets the language of the file.</summary>
    public string Language { get; init; } = string.Empty;

    /// <summary>Gets the heading of the chunk, such as <c>src/Scene/SceneHost.cs &gt; Axion.Scene.SceneHost &gt; public void Update(double time)</c>.</summary>
    public string Heading { get; init; } = string.Empty;

    /// <summary>Gets the first line of the chunk, from 1.</summary>
    public int LineStart { get; init; }

    /// <summary>Gets the last line of the chunk.</summary>
    public int LineEnd { get; init; }

    /// <summary>Gets the exact text of the chunk.</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>Gets or sets the score of the fusion, or the score of the reranker when the reranker ordered the hits.</summary>
    public double Score { get; set; }

    /// <summary>Gets the rank of the chunk in the vector search, from 1, or <see langword="null"/> when the vector search did not find it.</summary>
    public int? VectorRank { get; init; }

    /// <summary>Gets the rank of the chunk in the token search, from 1, or <see langword="null"/> when the token search did not find it.</summary>
    public int? TextRank { get; init; }

    /// <summary>Gets or sets the score of the reranker, or <see langword="null"/> without the reranker.</summary>
    public double? RerankScore { get; set; }

    /// <summary>Gets or sets the innermost symbol of the code graph whose declaration holds <see cref="LineStart"/>, or <see langword="null"/> without a graph.</summary>
    public SymbolSummary? Symbol { get; set; }
}

/// <summary>
/// The answer of the search in tiers.
/// </summary>
/// <remarks>
/// A place appears once, in its highest tier. An exact line on the name line of a listed symbol is dropped.
/// A chunk whose lines hold the name line of a listed symbol stays, because it shows the code.
/// </remarks>
/// <param name="Query">The query after the trim.</param>
/// <param name="Symbols">The tier of symbols, at most 10. It runs for a query without spaces.</param>
/// <param name="Matches">The tier of exact lines, at most 20. It runs for a query of one line with 3 or more characters.</param>
/// <param name="Chunks">The tier of similar code: the vector search and the token search fused, then the reranker.</param>
/// <param name="Mode">The legs of the third tier that worked: <c>hybrid</c>, <c>vector</c> or <c>text</c>.</param>
/// <param name="Reranked">A value that tells whether the reranker ordered the chunks.</param>
/// <param name="Notes">Messages about the legs that did not work.</param>
/// <param name="ElapsedMs">The time of the search in milliseconds.</param>
public sealed record CodeSearchResult(
    string Query,
    IReadOnlyList<SymbolMatch> Symbols,
    IReadOnlyList<CodeMatch> Matches,
    IReadOnlyList<CodeChunkHit> Chunks,
    string Mode,
    bool Reranked,
    IReadOnlyList<string> Notes,
    long ElapsedMs);
