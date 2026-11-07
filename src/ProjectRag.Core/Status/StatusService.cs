using Dapper;

using Microsoft.Extensions.Options;

using Npgsql;

using ProjectRag.Core.Embeddings;
using ProjectRag.Core.Indexing;
using ProjectRag.Core.Reranking;
using ProjectRag.Core.Storage;

namespace ProjectRag.Core.Status;

/// <summary>
/// The state of the knowledge base and of the services it needs.
/// </summary>
/// <param name="Database">The database: <see langword="null"/> when it works, or the error.</param>
/// <param name="Embedding">The embedding model.</param>
/// <param name="Reranker">The reranker.</param>
/// <param name="Counts">The counts of the stored rows, or <see langword="null"/> without the database.</param>
/// <param name="Indexer">The indexer.</param>
public sealed record KnowledgeStatus(string? Database, EmbeddingState Embedding, RerankerState Reranker, KnowledgeCounts? Counts, IndexerState Indexer);

/// <summary>
/// The state of the embedding model.
/// </summary>
/// <param name="Url">The address of the server.</param>
/// <param name="Name">The name of the model in the options.</param>
/// <param name="ModelId">The key of the model in the database, or <see langword="null"/> when it is not ready.</param>
/// <param name="Dims">The number of dimensions, or <see langword="null"/> when the model is not ready.</param>
/// <param name="Problem">The reason why the model is not ready, or <see langword="null"/>.</param>
/// <param name="Remote">The remote backend of the indexer.</param>
public sealed record EmbeddingState(Uri Url, string Name, short? ModelId, int? Dims, string? Problem, RemoteEmbeddingState Remote);

/// <summary>
/// The state of the remote embedding backend. The status shows the last check of the indexer and sends no request.
/// </summary>
/// <param name="Enabled">A value that tells whether the options enable the backend.</param>
/// <param name="Model">The name of the model at the backend.</param>
/// <param name="Sources">The sources that may use the backend.</param>
/// <param name="Unavailable">The reason why the backend cannot work, for example a missing API key, or <see langword="null"/>.</param>
/// <param name="GuardPassed">The result of the last identity check, or <see langword="null"/> when no check ran yet.</param>
/// <param name="Problem">The problem of the last identity check, or <see langword="null"/>.</param>
/// <param name="CheckedAt">The time of the last identity check, or <see langword="null"/>.</param>
public sealed record RemoteEmbeddingState(bool Enabled, string Model, IReadOnlyList<string> Sources, string? Unavailable, bool? GuardPassed, string? Problem, DateTimeOffset? CheckedAt);

/// <summary>
/// The state of the reranker.
/// </summary>
/// <param name="Url">The address of the server.</param>
/// <param name="Enabled">A value that tells whether the search uses the reranker by default.</param>
/// <param name="Healthy">A value that tells whether the server answers its health check.</param>
public sealed record RerankerState(Uri Url, bool Enabled, bool Healthy);

/// <summary>
/// The counts of the stored rows.
/// </summary>
/// <param name="Sources">The sources.</param>
/// <param name="Documents">The documents.</param>
/// <param name="Chunks">The chunks.</param>
/// <param name="EmbeddedChunks">The chunks with a vector of the current model.</param>
public sealed record KnowledgeCounts(long Sources, long Documents, long Chunks, long EmbeddedChunks);

/// <summary>
/// The state of the indexer.
/// </summary>
/// <param name="Current">The run that goes on now, or <see langword="null"/>.</param>
/// <param name="Queued">The keys of the sources in the queue.</param>
public sealed record IndexerState(IndexRun? Current, IReadOnlyList<int> Queued);

/// <summary>
/// Collects the <see cref="KnowledgeStatus"/>.
/// </summary>
/// <param name="database">The database.</param>
/// <param name="models">The registry of the embedding model.</param>
/// <param name="reranker">The client of the reranker.</param>
/// <param name="scheduler">The scheduler of the runs.</param>
/// <param name="embeddingOptions">The options of the embedding model.</param>
/// <param name="rerankOptions">The options of the reranker.</param>
/// <param name="remoteOptions">The options of the remote embedding backend.</param>
/// <param name="remoteClient">The client of the remote embedding backend.</param>
/// <param name="guard">The check of the identity of the remote backend.</param>
public sealed class StatusService(
    Database database,
    EmbeddingModelRegistry models,
    IRerankClient reranker,
    IndexScheduler scheduler,
    IOptionsMonitor<EmbeddingOptions> embeddingOptions,
    IOptionsMonitor<RerankOptions> rerankOptions,
    IOptionsMonitor<RemoteEmbeddingOptions> remoteOptions,
    IRemoteEmbeddingClient remoteClient,
    RemoteEmbeddingGuard guard)
{
    /// <summary>
    /// Collects the state.
    /// </summary>
    /// <param name="cancellationToken">The token that cancels the checks.</param>
    /// <returns>The state.</returns>
    public async Task<KnowledgeStatus> GetAsync(CancellationToken cancellationToken)
    {
        Task<bool> rerankerHealth = reranker.IsHealthyAsync(cancellationToken);
        EmbeddingOptions embedding = embeddingOptions.CurrentValue;
        RerankOptions rerank = rerankOptions.CurrentValue;
        string? databaseError = null;
        KnowledgeCounts? counts = null;
        RemoteEmbeddingOptions remote = remoteOptions.CurrentValue;
        RemoteCheck? check = guard.Last;
        EmbeddingState embeddingState = new(embedding.BaseUrl, embedding.Model, null, null, null,
            new RemoteEmbeddingState(remote.Enabled, remote.Model, [.. remote.Sources], remote.Enabled ? remoteClient.Unavailable : null, check?.Ok, check?.Problem, check?.CheckedAt));
        try
        {
            EmbeddingModelStatus model = await models.GetStatusAsync(cancellationToken);
            embeddingState = embeddingState with { ModelId = model.Model?.ModelId, Dims = model.Model?.Dims, Problem = model.Problem };
            await using NpgsqlConnection connection = await database.OpenAsync(cancellationToken);
            counts = await connection.QuerySingleAsync<KnowledgeCounts>(new CommandDefinition(
                """
                SELECT (SELECT count(*) FROM source) AS sources,
                       (SELECT count(*) FROM document) AS documents,
                       (SELECT count(*) FROM chunk) AS chunks,
                       (SELECT count(*) FROM chunk_embedding WHERE model_id = @ModelId) AS embeddedchunks
                """,
                new { ModelId = model.Model?.ModelId ?? 0 }, cancellationToken: cancellationToken));
        }
        catch (NpgsqlException exception)
        {
            databaseError = exception.Message;
        }

        return new KnowledgeStatus(
            databaseError,
            embeddingState,
            new RerankerState(rerank.BaseUrl, rerank.Enabled, await rerankerHealth),
            counts,
            new IndexerState(scheduler.Current, scheduler.Queued));
    }
}
