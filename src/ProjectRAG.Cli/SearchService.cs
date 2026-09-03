using System.Collections.Immutable;
using Microsoft.Extensions.Logging;

namespace ProjectRAG.Cli;

/// <summary>Searches the index. The full text mode matches words, the vector mode matches meaning.</summary>
public interface ISearchService
{
    /// <summary>Finds the chunks that hold the words of the query.</summary>
    Task<ImmutableArray<SearchHit>> SearchTextAsync(string query, int limit, CancellationToken cancellationToken = default);

    /// <summary>Embeds the query and finds the chunks that lie closest to it.</summary>
    Task<ImmutableArray<SearchHit>> SearchVectorAsync(string query, int limit, CancellationToken cancellationToken = default);
}

/// <summary>Runs the two searches over the repository. The query side of the embedding model lives here.</summary>
public sealed class SearchService(IDocumentRepository documents, Lazy<IEmbeddingProvider> embeddings, ILogger<SearchService> logger) : ISearchService
{
    private readonly IDocumentRepository _documents = documents;
    private readonly Lazy<IEmbeddingProvider> _embeddings = embeddings;
    private readonly ILogger<SearchService> _logger = logger;

    public async Task<ImmutableArray<SearchHit>> SearchTextAsync(string query, int limit, CancellationToken cancellationToken = default)
    {
        ImmutableArray<SearchHit> hits = await _documents.SearchByTextAsync(query, limit, cancellationToken);
        _logger.LogInformation("Full text search for {Query} found {Count} chunks", query, hits.Length);

        return hits;
    }

    public async Task<ImmutableArray<SearchHit>> SearchVectorAsync(string query, int limit, CancellationToken cancellationToken = default)
    {
        float[] vector = _embeddings.Value.EmbedQuery(query);
        ImmutableArray<SearchHit> hits = await _documents.SearchByVectorAsync(vector, limit, cancellationToken);
        _logger.LogInformation("Vector search for {Query} found {Count} chunks", query, hits.Length);

        return hits;
    }
}
