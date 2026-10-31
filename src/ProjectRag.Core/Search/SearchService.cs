using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using ProjectRag.Core.Documents;
using ProjectRag.Core.Embeddings;
using ProjectRag.Core.Reranking;
using ProjectRag.Core.Sources;

namespace ProjectRag.Core.Search;

/// <summary>
/// Answers a query with chunks: the hybrid search finds the candidates, and the reranker orders them.
/// </summary>
/// <remarks>
/// Each model is optional. Without the embedding server the search uses the full text search only.
/// Without the reranker it keeps the order of the fusion. The result names the legs that worked.
/// </remarks>
/// <param name="store">The SQL of the search.</param>
/// <param name="sources">The store of the sources.</param>
/// <param name="models">The registry of the embedding model.</param>
/// <param name="embedder">The client of the embedding server.</param>
/// <param name="reranker">The client of the reranker.</param>
/// <param name="searchOptions">The options of the search.</param>
/// <param name="rerankOptions">The options of the reranker.</param>
/// <param name="logger">The logger.</param>
public sealed class SearchService(
    SearchStore store,
    SourceStore sources,
    EmbeddingModelRegistry models,
    IEmbeddingClient embedder,
    IRerankClient reranker,
    IOptionsMonitor<SearchOptions> searchOptions,
    IOptionsMonitor<RerankOptions> rerankOptions,
    ILogger<SearchService> logger)
{
    /// <summary>
    /// Searches the knowledge base.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">The token that cancels the search.</param>
    /// <returns>The hits and the state of the legs.</returns>
    /// <exception cref="ArgumentException">The query is empty, or a source does not exist.</exception>
    public async Task<SearchResult> SearchAsync(SearchRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        string query = request.Query.Trim();
        if (query.Length == 0)
            throw new ArgumentException("The query is empty.", nameof(request));

        SearchOptions options = searchOptions.CurrentValue;
        RerankOptions rerank = rerankOptions.CurrentValue;
        int limit = Math.Clamp(request.Limit ?? options.DefaultLimit, 1, options.MaxLimit);
        int[] sourceIds = await ResolveSourcesAsync(request.Sources, cancellationToken);
        List<string> notes = [];

        EmbeddingModel? model = null;
        float[]? vector = null;
        EmbeddingModelStatus status = await models.GetStatusAsync(cancellationToken);
        if (status.Model is { } current)
        {
            try
            {
                vector = (await embedder.EmbedAsync([current.FormatQuery(query)], cancellationToken))[0];
                model = current;
            }
            catch (HttpRequestException exception)
            {
                models.Invalidate();
                notes.Add($"The vector search did not work: {exception.Message}");
            }
        }
        else
        {
            notes.Add($"The vector search did not work: {status.Problem}");
        }

        bool useReranker = (request.Rerank ?? rerank.Enabled) && rerank.Candidates > 1;
        int candidates = useReranker ? Math.Max(limit, rerank.Candidates) : limit;
        IReadOnlyList<SearchHit> hits = await store.SearchAsync(model, vector, query, sourceIds, candidates, options, cancellationToken);
        string mode = model is null ? "text" : hits.Any(static hit => hit.TextRank is not null) ? "hybrid" : "vector";

        bool reranked = false;
        if (useReranker && hits.Count > 1)
        {
            try
            {
                IReadOnlyList<double> scores = await reranker.RerankAsync(query, [.. hits.Select(static hit => TextChunk.Compose(hit.Heading, hit.Body))], cancellationToken);
                for (int i = 0; i < hits.Count; i++)
                    hits[i].RerankScore = scores[i];

                hits = [.. hits.OrderByDescending(static hit => hit.RerankScore).ThenByDescending(static hit => hit.Score)];
                reranked = true;
            }
            catch (HttpRequestException exception)
            {
                logger.LogWarning("The reranker did not work: {Reason}", exception.Message);
                notes.Add($"The reranker did not work: {exception.Message}");
            }
        }

        return new SearchResult(mode, reranked, notes, [.. hits.Take(limit)]);
    }

    private async Task<int[]> ResolveSourcesAsync(IReadOnlyList<string>? names, CancellationToken cancellationToken)
    {
        string[] wanted = [.. (names ?? []).Select(static name => name.Trim()).Where(static name => name.Length > 0).Distinct(StringComparer.Ordinal)];
        if (wanted.Length == 0)
            return [];

        IReadOnlyDictionary<string, int> found = await sources.FindIdsAsync(wanted, cancellationToken);
        string[] missing = [.. wanted.Where(name => !found.ContainsKey(name))];
        if (missing.Length > 0)
            throw new ArgumentException($"The knowledge base has no source {string.Join(", ", missing)}.", nameof(names));

        return [.. found.Values];
    }
}
