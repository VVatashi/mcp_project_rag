using Dapper;

using Npgsql;

using ProjectRag.Core.Storage;

namespace ProjectRag.Core.Documents;

/// <summary>
/// Reads and writes the documents and their chunks.
/// </summary>
/// <param name="database">The database.</param>
public sealed class DocumentStore(Database database)
{
    /// <summary>The most characters that <see cref="GetContentAsync"/> returns at once.</summary>
    public const int MaxContentLength = 100_000;

    private const string SummarySql = """
        SELECT d.document_id, d.source_id, s.name AS source, d.path, d.title, d.size_bytes, d.modified_at, d.indexed_at,
               length(d.content) AS content_length,
               (SELECT count(*) FROM chunk c WHERE c.document_id = d.document_id) AS chunk_count
        FROM document d
        JOIN source s USING (source_id)
        """;

    // A text matches by a part of the path or the title, or by the similarity of trigrams (operator <% of pg_trgm).
    private const string FilterSql = """
        WHERE (cardinality(@SourceIds) = 0 OR d.source_id = ANY (@SourceIds))
          AND (@Text = '' OR d.path ILIKE @Like OR d.title ILIKE @Like OR @Text <% d.path OR @Text <% d.title)
        """;

    /// <summary>
    /// Lists documents.
    /// </summary>
    /// <param name="sourceIds">The sources, or an empty list for all sources.</param>
    /// <param name="text">A part of the path or the title, or an empty text for all documents. The best matches come first.</param>
    /// <param name="offset">The number of documents to skip.</param>
    /// <param name="limit">The most documents to return.</param>
    /// <param name="cancellationToken">The token that cancels the query.</param>
    /// <returns>The documents and the number of all matches.</returns>
    public async Task<(IReadOnlyList<DocumentSummary> Items, long Total)> ListAsync(IReadOnlyCollection<int> sourceIds, string text, int offset, int limit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sourceIds);
        ArgumentNullException.ThrowIfNull(text);
        text = text.Trim();
        object parameters = new { SourceIds = sourceIds.ToArray(), Text = text, Like = $"%{EscapeLike(text)}%", Offset = offset, Limit = limit };
        await using NpgsqlConnection connection = await database.OpenAsync(cancellationToken);
        IEnumerable<DocumentSummary> rows = await connection.QueryAsync<DocumentSummary>(new CommandDefinition(
            $"""
            {SummarySql}
            {FilterSql}
            ORDER BY greatest(word_similarity(@Text, d.path), word_similarity(@Text, d.title)) DESC, s.name, d.path
            OFFSET @Offset LIMIT @Limit
            """,
            parameters, cancellationToken: cancellationToken));
        long total = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            $"SELECT count(*) FROM document d {FilterSql}", parameters, cancellationToken: cancellationToken));
        return ([.. rows], total);
    }

    /// <summary>
    /// Reads a document without its content.
    /// </summary>
    /// <param name="documentId">The key of the document.</param>
    /// <param name="cancellationToken">The token that cancels the query.</param>
    /// <returns>The document, or <see langword="null"/> when there is none.</returns>
    public async Task<DocumentSummary?> GetAsync(long documentId, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await database.OpenAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<DocumentSummary>(new CommandDefinition(
            $"{SummarySql} WHERE d.document_id = @DocumentId", new { DocumentId = documentId }, cancellationToken: cancellationToken));
    }

    /// <summary>
    /// Finds a document by the name of its source and its path.
    /// </summary>
    /// <param name="source">The name of the source.</param>
    /// <param name="path">The path relative to the folder of the source. Backslashes count as forward slashes.</param>
    /// <param name="cancellationToken">The token that cancels the query.</param>
    /// <returns>The document, or <see langword="null"/> when there is none.</returns>
    public async Task<DocumentSummary?> FindAsync(string source, string path, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(path);
        await using NpgsqlConnection connection = await database.OpenAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<DocumentSummary>(new CommandDefinition(
            $"{SummarySql} WHERE s.name = @Source AND d.path = @Path",
            new { Source = source, Path = path.Replace('\\', '/').TrimStart('/') }, cancellationToken: cancellationToken));
    }

    /// <summary>
    /// Reads a part of the content of a document.
    /// </summary>
    /// <param name="document">The document.</param>
    /// <param name="offset">The index of the first character.</param>
    /// <param name="length">The most characters, up to <see cref="MaxContentLength"/>.</param>
    /// <param name="cancellationToken">The token that cancels the query.</param>
    /// <returns>The part of the content.</returns>
    public async Task<DocumentContent> GetContentAsync(DocumentSummary document, int offset, int length, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        await using NpgsqlConnection connection = await database.OpenAsync(cancellationToken);
        // The offsets of the chunks count UTF-16 units and length() of PostgreSQL counts code points, so the slice happens here.
        string content = await connection.ExecuteScalarAsync<string>(new CommandDefinition(
            "SELECT content FROM document WHERE document_id = @DocumentId", new { document.DocumentId }, cancellationToken: cancellationToken)) ?? string.Empty;
        int start = Math.Clamp(offset, 0, content.Length);
        int end = start + Math.Clamp(length, 0, Math.Min(MaxContentLength, content.Length - start));
        return new DocumentContent(document, content[start..end], start, content.Length);
    }

    /// <summary>
    /// Reads the chunks of a document.
    /// </summary>
    /// <param name="documentId">The key of the document.</param>
    /// <param name="modelId">The model for <see cref="ChunkView.HasEmbedding"/>, or <see langword="null"/> when no model is ready.</param>
    /// <param name="cancellationToken">The token that cancels the query.</param>
    /// <returns>The chunks in their order.</returns>
    public async Task<IReadOnlyList<ChunkView>> GetChunksAsync(long documentId, short? modelId, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await database.OpenAsync(cancellationToken);
        IEnumerable<ChunkView> rows = await connection.QueryAsync<ChunkView>(new CommandDefinition(
            """
            SELECT c.chunk_id, c.ordinal, c.heading, c.body, c.char_start, c.char_end,
                   EXISTS (SELECT 1 FROM chunk_embedding e WHERE e.chunk_id = c.chunk_id AND e.model_id = @ModelId) AS has_embedding
            FROM chunk c
            WHERE c.document_id = @DocumentId
            ORDER BY c.ordinal
            """,
            new { DocumentId = documentId, ModelId = modelId ?? 0 }, cancellationToken: cancellationToken));
        return [.. rows];
    }

    /// <summary>
    /// Reads the state of the files of a source at the last run of the indexer.
    /// </summary>
    internal async Task<Dictionary<string, DocumentFileState>> GetFileStatesAsync(int sourceId, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await database.OpenAsync(cancellationToken);
        IEnumerable<DocumentFileState> rows = await connection.QueryAsync<DocumentFileState>(new CommandDefinition(
            "SELECT document_id, path, size_bytes, modified_at, content_hash, parser_version FROM document WHERE source_id = @SourceId",
            new { SourceId = sourceId }, cancellationToken: cancellationToken));
        return rows.ToDictionary(static row => row.Path, StringComparer.Ordinal);
    }

    /// <summary>
    /// Writes the new size and time of a file whose bytes did not change.
    /// </summary>
    internal async Task TouchAsync(long documentId, long sizeBytes, DateTime modifiedAt, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await database.OpenAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE document SET size_bytes = @SizeBytes, modified_at = @ModifiedAt WHERE document_id = @DocumentId",
            new { DocumentId = documentId, SizeBytes = sizeBytes, ModifiedAt = modifiedAt }, cancellationToken: cancellationToken));
    }

    /// <summary>
    /// Writes a document and replaces its chunks in one transaction.
    /// </summary>
    /// <remarks>
    /// A new chunk with the text of a stored chunk takes over its vectors, so an edit of a long document embeds only the changed chunks.
    /// The search covers all documents, so a chunk that another document repeats also needs no new vector.
    /// </remarks>
    /// <returns>The number of vectors that the new chunks took over.</returns>
    internal async Task<int> SaveAsync(int sourceId, string path, DocumentText text, DocumentFile file, IReadOnlyList<(TextChunk Chunk, string Hash)> chunks, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await database.OpenAsync(cancellationToken);
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);
        long documentId = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            """
            INSERT INTO document (source_id, path, title, content, content_hash, parser_version, size_bytes, modified_at)
            VALUES (@SourceId, @Path, @Title, @Content, @ContentHash, @ParserVersion, @SizeBytes, @ModifiedAt)
            ON CONFLICT (source_id, path) DO UPDATE
            SET title = EXCLUDED.title, content = EXCLUDED.content, content_hash = EXCLUDED.content_hash, parser_version = EXCLUDED.parser_version,
                size_bytes = EXCLUDED.size_bytes, modified_at = EXCLUDED.modified_at, indexed_at = now()
            RETURNING document_id
            """,
            new { SourceId = sourceId, Path = path, text.Title, text.Content, file.ContentHash, file.ParserVersion, file.SizeBytes, file.ModifiedAt },
            transaction, cancellationToken: cancellationToken));

        long[] oldIds = [.. await connection.QueryAsync<long>(new CommandDefinition(
            "SELECT chunk_id FROM chunk WHERE document_id = @DocumentId", new { DocumentId = documentId }, transaction, cancellationToken: cancellationToken))];

        long[] newIds = [.. await connection.QueryAsync<long>(new CommandDefinition(
            """
            INSERT INTO chunk (document_id, ordinal, heading, body, char_start, char_end, body_hash)
            SELECT @DocumentId, * FROM unnest(@Ordinals, @Headings, @Bodies, @Starts, @Ends, @Hashes)
            RETURNING chunk_id
            """,
            new
            {
                DocumentId = documentId,
                Ordinals = chunks.Select(static item => item.Chunk.Ordinal).ToArray(),
                Headings = chunks.Select(static item => item.Chunk.Heading).ToArray(),
                Bodies = chunks.Select(static item => item.Chunk.Body).ToArray(),
                Starts = chunks.Select(static item => item.Chunk.Start).ToArray(),
                Ends = chunks.Select(static item => item.Chunk.End).ToArray(),
                Hashes = chunks.Select(static item => item.Hash).ToArray(),
            },
            transaction, cancellationToken: cancellationToken))];

        int reused = newIds.Length == 0 ? 0 : await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO chunk_embedding (chunk_id, model_id, embedding)
            SELECT DISTINCT ON (n.chunk_id, e.model_id) n.chunk_id, e.model_id, e.embedding
            FROM chunk n
            JOIN chunk o ON o.body_hash = n.body_hash AND o.chunk_id <> ALL (@NewIds)
            JOIN chunk_embedding e ON e.chunk_id = o.chunk_id
            WHERE n.chunk_id = ANY (@NewIds)
            ORDER BY n.chunk_id, e.model_id
            ON CONFLICT DO NOTHING
            """,
            new { NewIds = newIds }, transaction, cancellationToken: cancellationToken));

        if (oldIds.Length > 0)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM chunk WHERE chunk_id = ANY (@OldIds)", new { OldIds = oldIds }, transaction, cancellationToken: cancellationToken));
        }

        await transaction.CommitAsync(cancellationToken);
        return reused;
    }

    /// <summary>
    /// Deletes the documents of a source whose files are gone.
    /// </summary>
    /// <returns>The number of deleted documents.</returns>
    internal async Task<int> DeleteMissingAsync(int sourceId, IReadOnlyCollection<string> paths, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await database.OpenAsync(cancellationToken);
        return await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM document WHERE source_id = @SourceId AND NOT (path = ANY (@Paths))",
            new { SourceId = sourceId, Paths = paths.ToArray() }, cancellationToken: cancellationToken));
    }

    private static string EscapeLike(string text) => text.Replace(@"\", @"\\", StringComparison.Ordinal).Replace("%", @"\%", StringComparison.Ordinal).Replace("_", @"\_", StringComparison.Ordinal);
}

/// <summary>
/// The facts of a file that the indexer stores with its document.
/// </summary>
/// <param name="ContentHash">The SHA-256 of the bytes in hexadecimal.</param>
/// <param name="ParserVersion">The version of the reader and the chunker.</param>
/// <param name="SizeBytes">The size in bytes.</param>
/// <param name="ModifiedAt">The time of the last write in UTC, cut to microseconds like the timestamps of PostgreSQL.</param>
internal sealed record DocumentFile(string ContentHash, int ParserVersion, long SizeBytes, DateTime ModifiedAt);
