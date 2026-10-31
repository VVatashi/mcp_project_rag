using Dapper;

using Npgsql;

using ProjectRag.Core.Storage;

namespace ProjectRag.Core.Documents;

/// <summary>
/// Reads the structure of the stored documents: the sections, a chunk with its neighbors, and the links between chunks.
/// </summary>
/// <remarks>
/// A link resolves at the time of the query. Its target document has the path of the link in the same source, or the document of the link.
/// The anchor gives an offset in that document, and the target chunk is the first chunk that ends after the offset.
/// A heading has its anchor before its first chunk, so a link to a heading reaches the first chunk of the section.
/// </remarks>
/// <param name="database">The database.</param>
public sealed class StructureStore(Database database)
{
    /// <summary>The most backlinks of a chunk that a read returns.</summary>
    public const int MaxBacklinks = 50;

    /// <summary>
    /// Reads the sections of a document in their order.
    /// </summary>
    /// <param name="documentId">The key of the document.</param>
    /// <param name="cancellationToken">The token that cancels the query.</param>
    /// <returns>The sections.</returns>
    public async Task<IReadOnlyList<SectionView>> GetSectionsAsync(long documentId, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await database.OpenAsync(cancellationToken);
        IEnumerable<SectionView> rows = await connection.QueryAsync<SectionView>(new CommandDefinition(
            """
            SELECT s.section_id, s.parent_id, s.ordinal, s.level, s.number, s.title, s.path, s.char_start, s.char_end, s.page_start, s.page_end,
                   (SELECT count(*) FROM chunk c WHERE c.section_id = s.section_id) AS chunk_count
            FROM section s
            WHERE s.document_id = @DocumentId
            ORDER BY s.ordinal
            """,
            new { DocumentId = documentId }, cancellationToken: cancellationToken));
        return [.. rows];
    }

    /// <summary>
    /// Reads a chunk with its section, its neighbors, its links and the links to it.
    /// </summary>
    /// <param name="chunkId">The key of the chunk.</param>
    /// <param name="modelId">The model for <see cref="ChunkDetail.HasEmbedding"/>, or <see langword="null"/> when no model is ready.</param>
    /// <param name="cancellationToken">The token that cancels the queries.</param>
    /// <returns>The chunk, or <see langword="null"/> when there is none.</returns>
    public async Task<ChunkDetail?> GetChunkAsync(long chunkId, short? modelId, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await database.OpenAsync(cancellationToken);
        ChunkDetail? chunk = await connection.QuerySingleOrDefaultAsync<ChunkDetail>(new CommandDefinition(
            """
            SELECT c.chunk_id, c.document_id, s.name AS source, d.path, d.title, c.ordinal, c.heading, c.body, c.char_start, c.char_end,
                   c.page_start, c.page_end, c.overlap_chars, c.section_id, sec.number AS section_number,
                   EXISTS (SELECT 1 FROM chunk_embedding e WHERE e.chunk_id = c.chunk_id AND e.model_id = @ModelId) AS has_embedding,
                   (SELECT p.chunk_id FROM chunk p WHERE p.document_id = c.document_id AND p.ordinal = c.ordinal - 1) AS previous_chunk_id,
                   (SELECT n.chunk_id FROM chunk n WHERE n.document_id = c.document_id AND n.ordinal = c.ordinal + 1) AS next_chunk_id
            FROM chunk c
            JOIN document d USING (document_id)
            JOIN source s USING (source_id)
            LEFT JOIN section sec ON sec.section_id = c.section_id
            WHERE c.chunk_id = @ChunkId
            """,
            new { ChunkId = chunkId, ModelId = modelId ?? 0 }, cancellationToken: cancellationToken));
        if (chunk is null)
            return null;

        chunk.Links = [.. await connection.QueryAsync<ChunkLinkView>(new CommandDefinition(
            """
            SELECT l.kind, l.label, l.target_path, l.target_anchor, td.document_id AS target_document_id,
                   tc.chunk_id AS target_chunk_id, tc.heading AS target_heading, tc.page_start AS target_page
            FROM chunk_link l
            JOIN chunk c ON c.chunk_id = l.chunk_id
            JOIN document d ON d.document_id = c.document_id
            LEFT JOIN document td ON td.source_id = d.source_id AND td.path = coalesce(l.target_path, d.path)
            LEFT JOIN anchor a ON a.document_id = td.document_id AND a.name = l.target_anchor
            LEFT JOIN LATERAL (
                SELECT t.chunk_id, t.heading, t.page_start
                FROM chunk t
                WHERE t.document_id = td.document_id
                  AND (l.target_anchor IS NULL OR a.char_offset IS NOT NULL)
                  AND t.char_end > coalesce(a.char_offset, 0)
                ORDER BY t.char_start
                LIMIT 1
            ) tc ON true
            WHERE l.chunk_id = @ChunkId
            ORDER BY l.ordinal
            """,
            new { ChunkId = chunkId }, cancellationToken: cancellationToken))];

        // A link reaches this chunk when its anchor lies after the end of the previous chunk and before the end of this chunk.
        chunk.Backlinks = [.. await connection.QueryAsync<ChunkBacklinkView>(new CommandDefinition(
            """
            WITH target AS (
                SELECT c.document_id, d.source_id, d.path, c.char_end,
                       coalesce((SELECT max(p.char_end) FROM chunk p WHERE p.document_id = c.document_id AND p.ordinal < c.ordinal), 0) AS previous_end
                FROM chunk c JOIN document d USING (document_id)
                WHERE c.chunk_id = @ChunkId
            )
            SELECT DISTINCT ON (l.chunk_id) l.chunk_id, sc.document_id, sd.path, sc.heading, l.label, l.kind
            FROM target t
            JOIN anchor a ON a.document_id = t.document_id AND a.char_offset >= t.previous_end AND a.char_offset < t.char_end
            JOIN chunk_link l ON l.target_anchor = a.name
            JOIN chunk sc ON sc.chunk_id = l.chunk_id
            JOIN document sd ON sd.document_id = sc.document_id
            WHERE sd.source_id = t.source_id AND coalesce(l.target_path, sd.path) = t.path AND l.chunk_id <> @ChunkId
            ORDER BY l.chunk_id
            LIMIT @Limit
            """,
            new { ChunkId = chunkId, Limit = MaxBacklinks }, cancellationToken: cancellationToken))];
        return chunk;
    }

