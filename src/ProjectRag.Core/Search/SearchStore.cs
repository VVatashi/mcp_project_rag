using System.Globalization;

using Dapper;

using Npgsql;

using ProjectRag.Core.Embeddings;
using ProjectRag.Core.Storage;

namespace ProjectRag.Core.Search;

/// <summary>
/// The hybrid search in SQL: the vector search over HNSW and the full text search, fused by their ranks.
/// </summary>
/// <remarks>
/// <para>
/// The SQL follows the function <c>search_passages</c> of the project rag. The raw scores of the two legs are not comparable, but their ranks are.
/// The fusion gives a chunk <c>w / (k + vector rank) + (1 - w) / (k + text rank)</c>.
/// </para>
/// <para>
/// The number of the model and its dimensions go into the text of the query as literals. With a parameter the generic plan cannot prove
/// the condition of the partial index and falls back to a full scan. The filter by source runs after the walk of the index,
/// so the query turns on the iterative scan of pgvector: it walks on until it has enough rows that pass the filter.
/// </para>
/// <para>
/// The full text search joins the words of the query with OR. A question rarely holds all words of a chunk, and AND finds nothing most of the time.
/// </para>
/// </remarks>
/// <param name="database">The database.</param>
public sealed class SearchStore(Database database)
{
    /// <summary>
    /// Runs the hybrid search.
    /// </summary>
    /// <param name="model">The embedding model, or <see langword="null"/> for the full text search only.</param>
    /// <param name="vector">The vector of the query, or <see langword="null"/> for the full text search only.</param>
    /// <param name="query">The text of the query.</param>
    /// <param name="sourceIds">The sources, or an empty list for all sources.</param>
    /// <param name="categories">The categories of the documents, or an empty list for all categories.</param>
    /// <param name="limit">The most hits.</param>
    /// <param name="options">The options of the fusion.</param>
    /// <param name="cancellationToken">The token that cancels the query.</param>
    /// <returns>The hits, the best first.</returns>
    public async Task<IReadOnlyList<SearchHit>> SearchAsync(EmbeddingModel? model, float[]? vector, string query, IReadOnlyCollection<int> sourceIds, IReadOnlyCollection<string> categories, int limit, SearchOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(sourceIds);
        ArgumentNullException.ThrowIfNull(categories);
        ArgumentNullException.ThrowIfNull(options);
        bool hasVector = model is not null && vector is not null;
        string vectorSql = hasVector
            ? string.Create(CultureInfo.InvariantCulture, $"""
                SELECT e.chunk_id, row_number() OVER (ORDER BY e.distance, e.chunk_id)::integer AS r
                FROM (
                    SELECT ce.chunk_id, ce.embedding::halfvec({model!.Dims}) <=> @Vector::real[]::vector({model.Dims})::halfvec({model.Dims}) AS distance
                    FROM chunk_embedding ce
                    JOIN chunk c USING (chunk_id)
                    JOIN document d USING (document_id)
                    WHERE ce.model_id = {model.ModelId}
                      AND (cardinality(@SourceIds) = 0 OR d.source_id = ANY (@SourceIds))
                      AND (cardinality(@Categories) = 0 OR d.category = ANY (@Categories))
                    ORDER BY distance
                    LIMIT @Pool
                ) e
                """)
            : "SELECT NULL::bigint AS chunk_id, NULL::integer AS r WHERE false";

        string sql = $"""
            WITH vec AS (
                {vectorSql}
            ),
            q AS (
                SELECT replace(plainto_tsquery('russian', @Query)::text, '&', '|')::tsquery AS tsq
            ),
            fts AS (
                SELECT c.chunk_id, row_number() OVER (ORDER BY ts_rank(c.tsv, q.tsq) DESC, c.chunk_id)::integer AS r
                FROM chunk c
                JOIN document d USING (document_id), q
                WHERE numnode(q.tsq) > 0
                  AND c.tsv @@ q.tsq
                  AND (cardinality(@SourceIds) = 0 OR d.source_id = ANY (@SourceIds))
                  AND (cardinality(@Categories) = 0 OR d.category = ANY (@Categories))
                ORDER BY ts_rank(c.tsv, q.tsq) DESC, c.chunk_id
                LIMIT @Pool
            ),
            fused AS (
                SELECT coalesce(vec.chunk_id, fts.chunk_id) AS chunk_id, vec.r AS vector_rank, fts.r AS text_rank,
                       coalesce(@Weight / (@RrfK + vec.r), 0) + coalesce((1 - @Weight) / (@RrfK + fts.r), 0) AS score
                FROM vec FULL JOIN fts USING (chunk_id)
            )
            SELECT c.chunk_id, c.document_id, s.name AS source, d.category, d.path, d.title, c.heading, c.body, c.char_start, c.char_end,
                   c.page_start, c.page_end, c.section_id, (SELECT count(*) FROM chunk_link l WHERE l.chunk_id = c.chunk_id) AS link_count,
                   f.vector_rank, f.text_rank, f.score
            FROM fused f
            JOIN chunk c USING (chunk_id)
            JOIN document d USING (document_id)
            JOIN source s USING (source_id)
            ORDER BY f.score DESC, c.chunk_id
            LIMIT @Limit
            """;

        await using NpgsqlConnection connection = await database.OpenAsync(cancellationToken);
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);
        if (hasVector)
        {
            string settings = string.Create(CultureInfo.InvariantCulture,
                $"SET LOCAL hnsw.iterative_scan = 'relaxed_order'; SET LOCAL hnsw.ef_search = {Math.Clamp(options.Pool, 40, 1000)}");
            await connection.ExecuteAsync(new CommandDefinition(settings, transaction: transaction, cancellationToken: cancellationToken));
        }

        IEnumerable<SearchHit> hits = await connection.QueryAsync<SearchHit>(new CommandDefinition(
            sql,
            new
            {
                Vector = vector ?? [],
                Query = query,
                SourceIds = sourceIds.ToArray(),
                Categories = categories.ToArray(),
                options.Pool,
                Weight = Math.Clamp(options.VectorWeight, 0, 1),
                options.RrfK,
                Limit = limit,
            },
            transaction, cancellationToken: cancellationToken));
        return [.. hits];
    }

    /// <summary>
    /// Finds which of the categories have documents.
    /// </summary>
    /// <param name="categories">The categories.</param>
    /// <param name="cancellationToken">The token that cancels the query.</param>
    /// <returns>The categories with documents.</returns>
    public async Task<IReadOnlySet<string>> FindCategoriesAsync(IReadOnlyCollection<string> categories, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(categories);
        await using NpgsqlConnection connection = await database.OpenAsync(cancellationToken);
        IEnumerable<string> found = await connection.QueryAsync<string>(new CommandDefinition(
            "SELECT DISTINCT category FROM document WHERE category = ANY (@Categories)", new { Categories = categories.ToArray() }, cancellationToken: cancellationToken));
        return found.ToHashSet(StringComparer.Ordinal);
    }
}
