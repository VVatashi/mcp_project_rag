namespace ProjectRag.Core.Embeddings;

/// <summary>
/// An embedding model that the database knows: the vectors of the chunks and of the queries must come from it.
/// </summary>
/// <param name="ModelId">The key of the model in the table <c>embedding_model</c>.</param>
/// <param name="Name">The name of the model.</param>
/// <param name="Dims">The number of dimensions of a vector.</param>
/// <param name="QueryTemplate">The template of a query.</param>
/// <param name="DocumentTemplate">The template of a chunk.</param>
public sealed record EmbeddingModel(short ModelId, string Name, int Dims, string QueryTemplate, string DocumentTemplate)
{
    /// <summary>
    /// Puts a query into the template of the queries.
    /// </summary>
    /// <param name="query">The query.</param>
    /// <returns>The text for the embedding model.</returns>
    public string FormatQuery(string query) => QueryTemplate.Replace(EmbeddingOptions.TextPlaceholder, query, StringComparison.Ordinal);

    /// <summary>
    /// Puts a chunk into the template of the chunks.
    /// </summary>
    /// <param name="text">The text of the chunk, see <see cref="Documents.TextChunk.EmbeddingText"/>.</param>
    /// <returns>The text for the embedding model.</returns>
    public string FormatDocument(string text) => DocumentTemplate.Replace(EmbeddingOptions.TextPlaceholder, text, StringComparison.Ordinal);
}

/// <summary>
/// The state of the embedding model: ready, or a reason why the search and the indexer cannot use vectors now.
/// </summary>
/// <param name="Model">The model, or <see langword="null"/> when it is not ready.</param>
/// <param name="Problem">The reason, or <see langword="null"/> when the model is ready.</param>
public sealed record EmbeddingModelStatus(EmbeddingModel? Model, string? Problem)
{
    /// <summary>
    /// Gets a value that tells whether the model is ready.
    /// </summary>
    public bool IsReady => Model is not null;
}
