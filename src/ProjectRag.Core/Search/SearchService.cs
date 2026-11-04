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
        // A JSON null reaches the property despite its type.
        string query = request.Query?.Trim() ?? string.Empty;
        if (query.Length == 0)
            throw new ArgumentException("The query is empty.");

        SearchOptions options = searchOptions.CurrentValue;
        RerankOptions rerank = rerankOptions.CurrentValue;
        int limit = Math.Clamp(request.Limit ?? options.DefaultLimit, 1, options.MaxLimit);
        int[] sourceIds = await ResolveSourcesAsync(request.Sources, cancellationToken);
        string[] categories = await ResolveCategoriesAsync(request.Categories, cancellationToken);
        List<string> notes = [];

        EmbeddingModel? model = null;
        float[]? vector = null;
        EmbeddingModelStatus status = await models.GetStatusAsync(cancellationToken);
        if (status.Model is { } current)
        {
            // A busy or hung server must not hold the search. The full text search answers without it.
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, options.QueryTimeoutSeconds)));
            try
            {
                vector = (await embedder.EmbedAsync([current.FormatQuery(query)], timeout.Token))[0];
                model = current;
            }
            catch (HttpRequestException exception)
            {
                models.Invalidate();
                notes.Add($"The vector search did not work: {exception.Message}");
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                notes.Add($"The vector search did not work: the embedding server did not answer in {options.QueryTimeoutSeconds} seconds.");
            }
        }
        else
        {
            notes.Add($"The vector search did not work: {status.Problem}");
        }

        bool useReranker = (request.Rerank ?? rerank.Enabled) && rerank.Candidates > 1;
        int candidates = useReranker ? Math.Max(limit, rerank.Candidates) : limit;
        // The copies of a text take places of the candidates, so the store finds twice as many.
        IReadOnlyList<SearchHit> found = await store.SearchAsync(model, vector, query, sourceIds, categories, candidates * 2, options, cancellationToken);
        IReadOnlyList<SearchHit> hits = [.. MergeCopies(found).Take(candidates)];
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
            catch (Exception exception) when (exception is HttpRequestException || (exception is TaskCanceledException && !cancellationToken.IsCancellationRequested))
            {
                logger.LogWarning("The reranker did not work: {Reason}", exception.Message);
                notes.Add($"The reranker did not work: {exception.Message}");
            }
        }

        return new SearchResult(mode, reranked, notes, [.. hits.Take(limit)]);
    }

    // A hit takes the later hits with the same text as its copies, so the text fills one place of the result.
    private static List<SearchHit> MergeCopies(IReadOnlyList<SearchHit> hits)
    {
        List<SearchHit> merged = [];
        Dictionary<string, SearchHit> first = new(StringComparer.Ordinal);
        foreach (SearchHit hit in hits)
        {
            if (first.TryGetValue(hit.Body, out SearchHit? kept))
            {
                kept.Copies = [.. kept.Copies, new HitCopy(hit.ChunkId, hit.DocumentId, hit.Path, hit.Heading)];
                continue;
            }

            first.Add(hit.Body, hit);
            merged.Add(hit);
        }

        return merged;
    }

    private async Task<string[]> ResolveCategoriesAsync(IReadOnlyList<string>? names, CancellationToken cancellationToken)
    {
        string[] wanted = [.. (names ?? []).Select(static name => name?.Trim() ?? string.Empty).Where(static name => name.Length > 0).Distinct(StringComparer.Ordinal)];
        if (wanted.Length == 0)
            return [];

        IReadOnlySet<string> found = await store.FindCategoriesAsync(wanted, cancellationToken);
        string[] missing = [.. wanted.Where(name => !found.Contains(name))];
        if (missing.Length > 0)
            throw new ArgumentException($"The knowledge base has no category {string.Join(", ", missing)}.");

        return wanted;
    }

    private async Task<int[]> ResolveSourcesAsync(IReadOnlyList<string>? names, CancellationToken cancellationToken)
    {
        string[] wanted = [.. (names ?? []).Select(static name => name?.Trim() ?? string.Empty).Where(static name => name.Length > 0).Distinct(StringComparer.Ordinal)];
        if (wanted.Length == 0)
            return [];

        IReadOnlyDictionary<string, int> found = await sources.FindIdsAsync(wanted, cancellationToken);
        string[] missing = [.. wanted.Where(name => !found.ContainsKey(name))];
        if (missing.Length > 0)
            throw new ArgumentException($"The knowledge base has no source {string.Join(", ", missing)}.");

        return [.. found.Values];
    }
}
