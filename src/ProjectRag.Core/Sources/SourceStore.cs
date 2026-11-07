using System.Text.Json;

using Dapper;

using Npgsql;

using ProjectRag.Core.Storage;

namespace ProjectRag.Core.Sources;

/// <summary>
/// Reads and writes the sources.
/// </summary>
/// <param name="database">The database.</param>
public sealed class SourceStore(Database database)
{
    // The counts come from subqueries. A join of documents and chunks would multiply the rows.
    private const string SelectSql = """
        SELECT s.source_id, s.name, s.description, s.root_path, s.urls, s.include_globs, s.exclude_globs, s.kind, s.dot_globs, s.category_rules::text AS category_rules, s.replacements::text AS replacements, s.build_properties::text AS build_properties, s.created_at, s.indexed_at,
               (SELECT count(*) FROM document d WHERE d.source_id = s.source_id) AS document_count,
               ARRAY(SELECT DISTINCT d.category COLLATE "C" FROM document d WHERE d.source_id = s.source_id ORDER BY 1) AS categories,
               (SELECT count(*) FROM chunk c JOIN document d USING (document_id) WHERE d.source_id = s.source_id) AS chunk_count,
               (SELECT count(*) FROM chunk_embedding e JOIN chunk c USING (chunk_id) JOIN document d USING (document_id)
                WHERE d.source_id = s.source_id AND e.model_id = @ModelId) AS embedded_count
        FROM source s
        """;

    /// <summary>
    /// Reads all sources in the order of their names.
    /// </summary>
    /// <param name="modelId">The model for <see cref="Source.EmbeddedCount"/>, or <see langword="null"/> when no model is ready.</param>
    /// <param name="cancellationToken">The token that cancels the query.</param>
    /// <returns>The sources.</returns>
    public async Task<IReadOnlyList<Source>> ListAsync(short? modelId, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await database.OpenAsync(cancellationToken);
        IEnumerable<SourceRow> rows = await connection.QueryAsync<SourceRow>(new CommandDefinition(
            SelectSql + " ORDER BY s.name", new { ModelId = modelId ?? 0 }, cancellationToken: cancellationToken));
        return [.. rows.Select(static row => row.ToSource())];
    }

    /// <summary>
    /// Reads a source.
    /// </summary>
    /// <param name="sourceId">The key of the source.</param>
    /// <param name="modelId">The model for <see cref="Source.EmbeddedCount"/>.</param>
    /// <param name="cancellationToken">The token that cancels the query.</param>
    /// <returns>The source, or <see langword="null"/> when there is none.</returns>
    public async Task<Source?> GetAsync(int sourceId, short? modelId, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await database.OpenAsync(cancellationToken);
        SourceRow? row = await connection.QuerySingleOrDefaultAsync<SourceRow>(new CommandDefinition(
            SelectSql + " WHERE s.source_id = @SourceId", new { SourceId = sourceId, ModelId = modelId ?? 0 }, cancellationToken: cancellationToken));
        return row?.ToSource();
    }

    /// <summary>
    /// Finds the keys of sources by their names.
    /// </summary>
    /// <param name="names">The names.</param>
    /// <param name="cancellationToken">The token that cancels the query.</param>
    /// <returns>The keys by name. A name without a source is missing from the dictionary.</returns>
    public async Task<IReadOnlyDictionary<string, int>> FindIdsAsync(IReadOnlyCollection<string> names, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await database.OpenAsync(cancellationToken);
        IEnumerable<(string Name, int SourceId)> rows = await connection.QueryAsync<(string Name, int SourceId)>(new CommandDefinition(
            "SELECT name, source_id FROM source WHERE name = ANY (@Names)", new { Names = names.ToArray() }, cancellationToken: cancellationToken));
        return rows.ToDictionary(static row => row.Name, static row => row.SourceId, StringComparer.Ordinal);
    }