    /// <summary>
    /// Finds an anchor of a document and the chunk at it.
    /// </summary>
    /// <param name="documentId">The key of the document.</param>
    /// <param name="name">The name of the anchor.</param>
    /// <param name="cancellationToken">The token that cancels the query.</param>
    /// <returns>The anchor, or <see langword="null"/> when the document has no such anchor.</returns>
    public async Task<AnchorView?> GetAnchorAsync(long documentId, string name, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await database.OpenAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<AnchorView>(new CommandDefinition(
            """
            SELECT a.name, a.char_offset,
                   (SELECT t.chunk_id FROM chunk t WHERE t.document_id = a.document_id AND t.char_end > a.char_offset ORDER BY t.char_start LIMIT 1) AS chunk_id
            FROM anchor a
            WHERE a.document_id = @DocumentId AND a.name = @Name
            """,
            new { DocumentId = documentId, Name = name }, cancellationToken: cancellationToken));
    }

    /// <summary>
    /// Finds the file of a document on the disk: the folder of its source and its path.
    /// </summary>
    /// <param name="documentId">The key of the document.</param>
    /// <param name="cancellationToken">The token that cancels the query.</param>
    /// <returns>The full path, or <see langword="null"/> when the document does not exist or its path leaves the folder of the source.</returns>
    public async Task<string?> GetFilePathAsync(long documentId, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await database.OpenAsync(cancellationToken);
        (string Root, string Path)? row = await connection.QuerySingleOrDefaultAsync<(string Root, string Path)?>(new CommandDefinition(
            "SELECT s.root_path, d.path FROM document d JOIN source s USING (source_id) WHERE d.document_id = @DocumentId",
            new { DocumentId = documentId }, cancellationToken: cancellationToken));
        if (row is not { } found)
            return null;

        // The stored path comes from the scan of the folder. The check still keeps a changed root from reaching other files.
        // The relative path works for a root such as "D:\" too, where a prefix with a separator would fail.
        string root = System.IO.Path.GetFullPath(found.Root);
        string full = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, found.Path));
        string relative = System.IO.Path.GetRelativePath(root, full);
        return relative == ".." || relative.StartsWith(".." + System.IO.Path.DirectorySeparatorChar, StringComparison.Ordinal) || System.IO.Path.IsPathRooted(relative) ? null : full;
    }
}
