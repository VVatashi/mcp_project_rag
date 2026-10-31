using System.Globalization;

using Dapper;

using Npgsql;

using ProjectRag.Core.Storage;

namespace ProjectRag.Core.Embeddings;

/// <summary>
/// Reads and writes the embedding models and the vectors of the chunks.
/// </summary>
/// <param name="database">The database.</param>
public sealed class EmbeddingStore(Database database)
{
    /// <summary>
    /// Finds the models with a name and templates. The dimensions complete the identity, and the caller learns them from the probe.
    /// </summary>
    /// <param name="name">The name of the model.</param>
    /// <param name="queryTemplate">The template of a query.</param>
    /// <param name="documentTemplate">The template of a chunk.</param>
    /// <param name="cancellationToken">The token that cancels the query.</param>
    /// <returns>The models, the oldest first. Usually there is one or none.</returns>
    internal async Task<IReadOnlyList<StoredModel>> FindModelsAsync(string name, string queryTemplate, string documentTemplate, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await database.OpenAsync(cancellationToken);
        IEnumerable<StoredModel> rows = await connection.QueryAsync<StoredModel>(new CommandDefinition(
            """
            SELECT model_id, dims, probe_text, probe_embedding::real[] AS probe_embedding
            FROM embedding_model
            WHERE name = @Name AND query_template = @QueryTemplate AND document_template = @DocumentTemplate
            ORDER BY model_id
            """,
            new { Name = name, QueryTemplate = queryTemplate, DocumentTemplate = documentTemplate },
            cancellationToken: cancellationToken));
        return [.. rows];
    }

    /// <summary>
    /// Adds a model and creates its HNSW index.
    /// </summary>
    /// <param name="name">The name of the model.</param>
    /// <param name="queryTemplate">The template of a query.</param>
    /// <param name="documentTemplate">The template of a chunk.</param>
    /// <param name="probeText">The text of the probe.</param>
    /// <param name="probeEmbedding">The vector of the probe. Its length gives the dimensions of the model.</param>
    /// <param name="cancellationToken">The token that cancels the query.</param>
    /// <returns>The key of the new model.</returns>
    public async Task<short> AddModelAsync(string name, string queryTemplate, string documentTemplate, string probeText, float[] probeEmbedding, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(probeEmbedding);
        await using NpgsqlConnection connection = await database.OpenAsync(cancellationToken);
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);
        short modelId = await connection.ExecuteScalarAsync<short>(new CommandDefinition(
            """
            INSERT INTO embedding_model (name, dims, query_template, document_template, probe_text, probe_embedding)
            VALUES (@Name, @Dims, @QueryTemplate, @DocumentTemplate, @ProbeText, @ProbeEmbedding::real[]::vector)
            RETURNING model_id
            """,
            new { Name = name, Dims = probeEmbedding.Length, QueryTemplate = queryTemplate, DocumentTemplate = documentTemplate, ProbeText = probeText, ProbeEmbedding = probeEmbedding },
            transaction, cancellationToken: cancellationToken));

