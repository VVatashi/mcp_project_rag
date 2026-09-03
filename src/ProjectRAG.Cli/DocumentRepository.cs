using System.Collections.Immutable;
using System.Data;
using System.Text;
using Dapper;
using Npgsql;

namespace ProjectRAG.Cli;

/// <summary>Settings of the database, bound from the Database section of appsettings.json.</summary>
public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    /// <summary>The text search configuration of the full text index. The migration builds the tsvector column with it, so a search has to use the same one.
    /// The simple configuration keeps every word as it stands, which suits a document set that mixes languages.</summary>
    public const string TextSearchConfiguration = "simple";

    public string ConnectionString { get; set; } = "Host=localhost;Port=5432;Database=projectrag;Username=projectrag;Password=projectrag";
}

/// <summary>A document the caller wants to store. The database gives it an id, a uuid and a timestamp.</summary>
public readonly record struct NewDocument(string Path, string Title, string Hash, string Content);

/// <summary>A row of the documents table.</summary>
public sealed record StoredDocument(long Id, Guid Uuid, string Title, string Path, string Hash, string Content, DateTime IndexedAt);

/// <summary>A row of the chunks table. The embedding stays in the database because only the search reads it.</summary>
public sealed record StoredChunk(long Id, long DocumentId, int Ordinal, ImmutableArray<string> HeadingPath, int StartLine, int EndLine, string Content, int TokenCount, DateTime CreatedAt);

/// <summary>One chunk a search found, with the document it belongs to and the score of the match.</summary>
public sealed record SearchHit(long ChunkId, long DocumentId, string Path, string Title, ImmutableArray<string> HeadingPath, int StartLine, int EndLine, string Content, double Score);

/// <summary>Stores documents and their chunks. The interface looks like a collection and keeps every SQL statement inside the implementation.</summary>
public interface IDocumentRepository
{
    /// <summary>Reads one document by its path. Returns null when the path is not indexed.</summary>
    Task<StoredDocument?> FindAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>Reads one document by its id. Returns null when the id is gone.</summary>
    Task<StoredDocument?> GetAsync(long id, CancellationToken cancellationToken = default);

    /// <summary>Reads a page of documents ordered by path.</summary>
    Task<ImmutableArray<StoredDocument>> BrowseAsync(int skip, int take, CancellationToken cancellationToken = default);

    /// <summary>Adds the document or replaces the one that already holds that path. Returns the stored row.</summary>
    Task<StoredDocument> SaveAsync(NewDocument document, CancellationToken cancellationToken = default);

    /// <summary>Writes the document and its chunks in one transaction. Either both land or neither does, so a stored hash always describes stored chunks.</summary>
    Task<StoredDocument> SaveWithChunksAsync(NewDocument document, IReadOnlyList<IndexedChunk> chunks, CancellationToken cancellationToken = default);

    /// <summary>Removes the document and its chunks. Returns false when the id is gone.</summary>
    Task<bool> RemoveAsync(long id, CancellationToken cancellationToken = default);

    /// <summary>Reads the chunks of one document in reading order.</summary>
    Task<ImmutableArray<StoredChunk>> ReadChunksAsync(long documentId, CancellationToken cancellationToken = default);

    /// <summary>Counts the chunks of one document.</summary>
    Task<int> CountChunksAsync(long documentId, CancellationToken cancellationToken = default);

    /// <summary>Drops the chunks of the document and writes the new ones. The ordinal of a chunk is its place in the list.</summary>
    Task ReplaceChunksAsync(long documentId, IReadOnlyList<IndexedChunk> chunks, CancellationToken cancellationToken = default);

    /// <summary>Returns the chunks that match the words of the query, best match first.</summary>
    Task<ImmutableArray<SearchHit>> SearchByTextAsync(string query, int limit, CancellationToken cancellationToken = default);

    /// <summary>Returns the chunks whose embedding lies closest to the vector, closest first.</summary>
    Task<ImmutableArray<SearchHit>> SearchByVectorAsync(float[] vector, int limit, CancellationToken cancellationToken = default);
}

/// <summary>Reads a text[] column into an immutable array. Without the handler Dapper spreads the array over many parameters.</summary>
internal sealed class HeadingPathHandler : SqlMapper.TypeHandler<ImmutableArray<string>>
{
    public override ImmutableArray<string> Parse(object value) => value is string[] items ? [.. items] : [];

    public override void SetValue(IDbDataParameter parameter, ImmutableArray<string> value) => parameter.Value = value.IsDefault ? Array.Empty<string>() : value.ToArray();
}

/// <summary>Writes an embedding as a real[] parameter. Every statement casts it to vector, the cast pgvector installs.</summary>
internal sealed class EmbeddingHandler : SqlMapper.TypeHandler<float[]>
{
    public override float[] Parse(object value) => value is float[] numbers ? numbers : [];

