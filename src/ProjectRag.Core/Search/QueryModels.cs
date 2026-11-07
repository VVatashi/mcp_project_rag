using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using ProjectRag.Core.Embeddings;
using ProjectRag.Core.Reranking;

namespace ProjectRag.Core.Search;

/// <summary>
/// Runs the models that a search uses at query time: the embedding of the query and the reranker. Both are optional.
/// </summary>
/// <remarks>
/// A model that does not work gives a note and no exception, so the search still answers with the legs that work.
/// The documentation search and the code search share these steps and differ in the template of the query.
/// </remarks>
/// <param name="models">The registry of the embedding model.</param>
/// <param name="embedder">The client of the embedding server.</param>
/// <param name="reranker">The client of the reranker.</param>
/// <param name="searchOptions">The options of the search.</param>
/// <param name="rerankOptions">The options of the reranker.</param>
/// <param name="logger">The logger.</param>
public sealed class QueryModels(
    EmbeddingModelRegistry models,
    IEmbeddingClient embedder,
    IRerankClient reranker,
    IOptionsMonitor<SearchOptions> searchOptions,
    IOptionsMonitor<RerankOptions> rerankOptions,
    ILogger<QueryModels> logger)
{
    /// <summary>
    /// Embeds a query with the current model.
    /// </summary>
    /// <param name="query">The query.</param>
    /// <param name="template">The template of the query, see <see cref="EmbeddingModel.FormatQuery"/>.</param>
    /// <param name="notes">Gets a note when the vector search cannot run.</param>
    /// <param name="cancellationToken">The token that cancels the call.</param>
    /// <returns>The model and the vector, or two <see langword="null"/> values when the vector search cannot run.</returns>
    public async Task<(EmbeddingModel? Model, float[]? Vector)> EmbedAsync(string query, string template, List<string> notes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notes);
        EmbeddingModelStatus status = await models.GetStatusAsync(cancellationToken);
        if (status.Model is not { } current)
        {
            notes.Add($"The vector search did not work: {status.Problem}");
            return (null, null);
        }

        // A busy or hung server must not hold the search. The full text search answers without it.
        int seconds = Math.Max(1, searchOptions.CurrentValue.QueryTimeoutSeconds);
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(seconds));
        try
        {
            float[] vector = (await embedder.EmbedAsync([EmbeddingModel.FormatQuery(query, template)], timeout.Token))[0];
            return (current, vector);
        }
        catch (HttpRequestException exception)
        {
            models.Invalidate();
            notes.Add($"The vector search did not work: {exception.Message}");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            notes.Add($"The vector search did not work: the embedding server did not answer in {seconds} seconds.");
        }

        return (null, null);
    }

    /// <summary>
    /// Decides whether the reranker runs and how many candidates it gets.
    /// </summary>
    /// <param name="requested">The choice of the request, or <see langword="null"/> for the setting of the server.</param>
    /// <param name="limit">The number of hits that the request wants.</param>
    /// <returns>The decision and the number of candidates to find.</returns>
    public (bool Use, int Candidates) PlanRerank(bool? requested, int limit)
    {
        RerankOptions options = rerankOptions.CurrentValue;
        bool use = (requested ?? options.Enabled) && options.Candidates > 1;
        return (use, use ? Math.Max(limit, options.Candidates) : limit);
    }

    /// <summary>
    /// Scores texts against a query with the reranker.
    /// </summary>
    /// <param name="query">The query.</param>
    /// <param name="texts">The texts of the candidates.</param>
    /// <param name="notes">Gets a note when the reranker does not work.</param>
    /// <param name="cancellationToken">The token that cancels the call.</param>
    /// <returns>The scores in the order of the texts, or <see langword="null"/> when the reranker does not work.</returns>
    public async Task<IReadOnlyList<double>?> RerankAsync(string query, IReadOnlyList<string> texts, List<string> notes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notes);
        try
        {
            return await reranker.RerankAsync(query, texts, cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException || (exception is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            logger.LogWarning("The reranker did not work: {Reason}", exception.Message);
            notes.Add($"The reranker did not work: {exception.Message}");
            return null;
        }
    }
}