        // The index is partial and over an expression with the dimensions of the model, see the page "Indexing" of the README of pgvector.
        // halfvec takes up to 4000 dimensions. It halves the index, and the project rag measured the same recall (ADR 0047 there).
        // The number of the model goes into the text: a partial index needs a literal in the condition of the query too.
        string sql = string.Create(CultureInfo.InvariantCulture, $"""
            CREATE INDEX chunk_embedding_hnsw_m{modelId} ON chunk_embedding
            USING hnsw ((embedding::halfvec({probeEmbedding.Length})) halfvec_cosine_ops)
            WITH (m = 16, ef_construction = 128)
            WHERE model_id = {modelId}
            """);
        await connection.ExecuteAsync(new CommandDefinition(sql, transaction: transaction, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        return modelId;
    }

    /// <summary>
    /// Reads the chunks of a source that have no vector of a model.
    /// </summary>
    /// <param name="sourceId">The source.</param>
    /// <param name="modelId">The model.</param>
    /// <param name="limit">The most chunks to read.</param>
    /// <param name="excluded">The chunks to skip, for example the chunks that the server rejected in this run.</param>
    /// <param name="cancellationToken">The token that cancels the query.</param>
    /// <returns>The chunks in the order of their keys.</returns>
    public async Task<IReadOnlyList<PendingChunk>> GetPendingAsync(int sourceId, short modelId, int limit, IReadOnlyCollection<long> excluded, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(excluded);
        await using NpgsqlConnection connection = await database.OpenAsync(cancellationToken);
        IEnumerable<PendingChunk> rows = await connection.QueryAsync<PendingChunk>(new CommandDefinition(
            """
            SELECT c.chunk_id, c.heading, c.body
            FROM chunk c
            JOIN document d USING (document_id)
            WHERE d.source_id = @SourceId
              AND c.chunk_id <> ALL (@Excluded)
              AND NOT EXISTS (SELECT 1 FROM chunk_embedding e WHERE e.chunk_id = c.chunk_id AND e.model_id = @ModelId)
            ORDER BY c.chunk_id
            LIMIT @Limit
            """,
            new { SourceId = sourceId, ModelId = modelId, Limit = limit, Excluded = excluded.ToArray() },
            cancellationToken: cancellationToken));
        return [.. rows];
    }

    /// <summary>
    /// Counts the chunks that have no vector of a model.
    /// </summary>
    /// <param name="sourceId">The source, or <see langword="null"/> for all sources.</param>
    /// <param name="modelId">The model.</param>
    /// <param name="cancellationToken">The token that cancels the query.</param>
    /// <returns>The number of chunks.</returns>
    public async Task<long> CountPendingAsync(int? sourceId, short modelId, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await database.OpenAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            """
            SELECT count(*)
            FROM chunk c
            JOIN document d USING (document_id)
            WHERE (@SourceId::integer IS NULL OR d.source_id = @SourceId)
              AND NOT EXISTS (SELECT 1 FROM chunk_embedding e WHERE e.chunk_id = c.chunk_id AND e.model_id = @ModelId)
            """,
            new { SourceId = sourceId, ModelId = modelId },
            cancellationToken: cancellationToken));
    }

    /// <summary>
    /// Writes the vectors of chunks in one transaction. A chunk that already has a vector of the model keeps it.
    /// </summary>
    /// <param name="modelId">The model.</param>
    /// <param name="vectors">The chunks and their vectors.</param>
    /// <param name="cancellationToken">The token that cancels the query.</param>
    /// <returns>A task that completes after the commit.</returns>
    public async Task AddEmbeddingsAsync(short modelId, IReadOnlyList<(long ChunkId, float[] Vector)> vectors, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(vectors);
        await using NpgsqlConnection connection = await database.OpenAsync(cancellationToken);
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO chunk_embedding (chunk_id, model_id, embedding)
            VALUES (@ChunkId, @ModelId, @Vector::real[]::vector)
            ON CONFLICT DO NOTHING
            """,
            vectors.Select(item => new { item.ChunkId, ModelId = modelId, item.Vector }),
            transaction, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
    }

}

/// <summary>
/// An embedding model as the database stores it, with its probe.
/// </summary>
internal sealed class StoredModel
{
    /// <summary>
    /// Gets the key of the model.
    /// </summary>
    public short ModelId { get; init; }

    /// <summary>
    /// Gets the number of dimensions.
    /// </summary>
    public int Dims { get; init; }

    /// <summary>
    /// Gets the text of the probe.
    /// </summary>
    public string ProbeText { get; init; } = string.Empty;

    /// <summary>
    /// Gets the stored vector of the probe.
    /// </summary>
    public float[] ProbeEmbedding { get; init; } = [];
}

/// <summary>
/// A chunk that waits for its vector.
/// </summary>
public sealed class PendingChunk
{
    /// <summary>
    /// Gets the key of the chunk.
    /// </summary>
    public long ChunkId { get; init; }

    /// <summary>
    /// Gets the path of the section.
    /// </summary>
    public string Heading { get; init; } = string.Empty;

    /// <summary>
    /// Gets the text of the chunk.
    /// </summary>
    public string Body { get; init; } = string.Empty;
}