    public override void SetValue(IDbDataParameter parameter, float[]? value) => parameter.Value = value ?? [];
}

/// <summary>Stores documents and chunks in PostgreSQL.</summary>
public sealed class DocumentRepository(NpgsqlDataSource source) : IDocumentRepository
{
    private const string DocumentColumns = "id, uuid, title, path, hash, content, indexed_at";

    private const string ChunkColumns = "id, document_id, ordinal, heading_path, start_line, end_line, content, token_count, created_at";

    private const string HitColumns = "c.id AS chunk_id, c.document_id, d.path, d.title, c.heading_path, c.start_line, c.end_line, c.content";

    private const string SearchByText = $"""
        WITH q AS (SELECT websearch_to_tsquery('{DatabaseOptions.TextSearchConfiguration}', @Query) AS query)
        SELECT {HitColumns}, ts_rank_cd(c.content_tsv, q.query)::float8 AS score
        FROM chunks c
        JOIN documents d ON d.id = c.document_id, q
        WHERE c.content_tsv @@ q.query
        ORDER BY score DESC
        LIMIT @Limit;
        """;

    private const string SearchByVector = $"""
        SELECT {HitColumns}, (1 - (c.embedding <=> @Embedding::vector))::float8 AS score
        FROM chunks c
        JOIN documents d ON d.id = c.document_id
        ORDER BY c.embedding <=> @Embedding::vector
        LIMIT @Limit;
        """;

    private const string SaveDocument = $"""
        INSERT INTO documents (path, title, hash, content)
        VALUES (@Path, @Title, @Hash, @Content)
        ON CONFLICT (lower(path)) DO UPDATE SET path = EXCLUDED.path, title = EXCLUDED.title, hash = EXCLUDED.hash, content = EXCLUDED.content, indexed_at = now()
        RETURNING {DocumentColumns};
        """;

    private const string DeleteChunks = "DELETE FROM chunks WHERE document_id = @DocumentId;";

    private const string InsertHead = "INSERT INTO chunks (document_id, ordinal, heading_path, headings, start_line, end_line, content, embedding, token_count) VALUES ";

    /// <summary>How many chunks one insert statement carries. One statement is one round trip, and every chunk costs eight parameters.</summary>
    private const int InsertBatch = 100;

    private readonly NpgsqlDataSource _source = source;

    /// <summary>Dapper reads its settings and its type handlers from static fields, so this changes the whole process.
    /// The static constructor holds it because every caller reaches the database through this class, and no caller can forget it.</summary>
    static DocumentRepository()
    {
        DefaultTypeMap.MatchNamesWithUnderscores = true;
        SqlMapper.AddTypeHandler(new HeadingPathHandler());
        SqlMapper.AddTypeHandler(new EmbeddingHandler());
    }

    public async Task<StoredDocument?> FindAsync(string path, CancellationToken cancellationToken = default)
    {
        await using NpgsqlConnection connection = await _source.OpenConnectionAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<StoredDocument>(Command($"SELECT {DocumentColumns} FROM documents WHERE lower(path) = lower(@Path);", new { Path = path }, cancellationToken));
    }

    public async Task<StoredDocument?> GetAsync(long id, CancellationToken cancellationToken = default)
    {
        await using NpgsqlConnection connection = await _source.OpenConnectionAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<StoredDocument>(Command($"SELECT {DocumentColumns} FROM documents WHERE id = @Id;", new { Id = id }, cancellationToken));
    }

    public async Task<ImmutableArray<StoredDocument>> BrowseAsync(int skip, int take, CancellationToken cancellationToken = default)
    {
        await using NpgsqlConnection connection = await _source.OpenConnectionAsync(cancellationToken);
        IEnumerable<StoredDocument> documents = await connection.QueryAsync<StoredDocument>(
            Command($"SELECT {DocumentColumns} FROM documents ORDER BY path OFFSET @Skip LIMIT @Take;", new { Skip = skip, Take = take }, cancellationToken));

        return [.. documents];
    }

    public async Task<StoredDocument> SaveAsync(NewDocument document, CancellationToken cancellationToken = default)
    {
        await using NpgsqlConnection connection = await _source.OpenConnectionAsync(cancellationToken);
        return await connection.QuerySingleAsync<StoredDocument>(Command(SaveDocument, document, cancellationToken));
    }

    public async Task<StoredDocument> SaveWithChunksAsync(NewDocument document, IReadOnlyList<IndexedChunk> chunks, CancellationToken cancellationToken = default)
    {
        await using NpgsqlConnection connection = await _source.OpenConnectionAsync(cancellationToken);
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);

        StoredDocument stored = await connection.QuerySingleAsync<StoredDocument>(new CommandDefinition(SaveDocument, document, transaction, cancellationToken: cancellationToken));
        await WriteChunksAsync(connection, transaction, stored.Id, chunks, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return stored;
    }

