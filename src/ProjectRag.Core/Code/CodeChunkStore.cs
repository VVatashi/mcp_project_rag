using System.Globalization;

using Dapper;

using Npgsql;

using ProjectRag.Core.Embeddings;
using ProjectRag.Core.Search;
using ProjectRag.Core.Storage;

namespace ProjectRag.Core.Code;

/// <summary>
/// The hybrid search over the chunks of code files in SQL: the vector search over HNSW and the token search over <c>code_tsv</c>, fused by their ranks.
/// </summary>
/// <remarks>
/// <para>
/// The fusion is the one of <see cref="SearchStore"/>: a chunk scores <c>w / (k + vector rank) + (1 - w) / (k + text rank)</c>.
/// The query keeps the chunks of the kinds <c>code</c> and <c>config</c>, so the documentation never takes a place of the candidates.
/// </para>
/// <para>
/// The token search joins the tokens of the query with OR, because a description rarely holds all identifiers of a chunk.
/// The text of the query goes into the query of the database as quoted lexemes of letters and digits only.
/// </para>
/// </remarks>
/// <param name="database">The database.</param>
public sealed class CodeChunkStore(Database database)
{
    // The filters of both legs. A path filter arrives as a list of document keys, which the service builds from the pattern.
    private const string FilterSql = """
        (@SourceId::integer IS NULL OR d.source_id = @SourceId)
        AND (@Language::text IS NULL OR d.language = @Language)
        AND (@DocumentIds::bigint[] IS NULL OR d.document_id = ANY (@DocumentIds))
        AND (@IncludeTests OR NOT EXISTS (SELECT 1 FROM code_file_project fp JOIN code_project p ON p.project_id = fp.project_id WHERE fp.document_id = d.document_id AND fp.is_owner AND p.is_test))
        """;

    /// <summary>
    /// Runs the hybrid search.
    /// </summary>
    /// <param name="model">The embedding model, or <see langword="null"/> for the token search only.</param>
    /// <param name="vector">The vector of the query, or <see langword="null"/> for the token search only.</param>
    /// <param name="terms">The tokens of the query, see <see cref="Chunks.IdentifierTokens.Terms"/>.</param>
    /// <param name="sourceId">The code source, or <see langword="null"/> for all code sources.</param>
    /// <param name="language">The language, or <see langword="null"/> for all languages.</param>
    /// <param name="documentIds">The documents to keep, or <see langword="null"/> for all documents.</param>
    /// <param name="includeTests"><see langword="false"/> drops the files whose owner project is a test project.</param>
    /// <param name="limit">The most hits.</param>
    /// <param name="options">The options of the fusion.</param>
    /// <param name="cancellationToken">The token that cancels the query.</param>
    /// <returns>The hits, the best first.</returns>
    public async Task<IReadOnlyList<CodeChunkHit>> SearchAsync(
        EmbeddingModel? model, float[]? vector, IReadOnlyList<string> terms, int? sourceId, string? language, long[]? documentIds, bool includeTests, int limit, SearchOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(terms);
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
                      AND c.kind <> 'prose'
                      AND {FilterSql}
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
                SELECT CASE WHEN @TsQuery = '' THEN NULL::tsquery ELSE to_tsquery('simple', @TsQuery) END AS tsq
            ),
            fts AS (
                SELECT c.chunk_id, row_number() OVER (ORDER BY ts_rank(c.code_tsv, q.tsq) DESC, c.chunk_id)::integer AS r
                FROM chunk c
                JOIN document d USING (document_id), q
                WHERE q.tsq IS NOT NULL AND numnode(q.tsq) > 0
                  AND c.code_tsv IS NOT NULL
                  AND c.code_tsv @@ q.tsq
                  AND c.kind <> 'prose'
                  AND {FilterSql}
                ORDER BY ts_rank(c.code_tsv, q.tsq) DESC, c.chunk_id
                LIMIT @Pool
            ),
            fused AS (
                SELECT coalesce(vec.chunk_id, fts.chunk_id) AS chunk_id, vec.r AS vector_rank, fts.r AS text_rank,
                       coalesce(@Weight / (@RrfK + vec.r), 0) + coalesce((1 - @Weight) / (@RrfK + fts.r), 0) AS score
                FROM vec FULL JOIN fts USING (chunk_id)
            )
            SELECT c.chunk_id, c.document_id, d.source_id, s.name AS source, d.path, d.language, c.heading, c.line_start, c.line_end, c.body AS text,
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
            await SearchStore.ConfigureVectorScanAsync(connection, transaction, options, cancellationToken);

        IEnumerable<CodeChunkHit> hits = await connection.QueryAsync<CodeChunkHit>(new CommandDefinition(
            sql,
            new
            {
                Vector = vector ?? [],
                TsQuery = TsQuery(terms),
                SourceId = sourceId,
                Language = language,
                DocumentIds = documentIds,
                IncludeTests = includeTests,
                options.Pool,
                Weight = Math.Clamp(options.VectorWeight, 0, 1),
                options.RrfK,
                Limit = limit,
            },
            transaction, cancellationToken: cancellationToken));
        return [.. hits];
    }

    /// <summary>
    /// Builds the text of a query of the database that joins quoted tokens with OR.
    /// </summary>
    /// <remarks>
    /// Only letters and digits stay in a token, so the text holds no operator and no quote of the query language.
    /// </remarks>
    /// <param name="terms">The tokens.</param>
    /// <returns>The text for <c>to_tsquery</c>, or an empty text when no token is left.</returns>
    public static string TsQuery(IReadOnlyList<string> terms)
    {
        ArgumentNullException.ThrowIfNull(terms);
        IEnumerable<string> safe = terms
            .Select(static term => new string([.. term.Where(char.IsLetterOrDigit)]))
            .Where(static term => term.Length > 0)
            .Distinct(StringComparer.Ordinal);
        return string.Join(" | ", safe.Select(static term => $"'{term}'"));
    }
}