    /// <summary>
    /// Reads the keys of all sources.
    /// </summary>
    /// <param name="cancellationToken">The token that cancels the query.</param>
    /// <returns>The keys.</returns>
    public async Task<IReadOnlyList<int>> ListIdsAsync(CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await database.OpenAsync(cancellationToken);
        return [.. await connection.QueryAsync<int>(new CommandDefinition("SELECT source_id FROM source ORDER BY name", cancellationToken: cancellationToken))];
    }

    /// <summary>
    /// Reads the folders of the sources that have one. The query is cheap, because it counts no documents.
    /// </summary>
    /// <param name="cancellationToken">The token that cancels the query.</param>
    /// <returns>The folders.</returns>
    public async Task<IReadOnlyList<SourceFolder>> ListFoldersAsync(CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await database.OpenAsync(cancellationToken);
        IEnumerable<FolderRow> rows = await connection.QueryAsync<FolderRow>(new CommandDefinition(
            "SELECT source_id, name, root_path, dot_globs FROM source WHERE root_path <> '' ORDER BY source_id", cancellationToken: cancellationToken));
        return [.. rows.Select(static row => new SourceFolder(row.SourceId, row.Name, row.RootPath, row.DotGlobs))];
    }

    /// <summary>
    /// Adds a source.
    /// </summary>
    /// <param name="input">The fields of the source. The caller has checked them.</param>
    /// <param name="cancellationToken">The token that cancels the query.</param>
    /// <returns>The key of the new source.</returns>
    /// <exception cref="PostgresException">Another source has the name (<see cref="PostgresErrorCodes.UniqueViolation"/>).</exception>
    public async Task<int> AddAsync(SourceInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        await using NpgsqlConnection connection = await database.OpenAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            """
            INSERT INTO source (name, description, root_path, urls, include_globs, exclude_globs, kind, dot_globs, category_rules, replacements, build_properties)
            VALUES (@Name, @Description, @RootPath, @Urls, @IncludeGlobs, @ExcludeGlobs, @Kind, @DotGlobs, CAST(@CategoryRules AS jsonb), CAST(@Replacements AS jsonb), COALESCE(CAST(@BuildProperties AS jsonb), '{}'::jsonb))
            RETURNING source_id
            """,
            Parameters(input, 0), cancellationToken: cancellationToken));
    }

    /// <summary>
    /// Changes a source. The documents stay until the next run of the indexer. The kind of the source stays as it is.
    /// </summary>
    /// <param name="sourceId">The key of the source.</param>
    /// <param name="input">The new fields. The caller has checked them.</param>
    /// <param name="cancellationToken">The token that cancels the query.</param>
    /// <returns><see langword="true"/> when the source exists.</returns>
    /// <exception cref="PostgresException">Another source has the name (<see cref="PostgresErrorCodes.UniqueViolation"/>).</exception>
    public async Task<bool> UpdateAsync(int sourceId, SourceInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        await using NpgsqlConnection connection = await database.OpenAsync(cancellationToken);
        int count = await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE source
            SET name = @Name, description = @Description, root_path = @RootPath, urls = @Urls, include_globs = @IncludeGlobs, exclude_globs = @ExcludeGlobs, dot_globs = @DotGlobs,
                category_rules = CAST(@CategoryRules AS jsonb), replacements = CAST(@Replacements AS jsonb),
                build_properties = COALESCE(CAST(@BuildProperties AS jsonb), build_properties)
            WHERE source_id = @SourceId
            """,
            Parameters(input, sourceId), cancellationToken: cancellationToken));
        return count > 0;
    }

    /// <summary>
    /// Deletes a source with its documents, chunks, vectors and runs.
    /// </summary>
    /// <param name="sourceId">The key of the source.</param>
    /// <param name="cancellationToken">The token that cancels the query.</param>
    /// <returns><see langword="true"/> when the source existed.</returns>
    public async Task<bool> DeleteAsync(int sourceId, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await database.OpenAsync(cancellationToken);
        return await connection.ExecuteAsync(new CommandDefinition("DELETE FROM source WHERE source_id = @SourceId", new { SourceId = sourceId }, cancellationToken: cancellationToken)) > 0;
    }

    /// <summary>
    /// Writes the end time of a successful run of the indexer.
    /// </summary>
    /// <param name="sourceId">The key of the source.</param>
    /// <param name="cancellationToken">The token that cancels the query.</param>
    /// <returns>A task that completes after the write.</returns>
    public async Task MarkIndexedAsync(int sourceId, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await database.OpenAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition("UPDATE source SET indexed_at = now() WHERE source_id = @SourceId", new { SourceId = sourceId }, cancellationToken: cancellationToken));
    }

    private static object Parameters(SourceInput input, int sourceId) => new
    {
        SourceId = sourceId,
        input.Name,
        Description = input.Description ?? string.Empty,
        RootPath = input.RootPath ?? string.Empty,
        Urls = input.CleanUrls().ToArray(),
        IncludeGlobs = input.Includes().ToArray(),
        ExcludeGlobs = input.Excludes().ToArray(),
        Kind = input.EffectiveKind(),
        DotGlobs = input.DotGlobsOrDefault().ToArray(),
        CategoryRules = JsonSerializer.Serialize(input.Rules().Select(static rule => new CategoryRule { Pattern = rule.Pattern.Trim(), Category = rule.Category.Trim() }), JsonOptions),
        Replacements = JsonSerializer.Serialize(
            (input.Replacements ?? []).Select(static replacement => new TextReplacement { Pattern = replacement.Pattern.Trim(), Find = replacement.Find, Replace = replacement.Replace ?? string.Empty }),
            JsonOptions),
        BuildProperties = input.BuildProperties is null ? null : JsonSerializer.Serialize(input.BuildProperties, JsonOptions),
    };

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private sealed class FolderRow
    {
        public int SourceId { get; init; }

        public string Name { get; init; } = string.Empty;

        public string RootPath { get; init; } = string.Empty;

        public string[] DotGlobs { get; init; } = [];
    }

    // Npgsql reads text[] as string[]. The public type exposes read-only lists.
    private sealed class SourceRow
    {
        public int SourceId { get; init; }

        public string Name { get; init; } = string.Empty;

        public string Description { get; init; } = string.Empty;

        public string RootPath { get; init; } = string.Empty;

        public string[] Urls { get; init; } = [];

        public string[] IncludeGlobs { get; init; } = [];

        public string[] ExcludeGlobs { get; init; } = [];

        public string Kind { get; init; } = SourceKind.Docs;

        public string[] DotGlobs { get; init; } = [];

        public string CategoryRules { get; init; } = "[]";

        public string Replacements { get; init; } = "[]";

        public string BuildProperties { get; init; } = "{}";

        public DateTime CreatedAt { get; init; }

        public DateTime? IndexedAt { get; init; }

        public long DocumentCount { get; init; }

        public string[] Categories { get; init; } = [];

        public long ChunkCount { get; init; }

        public long EmbeddedCount { get; init; }

        public Source ToSource() => new()
        {
            SourceId = SourceId,
            Name = Name,
            Description = Description,
            RootPath = RootPath,
            Urls = Urls,
            IncludeGlobs = IncludeGlobs,
            ExcludeGlobs = ExcludeGlobs,
            Kind = Kind,
            DotGlobs = DotGlobs,
            CategoryRules = JsonSerializer.Deserialize<List<CategoryRule>>(CategoryRules, JsonOptions) ?? [],
            Replacements = JsonSerializer.Deserialize<List<TextReplacement>>(Replacements, JsonOptions) ?? [],
            BuildProperties = JsonSerializer.Deserialize<Dictionary<string, string>>(BuildProperties, JsonOptions) ?? [],
            CreatedAt = CreatedAt,
            IndexedAt = IndexedAt,
            DocumentCount = DocumentCount,
            Categories = Categories,
            ChunkCount = ChunkCount,
            EmbeddedCount = EmbeddedCount,
        };
    }
}