    public async Task<bool> RemoveAsync(long id, CancellationToken cancellationToken = default)
    {
        await using NpgsqlConnection connection = await _source.OpenConnectionAsync(cancellationToken);
        return await connection.ExecuteAsync(Command("DELETE FROM documents WHERE id = @Id;", new { Id = id }, cancellationToken)) > 0;
    }

    public async Task<ImmutableArray<StoredChunk>> ReadChunksAsync(long documentId, CancellationToken cancellationToken = default)
    {
        await using NpgsqlConnection connection = await _source.OpenConnectionAsync(cancellationToken);
        IEnumerable<StoredChunk> chunks = await connection.QueryAsync<StoredChunk>(
            Command($"SELECT {ChunkColumns} FROM chunks WHERE document_id = @DocumentId ORDER BY ordinal;", new { DocumentId = documentId }, cancellationToken));

        return [.. chunks];
    }

    public async Task<int> CountChunksAsync(long documentId, CancellationToken cancellationToken = default)
    {
        await using NpgsqlConnection connection = await _source.OpenConnectionAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<int>(Command("SELECT count(*) FROM chunks WHERE document_id = @DocumentId;", new { DocumentId = documentId }, cancellationToken));
    }

    public async Task ReplaceChunksAsync(long documentId, IReadOnlyList<IndexedChunk> chunks, CancellationToken cancellationToken = default)
    {
        await using NpgsqlConnection connection = await _source.OpenConnectionAsync(cancellationToken);
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);

        await WriteChunksAsync(connection, transaction, documentId, chunks, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task WriteChunksAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long documentId, IReadOnlyList<IndexedChunk> chunks, CancellationToken cancellationToken)
    {
        await connection.ExecuteAsync(new CommandDefinition(DeleteChunks, new { DocumentId = documentId }, transaction, cancellationToken: cancellationToken));

        for (int start = 0; start < chunks.Count; start += InsertBatch)
        {
            (string sql, DynamicParameters parameters) = Insert(documentId, chunks, start, Math.Min(InsertBatch, chunks.Count - start));
            await connection.ExecuteAsync(new CommandDefinition(sql, parameters, transaction, cancellationToken: cancellationToken));
        }
    }

    /// <summary>Builds one insert statement for a run of chunks. The ordinal of a chunk is its place in the list.</summary>
    private static (string Sql, DynamicParameters Parameters) Insert(long documentId, IReadOnlyList<IndexedChunk> chunks, int start, int size)
    {
        StringBuilder sql = new(InsertHead);
        DynamicParameters parameters = new();
        parameters.Add("DocumentId", documentId);

        for (int row = 0; row < size; row++)
        {
            int ordinal = start + row;
            Chunk chunk = chunks[ordinal].Chunk;

            if (row > 0)
                sql.Append(", ");

            sql.Append($"(@DocumentId, @Ordinal{row}, @HeadingPath{row}, @Headings{row}, @StartLine{row}, @EndLine{row}, @Content{row}, @Embedding{row}::vector, @TokenCount{row})");

            parameters.Add($"Ordinal{row}", ordinal);
            parameters.Add($"HeadingPath{row}", chunk.HeadingPath);
            parameters.Add($"Headings{row}", chunk.HeadingPath.IsDefaultOrEmpty ? string.Empty : string.Join(' ', chunk.HeadingPath));
            parameters.Add($"StartLine{row}", chunk.Line);
            parameters.Add($"EndLine{row}", chunk.EndLine);
            parameters.Add($"Content{row}", chunk.Text);
            parameters.Add($"Embedding{row}", chunks[ordinal].Vector);
            parameters.Add($"TokenCount{row}", chunk.TokenCount);
        }

        return (sql.Append(';').ToString(), parameters);
    }

    public async Task<ImmutableArray<SearchHit>> SearchByTextAsync(string query, int limit, CancellationToken cancellationToken = default)
    {
        await using NpgsqlConnection connection = await _source.OpenConnectionAsync(cancellationToken);
        IEnumerable<SearchHit> hits = await connection.QueryAsync<SearchHit>(Command(SearchByText, new { Query = query, Limit = limit }, cancellationToken));

        return [.. hits];
    }

    public async Task<ImmutableArray<SearchHit>> SearchByVectorAsync(float[] vector, int limit, CancellationToken cancellationToken = default)
    {
        await using NpgsqlConnection connection = await _source.OpenConnectionAsync(cancellationToken);
        IEnumerable<SearchHit> hits = await connection.QueryAsync<SearchHit>(Command(SearchByVector, new { Embedding = vector, Limit = limit }, cancellationToken));

        return [.. hits];
    }

    private static CommandDefinition Command(string sql, object parameters, CancellationToken cancellationToken) => new(sql, parameters, cancellationToken: cancellationToken);
}
