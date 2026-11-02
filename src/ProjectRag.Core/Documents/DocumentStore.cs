using System.Globalization;
using System.Text.RegularExpressions;

using Dapper;

using Npgsql;

using ProjectRag.Core.Sources;
using ProjectRag.Core.Storage;

namespace ProjectRag.Core.Documents;

/// <summary>
/// Reads and writes the documents and their chunks.
/// </summary>
/// <param name="database">The database.</param>
public sealed partial class DocumentStore(Database database)
{
    /// <summary>The most characters that <see cref="GetContentAsync"/> returns at once.</summary>
    public const int MaxContentLength = 100_000;

    // The time of a command that writes the chunks of a large document or copies or deletes their vectors.
    private const int LongCommandSeconds = 1800;

    private const string SummarySql = """
        SELECT d.document_id, d.source_id, s.name AS source, d.category, d.path, d.title, d.size_bytes, d.modified_at, d.indexed_at,
               length(d.content) AS content_length,
               (SELECT count(*) FROM chunk c WHERE c.document_id = d.document_id) AS chunk_count
        FROM document d
        JOIN source s USING (source_id)
        """;

    // A text matches by a part of the path or the title, or by the similarity of trigrams (operator <% of pg_trgm).
    private const string FilterSql = """
        WHERE (cardinality(@SourceIds) = 0 OR d.source_id = ANY (@SourceIds))
          AND (@Category = '' OR d.category = @Category)
          AND (@Text = '' OR d.path ILIKE @Like OR d.title ILIKE @Like OR @Text <% d.path OR @Text <% d.title)
        """;

    /// <summary>
    /// Lists documents.
    /// </summary>
    /// <param name="sourceIds">The sources, or an empty list for all sources.</param>
    /// <param name="category">The category, or an empty text for all categories.</param>
    /// <param name="text">A part of the path or the title, or an empty text for all documents. The best matches come first.</param>
    /// <param name="offset">The number of documents to skip.</param>
    /// <param name="limit">The most documents to return.</param>
    /// <param name="cancellationToken">The token that cancels the query.</param>
    /// <returns>The documents and the number of all matches.</returns>
    public async Task<(IReadOnlyList<DocumentSummary> Items, long Total)> ListAsync(IReadOnlyCollection<int> sourceIds, string category, string text, int offset, int limit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sourceIds);
        ArgumentNullException.ThrowIfNull(category);
        ArgumentNullException.ThrowIfNull(text);
        text = text.Trim();
        object parameters = new { SourceIds = sourceIds.ToArray(), Category = category.Trim(), Text = text, Like = $"%{EscapeLike(text)}%", Offset = offset, Limit = limit };
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
    /// Reads the categories of the documents with their counts.
    /// </summary>
    /// <param name="modelId">The model for <see cref="CategoryInfo.EmbeddedCount"/>, or <see langword="null"/> when no model is ready.</param>
    /// <param name="cancellationToken">The token that cancels the query.</param>
    /// <returns>The categories in the order of their names.</returns>
    public async Task<IReadOnlyList<CategoryInfo>> GetCategoriesAsync(short? modelId, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await database.OpenAsync(cancellationToken);
        IEnumerable<CategoryInfo> rows = await connection.QueryAsync<CategoryInfo>(new CommandDefinition(
            """
            SELECT d.category AS name, count(DISTINCT d.document_id) AS document_count, count(c.chunk_id) AS chunk_count,
                   count(c.chunk_id) FILTER (WHERE EXISTS (SELECT 1 FROM chunk_embedding e WHERE e.chunk_id = c.chunk_id AND e.model_id = @ModelId)) AS embedded_count
            FROM document d
            LEFT JOIN chunk c USING (document_id)
            GROUP BY d.category
            ORDER BY d.category
            """,
            new { ModelId = modelId ?? 0 }, cancellationToken: cancellationToken));
        return [.. rows];
    }

