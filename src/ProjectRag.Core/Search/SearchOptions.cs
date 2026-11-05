namespace ProjectRag.Core.Search;

/// <summary>
/// The options of the hybrid search.
/// </summary>
/// <remarks>
/// The defaults come from the measurements of the project rag (ADR 0047 and 0048 there): reciprocal rank fusion with k = 60 and a weight of 0.8 for the vector.
/// </remarks>
public sealed class SearchOptions
{
    /// <summary>
    /// The configuration section that holds these options.
    /// </summary>
    public const string SectionName = "Search";

    /// <summary>
    /// Gets or sets the number of results when the request names none.
    /// </summary>
    public int DefaultLimit { get; set; } = 8;

    /// <summary>
    /// Gets or sets the most results of one request.
    /// </summary>
    public int MaxLimit { get; set; } = 50;

    /// <summary>
    /// Gets or sets the number of candidates of the vector search and of the full text search before the fusion.
    /// </summary>
    public int Pool { get; set; } = 100;

    /// <summary>
    /// Gets or sets the constant k of the fusion: a candidate scores <c>w / (k + rank)</c>.
    /// </summary>
    public int RrfK { get; set; } = 60;

    /// <summary>
    /// Gets or sets the weight w of the vector in the fusion. The full text search gets <c>1 - w</c>.
    /// </summary>
    public double VectorWeight { get; set; } = 0.8;

    /// <summary>
    /// Gets or sets the time limit of the embedding of a query in seconds. After it the search uses the full text search only.
    /// </summary>
    public int QueryTimeoutSeconds { get; set; } = 20;

    /// <summary>
    /// Gets or sets the categories that a request without categories skips, such as the old versions of a specification.
    /// A request that names such a category searches it.
    /// </summary>
    public string[] HiddenCategories { get; set; } = [];
}