    /// <summary>
    /// Gives the documents of a source the categories of its rules. A change of the rules needs no new read of the files.
    /// </summary>
    /// <param name="source">The source with its rules.</param>
    /// <param name="cancellationToken">The token that cancels the queries.</param>
    /// <returns>The number of documents with a new category.</returns>
    public async Task<int> ApplyCategoriesAsync(Source source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        await using NpgsqlConnection connection = await database.OpenAsync(cancellationToken);
        string[] paths = [.. await connection.QueryAsync<string>(new CommandDefinition(
            "SELECT path FROM document WHERE source_id = @SourceId", new { source.SourceId }, cancellationToken: cancellationToken))];
        string[] categories = [.. paths.Select(path => SourceScanner.Category(source, path))];
        return await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE document d SET category = v.category
            FROM unnest(@Paths, @Categories) AS v(path, category)
            WHERE d.source_id = @SourceId AND d.path = v.path AND d.category <> v.category
            """,
            new { source.SourceId, Paths = paths, Categories = categories }, cancellationToken: cancellationToken));
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
    /// Reads a page of the chunks of a document. A specification of thousands of pages has thousands of chunks, so a reader takes them in pages.
    /// </summary>
    /// <param name="documentId">The key of the document.</param>
    /// <param name="sectionId">The section whose own chunks to read, or <see langword="null"/> for all chunks.</param>
    /// <param name="offset">The number of chunks to skip.</param>
    /// <param name="limit">The most chunks to return.</param>
    /// <param name="modelId">The model for <see cref="ChunkView.HasEmbedding"/>, or <see langword="null"/> when no model is ready.</param>
    /// <param name="cancellationToken">The token that cancels the query.</param>
    /// <returns>The chunks of the page in their order and the counts.</returns>
    public async Task<ChunkPage> GetChunksAsync(long documentId, long? sectionId, int offset, int limit, short? modelId, CancellationToken cancellationToken)
    {
        object parameters = new { DocumentId = documentId, SectionId = sectionId, Offset = offset, Limit = limit, ModelId = modelId ?? 0 };
        await using NpgsqlConnection connection = await database.OpenAsync(cancellationToken);
        IEnumerable<ChunkView> rows = await connection.QueryAsync<ChunkView>(new CommandDefinition(
            """
            SELECT c.chunk_id, c.ordinal, c.heading, c.body, c.char_start, c.char_end, c.section_id, c.page_start, c.page_end, c.overlap_chars,
                   EXISTS (SELECT 1 FROM chunk_embedding e WHERE e.chunk_id = c.chunk_id AND e.model_id = @ModelId) AS has_embedding
            FROM chunk c
            WHERE c.document_id = @DocumentId AND (@SectionId::bigint IS NULL OR c.section_id = @SectionId OR EXISTS (
                SELECT 1 FROM section s
                WHERE s.section_id = @SectionId AND c.char_start < coalesce((SELECT n.char_start FROM section n WHERE n.document_id = s.document_id AND n.ordinal = s.ordinal + 1), s.char_end)
                  AND c.char_end > s.char_start))
            ORDER BY c.ordinal
            OFFSET @Offset LIMIT @Limit
            """,
            parameters, cancellationToken: cancellationToken));
        (long total, long embedded) = await connection.QuerySingleAsync<(long, long)>(new CommandDefinition(
            """
            SELECT count(*) FILTER (WHERE @SectionId::bigint IS NULL OR c.section_id = @SectionId OR EXISTS (
                       SELECT 1 FROM section s
                       WHERE s.section_id = @SectionId AND c.char_start < coalesce((SELECT n.char_start FROM section n WHERE n.document_id = s.document_id AND n.ordinal = s.ordinal + 1), s.char_end)
                         AND c.char_end > s.char_start)),
                   count(*) FILTER (WHERE EXISTS (SELECT 1 FROM chunk_embedding e WHERE e.chunk_id = c.chunk_id AND e.model_id = @ModelId))
            FROM chunk c
            WHERE c.document_id = @DocumentId
            """,
            parameters, cancellationToken: cancellationToken));
        return new ChunkPage([.. rows], total, embedded);
    }

    /// <summary>
    /// Reads the places where a view of the text of a document can start: the starts of the chunks that start a block of the Markdown.
    /// </summary>
    /// <param name="documentId">The key of the document.</param>
    /// <param name="cancellationToken">The token that cancels the query.</param>
    /// <returns>The offsets in their order.</returns>
    public async Task<IReadOnlyList<int>> GetBoundariesAsync(long documentId, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await database.OpenAsync(cancellationToken);
        IEnumerable<int> starts = await connection.QueryAsync<int>(new CommandDefinition(
            "SELECT char_start FROM chunk WHERE document_id = @DocumentId ORDER BY char_start", new { DocumentId = documentId }, cancellationToken: cancellationToken));
        // The offsets count UTF-16 units, so the check reads the content here and not in SQL.
        string content = await ContentAsync(connection, documentId, cancellationToken);
        return [.. starts.Where(start => StartsBlock(content, start)).Distinct()];
    }

    /// <summary>
    /// Reads the display formulas of a document with their pages.
    /// </summary>
    /// <param name="documentId">The key of the document.</param>
    /// <param name="cancellationToken">The token that cancels the query.</param>
    /// <returns>The formulas in their order.</returns>
    public async Task<IReadOnlyList<FormulaView>> GetFormulasAsync(long documentId, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await database.OpenAsync(cancellationToken);
        string content = await ContentAsync(connection, documentId, cancellationToken);
        List<(int Offset, int Page)> pages = [.. PageMarkRegex().Matches(content).Select(static match => (match.Index, int.Parse(match.Groups["page"].ValueSpan, CultureInfo.InvariantCulture)))];
        List<FormulaView> formulas = [];
        int next = 0;
        int? page = null;
        foreach (Match match in DisplayFormulaRegex().Matches(content))
        {
            for (; next < pages.Count && pages[next].Offset <= match.Index; next++)
                page = pages[next].Page;

            formulas.Add(new FormulaView(match.Groups["latex"].Value, match.Index, page));
        }

        return formulas;
    }

    private static async Task<string> ContentAsync(NpgsqlConnection connection, long documentId, CancellationToken cancellationToken) =>
        await connection.ExecuteScalarAsync<string>(new CommandDefinition(
            "SELECT content FROM document WHERE document_id = @DocumentId", new { DocumentId = documentId }, cancellationToken: cancellationToken)) ?? string.Empty;

    // A block of Markdown starts after an empty line, or after the line of a service mark such as an anchor before a table.
    internal static bool StartsBlock(string content, int offset)
    {
        if (offset <= 0)
            return true;

        if (offset > content.Length || content[offset - 1] != '\n')
            return false;

        if (offset == 1 || content[offset - 2] == '\n')
            return true;

        int lineStart = content.LastIndexOf('\n', offset - 2) + 1;
        return content.AsSpan(lineStart, offset - 1 - lineStart).TrimStart().StartsWith("<!-- rag:", StringComparison.Ordinal);
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
    /// Writes a document and replaces its sections, anchors, chunks and links in one transaction.
    /// </summary>
    /// <remarks>
    /// A new chunk with the text of a stored chunk takes over its vectors, so an edit of a long document embeds only the changed chunks.
    /// The search covers all documents, so a chunk that another document repeats also needs no new vector.
    /// </remarks>
    /// <returns>The number of vectors that the new chunks took over.</returns>
    internal async Task<int> SaveAsync(int sourceId, string path, DocumentStructure structure, DocumentFile file, IReadOnlyList<(TextChunk Chunk, string Hash)> chunks, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await database.OpenAsync(cancellationToken);
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);
        long documentId = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            """
            INSERT INTO document (source_id, path, title, content, content_hash, parser_version, size_bytes, modified_at, category)
            VALUES (@SourceId, @Path, @Title, @Content, @ContentHash, @ParserVersion, @SizeBytes, @ModifiedAt, @Category)
            ON CONFLICT (source_id, path) DO UPDATE
            SET title = EXCLUDED.title, content = EXCLUDED.content, content_hash = EXCLUDED.content_hash, parser_version = EXCLUDED.parser_version,
                size_bytes = EXCLUDED.size_bytes, modified_at = EXCLUDED.modified_at, category = EXCLUDED.category, indexed_at = now()
            RETURNING document_id
            """,
            new { SourceId = sourceId, Path = path, structure.Title, structure.Content, file.ContentHash, file.ParserVersion, file.SizeBytes, file.ModifiedAt, file.Category },
            transaction, cancellationToken: cancellationToken));

        long[] oldIds = [.. await connection.QueryAsync<long>(new CommandDefinition(
            "SELECT chunk_id FROM chunk WHERE document_id = @DocumentId", new { DocumentId = documentId }, transaction, cancellationToken: cancellationToken))];
        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM section WHERE document_id = @DocumentId; DELETE FROM anchor WHERE document_id = @DocumentId;",
            new { DocumentId = documentId }, transaction, cancellationToken: cancellationToken));

        IReadOnlyList<DocumentSection> sections = structure.Sections;
        Dictionary<int, long> sectionIds = (await connection.QueryAsync<(int Ordinal, long SectionId)>(new CommandDefinition(
            """
            INSERT INTO section (document_id, ordinal, level, number, title, anchor, path, char_start, char_end, page_start, page_end)
            SELECT @DocumentId, * FROM unnest(@Ordinals, @Levels, @Numbers, @Titles, @Anchors, @Paths, @Starts, @Ends, @PageStarts, @PageEnds)
            RETURNING ordinal, section_id
            """,
            new
            {
                DocumentId = documentId,
                Ordinals = sections.Select(static section => section.Index).ToArray(),
                Levels = sections.Select(static section => (short)section.Level).ToArray(),
                Numbers = sections.Select(static section => section.Number).ToArray(),
                Titles = sections.Select(static section => section.Title).ToArray(),
                Anchors = sections.Select(static section => section.Anchor).ToArray(),
                Paths = sections.Select(static section => section.Path).ToArray(),
                Starts = sections.Select(static section => section.Start).ToArray(),
                Ends = sections.Select(static section => section.End).ToArray(),
                PageStarts = sections.Select(section => structure.PageAt(section.Start)).ToArray(),
                PageEnds = sections.Select(section => structure.PageAt(Math.Max(section.Start, section.End - 1))).ToArray(),
            },
            transaction, cancellationToken: cancellationToken))).ToDictionary(static row => row.Ordinal, static row => row.SectionId);

        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE section s SET parent_id = p.section_id
            FROM unnest(@Ordinals, @Parents) AS x(ordinal, parent)
            JOIN section p ON p.document_id = @DocumentId AND p.ordinal = x.parent
            WHERE s.document_id = @DocumentId AND s.ordinal = x.ordinal
            """,
            new
            {
                DocumentId = documentId,
                Ordinals = sections.Where(static section => section.Parent >= 0).Select(static section => section.Index).ToArray(),
                Parents = sections.Where(static section => section.Parent >= 0).Select(static section => section.Parent).ToArray(),
            },
            transaction, cancellationToken: cancellationToken));

        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO anchor (document_id, name, char_offset) SELECT @DocumentId, * FROM unnest(@Names, @Offsets)",
            new { DocumentId = documentId, Names = structure.Anchors.Keys.ToArray(), Offsets = structure.Anchors.Values.ToArray() },
            transaction, cancellationToken: cancellationToken));

        Dictionary<int, long> chunkIds = (await connection.QueryAsync<(int Ordinal, long ChunkId)>(new CommandDefinition(
            """
            INSERT INTO chunk (document_id, ordinal, heading, body, char_start, char_end, body_hash, section_id, page_start, page_end, overlap_chars)
            SELECT @DocumentId, * FROM unnest(@Ordinals, @Headings, @Bodies, @Starts, @Ends, @Hashes, @Sections, @PageStarts, @PageEnds, @Overlaps)
            RETURNING ordinal, chunk_id
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
                Sections = chunks.Select(item => sectionIds.TryGetValue(item.Chunk.Section, out long id) ? id : (long?)null).ToArray(),
                PageStarts = chunks.Select(static item => item.Chunk.PageStart).ToArray(),
                PageEnds = chunks.Select(static item => item.Chunk.PageEnd).ToArray(),
                Overlaps = chunks.Select(static item => item.Chunk.OverlapChars).ToArray(),
            },
            transaction, LongCommandSeconds, cancellationToken: cancellationToken))).ToDictionary(static row => row.Ordinal, static row => row.ChunkId);

        // A link belongs to each chunk whose range holds its start. An overlap gives a link to both chunks.
        List<(long ChunkId, int Ordinal, DocumentLink Link)> links = [];
        IReadOnlyList<DocumentLink> sorted = structure.Links;
        foreach ((TextChunk chunk, _) in chunks)
        {
            int ordinal = 0;
            for (int i = FirstLinkAt(sorted, chunk.Start); i < sorted.Count && sorted[i].Start < chunk.End; i++)
                links.Add((chunkIds[chunk.Ordinal], ordinal++, sorted[i]));
        }

        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO chunk_link (chunk_id, ordinal, kind, label, target_path, target_anchor, char_start)
            SELECT * FROM unnest(@ChunkIds, @Ordinals, @Kinds, @Labels, @Paths, @Anchors, @Starts)
            """,
            new
            {
                ChunkIds = links.Select(static item => item.ChunkId).ToArray(),
                Ordinals = links.Select(static item => item.Ordinal).ToArray(),
                Kinds = links.Select(static item => item.Link.Kind == LinkKind.Link ? "link" : "mention").ToArray(),
                Labels = links.Select(static item => item.Link.Label).ToArray(),
                Paths = links.Select(static item => item.Link.TargetPath).ToArray(),
                Anchors = links.Select(static item => item.Link.TargetAnchor).ToArray(),
                Starts = links.Select(static item => item.Link.Start).ToArray(),
            },
            transaction, cancellationToken: cancellationToken));

        // The identity gives the new chunks larger numbers than all chunks before them, so a smaller number marks an old chunk.
        // A large document, such as the 14000 chunks of the Vulkan specification, copies its vectors into the HNSW index for minutes.
        long[] newIds = [.. chunkIds.Values];
        int reused = newIds.Length == 0 ? 0 : await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO chunk_embedding (chunk_id, model_id, embedding)
            SELECT DISTINCT ON (n.chunk_id, e.model_id) n.chunk_id, e.model_id, e.embedding
            FROM chunk n
            JOIN chunk o ON o.body_hash = n.body_hash AND o.chunk_id < @FirstNewId
            JOIN chunk_embedding e ON e.chunk_id = o.chunk_id
            WHERE n.chunk_id = ANY (@NewIds)
            ORDER BY n.chunk_id, e.model_id
            ON CONFLICT DO NOTHING
            """,
            new { NewIds = newIds, FirstNewId = newIds.Min() }, transaction, LongCommandSeconds, cancellationToken: cancellationToken));

        if (oldIds.Length > 0)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM chunk WHERE chunk_id = ANY (@OldIds)", new { OldIds = oldIds }, transaction, LongCommandSeconds, cancellationToken: cancellationToken));
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

    private static int FirstLinkAt(IReadOnlyList<DocumentLink> links, int start)
    {
        int low = 0;
        int high = links.Count;
        while (low < high)
        {
            int middle = (low + high) / 2;
            if (links[middle].Start < start)
                low = middle + 1;
            else
                high = middle;
        }

        return low;
    }

    private static string EscapeLike(string text) => text.Replace(@"\", @"\\", StringComparison.Ordinal).Replace("%", @"\%", StringComparison.Ordinal).Replace("_", @"\_", StringComparison.Ordinal);

    [GeneratedRegex(@"<!-- rag:page (?<page>\d+) -->")]
    private static partial Regex PageMarkRegex();

    // A display formula of the importers: two dollars on a line of their own before and after the LaTeX.
    [GeneratedRegex(@"^\$\$\n(?<latex>[\s\S]*?)\n\$\$$", RegexOptions.Multiline)]
    private static partial Regex DisplayFormulaRegex();
}

/// <summary>
/// The facts of a file that the indexer stores with its document.
/// </summary>
/// <param name="ContentHash">The SHA-256 of the bytes in hexadecimal.</param>
/// <param name="ParserVersion">The version of the importers and the options of the chunker.</param>
/// <param name="SizeBytes">The size in bytes.</param>
/// <param name="ModifiedAt">The time of the last write in UTC, cut to microseconds like the timestamps of PostgreSQL.</param>
/// <param name="Category">The category from the rules of the source.</param>
internal sealed record DocumentFile(string ContentHash, string ParserVersion, long SizeBytes, DateTime ModifiedAt, string Category);
