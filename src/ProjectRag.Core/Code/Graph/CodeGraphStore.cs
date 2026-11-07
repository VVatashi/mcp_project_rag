using Dapper;

using Npgsql;

using NpgsqlTypes;

using ProjectRag.Core.Storage;

namespace ProjectRag.Core.Code.Graph;

/// <summary>
/// Writes the code graph of a code source and reads its state.
/// </summary>
/// <remarks>
/// The writer replaces the graph of a source in one transaction. It reserves the ids from the sequences and writes the large tables with binary COPY.
/// A path of the graph that has no document in the source drops its row. A symbol without a kept declaration drops too, with the symbols inside it.
/// A type edge, an external edge and a member link belong to a file, so a patch can replace the rows of some files, see <see cref="MergeAsync"/>.
/// </remarks>
/// <param name="database">The database.</param>
public sealed partial class CodeGraphStore(Database database)
{
    private const int WriteTimeoutSeconds = 600;

    // The children come before their parents.
    private static readonly string[] GraphTables =
    [
        "code_diagnostic", "code_member_link", "code_external_edge", "code_type_edge", "code_reference", "code_declaration", "code_symbol",
        "code_file_project", "code_solution_project", "code_solution", "code_project_reference", "code_project",
    ];

    /// <summary>
    /// Reads the state of the graph of a source.
    /// </summary>
    /// <param name="sourceId">The key of the source.</param>
    /// <param name="cancellationToken">The token that cancels the query.</param>
    /// <returns>The state, or <see langword="null"/> when the source has no graph and no error.</returns>
    public async Task<CodeGraphState?> GetStateAsync(int sourceId, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await database.OpenAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<CodeGraphState>(new CommandDefinition(
            "SELECT built_at, input_hash, extractor_version, error, failed_at, updated_at, update_count FROM code_graph_state WHERE source_id = @SourceId", new { SourceId = sourceId }, cancellationToken: cancellationToken));
    }

    /// <summary>
    /// Lists the files of a source with the facts that the graph depends on.
    /// </summary>
    /// <param name="sourceId">The key of the source.</param>
    /// <param name="cancellationToken">The token that cancels the query.</param>
    /// <returns>The files.</returns>
    public async Task<IReadOnlyList<CodeGraphFile>> ListFilesAsync(int sourceId, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await database.OpenAsync(cancellationToken);
        return [.. await connection.QueryAsync<CodeGraphFile>(new CommandDefinition(
            "SELECT path, content_hash, language FROM document WHERE source_id = @SourceId AND language IS NOT NULL", new { SourceId = sourceId }, cancellationToken: cancellationToken))];
    }

    /// <summary>
    /// Deletes the graph of a source and its state.
    /// </summary>
    /// <param name="sourceId">The key of the source.</param>
    /// <param name="cancellationToken">The token that cancels the query.</param>
    /// <returns>A task that completes after the delete.</returns>
    public async Task ClearAsync(int sourceId, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await database.OpenAsync(cancellationToken);
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);
        await DeleteGraphAsync(connection, transaction, sourceId, cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition("DELETE FROM code_graph_state WHERE source_id = @SourceId", new { SourceId = sourceId }, transaction, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// Records the error of a build. The old graph stays.
    /// </summary>
    /// <param name="sourceId">The key of the source.</param>
    /// <param name="error">The message.</param>
    /// <param name="inputHash">The hash of the input of the failed build, or <see langword="null"/> to keep the stored hash.</param>
    /// <param name="cancellationToken">The token that cancels the query.</param>
    /// <returns>A task that completes after the write.</returns>
    public async Task SaveErrorAsync(int sourceId, string error, string? inputHash, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await database.OpenAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO code_graph_state (source_id, error, failed_at, input_hash) VALUES (@SourceId, @Error, now(), @InputHash)
            ON CONFLICT (source_id) DO UPDATE SET error = EXCLUDED.error, failed_at = EXCLUDED.failed_at, input_hash = COALESCE(EXCLUDED.input_hash, code_graph_state.input_hash)
            """,
            new { SourceId = sourceId, Error = error, InputHash = inputHash }, cancellationToken: cancellationToken));
    }

    /// <summary>
    /// Replaces the graph of a source.
    /// </summary>
    /// <param name="sourceId">The key of the source.</param>
    /// <param name="graph">The new graph.</param>
    /// <param name="inputHash">The hash of the input of the build.</param>
    /// <param name="extractorVersion">The version of the extractor.</param>
    /// <param name="duration">The time of the build.</param>
    /// <param name="cancellationToken">The token that cancels the write. A cancel keeps the old graph.</param>
    /// <returns>The numbers of the stored rows.</returns>
    public async Task<CodeGraphCounts> ReplaceAsync(int sourceId, CodeGraph graph, string inputHash, int extractorVersion, TimeSpan duration, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(graph);
        await using NpgsqlConnection connection = await database.OpenAsync(cancellationToken);
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);
        Dictionary<string, long> documents = (await connection.QueryAsync<(string Path, long DocumentId)>(new CommandDefinition(
            "SELECT path, document_id FROM document WHERE source_id = @SourceId", new { SourceId = sourceId }, transaction, cancellationToken: cancellationToken)))
            .ToDictionary(static row => row.Path, static row => row.DocumentId, StringComparer.Ordinal);
        await DeleteGraphAsync(connection, transaction, sourceId, cancellationToken);

        int[] projectIds = await ReserveAsync(connection, transaction, "code_project", "project_id", graph.Projects.Count, cancellationToken);
        await WriteProjectsAsync(connection, sourceId, graph, projectIds, cancellationToken);
        await WriteSolutionsAsync(connection, transaction, sourceId, graph, projectIds, cancellationToken);
        await WriteFileProjectsAsync(connection, sourceId, graph, projectIds, documents, cancellationToken);

        int[] symbolIds = await WriteSymbolsAsync(connection, transaction, sourceId, graph, projectIds, documents, cancellationToken);
        await WriteDeclarationsAsync(connection, sourceId, graph, symbolIds, documents, cancellationToken);
        int references = await WriteReferencesAsync(connection, sourceId, graph, symbolIds, documents, cancellationToken);
        await WriteEdgesAsync(connection, sourceId, graph, symbolIds, documents, cancellationToken);
        await WriteDiagnosticsAsync(connection, sourceId, graph, projectIds, cancellationToken);

        int symbols = symbolIds.Count(static id => id >= 0);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO code_graph_state (source_id, built_at, duration_ms, input_hash, extractor_version, sdk_version, error, project_count, symbol_count, reference_count, updated_at, update_count)
            VALUES (@SourceId, now(), @DurationMs, @InputHash, @ExtractorVersion, @SdkVersion, NULL, @Projects, @Symbols, @References, NULL, 0)
            ON CONFLICT (source_id) DO UPDATE
            SET built_at = EXCLUDED.built_at, duration_ms = EXCLUDED.duration_ms, input_hash = EXCLUDED.input_hash, extractor_version = EXCLUDED.extractor_version,
                sdk_version = EXCLUDED.sdk_version, error = NULL, failed_at = NULL, project_count = EXCLUDED.project_count, symbol_count = EXCLUDED.symbol_count, reference_count = EXCLUDED.reference_count,
                updated_at = NULL, update_count = 0, update_duration_ms = NULL
            """,
            new
            {
                SourceId = sourceId,
                DurationMs = (int)Math.Min(duration.TotalMilliseconds, int.MaxValue),
                InputHash = inputHash,
                ExtractorVersion = extractorVersion,
                graph.SdkVersion,
                Projects = graph.Projects.Count,
                Symbols = symbols,
                References = references,
            },
            transaction, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        return new CodeGraphCounts(graph.Projects.Count, symbols, references);
    }

    private static async Task DeleteGraphAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, int sourceId, CancellationToken cancellationToken)
    {
        foreach (string table in GraphTables)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                $"DELETE FROM {table} WHERE source_id = @SourceId", new { SourceId = sourceId }, transaction, WriteTimeoutSeconds, cancellationToken: cancellationToken));
        }
    }

    private static async Task<int[]> ReserveAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string table, string column, int count, CancellationToken cancellationToken)
    {
        if (count == 0)
            return [];

        return [.. await connection.QueryAsync<int>(new CommandDefinition(
            "SELECT nextval(pg_get_serial_sequence(@Table, @Column))::integer FROM generate_series(1, @Count)",
            new { Table = table, Column = column, Count = count }, transaction, WriteTimeoutSeconds, cancellationToken: cancellationToken))];
    }

    private static async Task WriteProjectsAsync(NpgsqlConnection connection, int sourceId, CodeGraph graph, int[] projectIds, CancellationToken cancellationToken)
    {
        await using (NpgsqlBinaryImporter writer = await connection.BeginBinaryImportAsync(
            "COPY code_project (project_id, source_id, path, name, target_framework, is_test, document_count, error_count) FROM STDIN (FORMAT BINARY)", cancellationToken))
        {
            for (int i = 0; i < graph.Projects.Count; i++)
            {
                GraphProject project = graph.Projects[i];
                await writer.StartRowAsync(cancellationToken);
                await writer.WriteAsync(projectIds[i], NpgsqlDbType.Integer, cancellationToken);
                await writer.WriteAsync(sourceId, NpgsqlDbType.Integer, cancellationToken);
                await writer.WriteAsync(project.Path, NpgsqlDbType.Text, cancellationToken);
                await writer.WriteAsync(project.Name, NpgsqlDbType.Text, cancellationToken);
                await WriteNullableTextAsync(writer, project.TargetFramework, cancellationToken);
                await writer.WriteAsync(project.IsTest, NpgsqlDbType.Boolean, cancellationToken);
                await writer.WriteAsync(project.DocumentCount, NpgsqlDbType.Integer, cancellationToken);
                await writer.WriteAsync(project.ErrorCount, NpgsqlDbType.Integer, cancellationToken);
            }

            await writer.CompleteAsync(cancellationToken);
        }

        HashSet<(int, int)> seen = [];
        await using NpgsqlBinaryImporter links = await connection.BeginBinaryImportAsync(
            "COPY code_project_reference (source_id, project_id, referenced_id) FROM STDIN (FORMAT BINARY)", cancellationToken);
        for (int i = 0; i < graph.Projects.Count; i++)
        {
            foreach (int referenced in graph.Projects[i].ProjectReferences)
            {
                if (referenced < 0 || referenced >= projectIds.Length || referenced == i || !seen.Add((i, referenced)))
                    continue;

                await links.StartRowAsync(cancellationToken);
                await links.WriteAsync(sourceId, NpgsqlDbType.Integer, cancellationToken);
                await links.WriteAsync(projectIds[i], NpgsqlDbType.Integer, cancellationToken);
                await links.WriteAsync(projectIds[referenced], NpgsqlDbType.Integer, cancellationToken);
            }
        }

        await links.CompleteAsync(cancellationToken);
    }

    private static async Task WriteSolutionsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, int sourceId, CodeGraph graph, int[] projectIds, CancellationToken cancellationToken)
    {
        int[] solutionIds = await ReserveAsync(connection, transaction, "code_solution", "solution_id", graph.Solutions.Count, cancellationToken);
        await using (NpgsqlBinaryImporter writer = await connection.BeginBinaryImportAsync(
            "COPY code_solution (solution_id, source_id, path) FROM STDIN (FORMAT BINARY)", cancellationToken))
        {
            for (int i = 0; i < graph.Solutions.Count; i++)
            {
                await writer.StartRowAsync(cancellationToken);
                await writer.WriteAsync(solutionIds[i], NpgsqlDbType.Integer, cancellationToken);
                await writer.WriteAsync(sourceId, NpgsqlDbType.Integer, cancellationToken);
                await writer.WriteAsync(graph.Solutions[i].Path, NpgsqlDbType.Text, cancellationToken);
            }

            await writer.CompleteAsync(cancellationToken);
        }

        await using NpgsqlBinaryImporter links = await connection.BeginBinaryImportAsync(
            "COPY code_solution_project (source_id, solution_id, project_id) FROM STDIN (FORMAT BINARY)", cancellationToken);
        for (int i = 0; i < graph.Solutions.Count; i++)
        {
            foreach (int project in graph.Solutions[i].Projects.Where(project => project >= 0 && project < projectIds.Length).Distinct())
            {
                await links.StartRowAsync(cancellationToken);
                await links.WriteAsync(sourceId, NpgsqlDbType.Integer, cancellationToken);
                await links.WriteAsync(solutionIds[i], NpgsqlDbType.Integer, cancellationToken);
                await links.WriteAsync(projectIds[project], NpgsqlDbType.Integer, cancellationToken);
            }
        }

        await links.CompleteAsync(cancellationToken);
    }

    private static async Task WriteFileProjectsAsync(NpgsqlConnection connection, int sourceId, CodeGraph graph, int[] projectIds, Dictionary<string, long> documents, CancellationToken cancellationToken)
    {
        HashSet<(long, int)> seen = [];
        await using NpgsqlBinaryImporter writer = await connection.BeginBinaryImportAsync(
            "COPY code_file_project (source_id, document_id, project_id, is_owner) FROM STDIN (FORMAT BINARY)", cancellationToken);
        foreach (GraphFileProject file in graph.FileProjects)
        {
            if (file.Project < 0 || file.Project >= projectIds.Length || !documents.TryGetValue(file.Path, out long documentId) || !seen.Add((documentId, file.Project)))
                continue;

            await writer.StartRowAsync(cancellationToken);
            await writer.WriteAsync(sourceId, NpgsqlDbType.Integer, cancellationToken);
            await writer.WriteAsync(documentId, NpgsqlDbType.Bigint, cancellationToken);
            await writer.WriteAsync(projectIds[file.Project], NpgsqlDbType.Integer, cancellationToken);
            await writer.WriteAsync(file.IsOwner, NpgsqlDbType.Boolean, cancellationToken);
        }

        await writer.CompleteAsync(cancellationToken);
    }

    // Returns the key of each symbol of the graph by its index, or -1 for a symbol that the writer drops.
    private static async Task<int[]> WriteSymbolsAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, int sourceId, CodeGraph graph, int[] projectIds, Dictionary<string, long> documents, CancellationToken cancellationToken)
    {
        int count = graph.Symbols.Count;
        bool[] alive = new bool[count];
        foreach (GraphDeclaration declaration in graph.Declarations)
        {
            if (declaration.Symbol >= 0 && declaration.Symbol < count && documents.ContainsKey(declaration.Path))
                alive[declaration.Symbol] = true;
        }

        for (int i = 0; i < count; i++)
        {
            if (graph.Symbols[i].Project < 0 || graph.Symbols[i].Project >= projectIds.Length)
                alive[i] = false;
        }

        // A symbol inside a dropped symbol drops too. The loop ends after the depth of the deepest nesting.
        for (bool changed = true; changed;)
        {
            changed = false;
            for (int i = 0; i < count; i++)
            {
                if (alive[i] && graph.Symbols[i].Parent is { } parent && (parent < 0 || parent >= count || !alive[parent]))
                {
                    alive[i] = false;
                    changed = true;
                }
            }
        }

        int[] reserved = await ReserveAsync(connection, transaction, "code_symbol", "symbol_id", alive.Count(static flag => flag), cancellationToken);
        int[] ids = new int[count];
        int next = 0;
        for (int i = 0; i < count; i++)
            ids[i] = alive[i] ? reserved[next++] : -1;

        await using NpgsqlBinaryImporter writer = await connection.BeginBinaryImportAsync(
            "COPY code_symbol (symbol_id, source_id, project_id, parent_id, kind, name, full_name, doc_id, signature, namespace, accessibility, modifiers, summary) FROM STDIN (FORMAT BINARY)",
            cancellationToken);
        for (int i = 0; i < count; i++)
        {
            if (!alive[i])
                continue;

            GraphSymbol symbol = graph.Symbols[i];
            await writer.StartRowAsync(cancellationToken);
            await writer.WriteAsync(ids[i], NpgsqlDbType.Integer, cancellationToken);
            await writer.WriteAsync(sourceId, NpgsqlDbType.Integer, cancellationToken);
            await writer.WriteAsync(projectIds[symbol.Project], NpgsqlDbType.Integer, cancellationToken);
            if (symbol.Parent is { } parent)
                await writer.WriteAsync(ids[parent], NpgsqlDbType.Integer, cancellationToken);
            else
                await writer.WriteNullAsync(cancellationToken);

            await writer.WriteAsync(symbol.Kind, NpgsqlDbType.Text, cancellationToken);
            await writer.WriteAsync(symbol.Name, NpgsqlDbType.Text, cancellationToken);
            await writer.WriteAsync(symbol.FullName, NpgsqlDbType.Text, cancellationToken);
            await writer.WriteAsync(symbol.DocId, NpgsqlDbType.Text, cancellationToken);
            await writer.WriteAsync(symbol.Signature, NpgsqlDbType.Text, cancellationToken);
            await writer.WriteAsync(symbol.Namespace, NpgsqlDbType.Text, cancellationToken);
            await writer.WriteAsync(symbol.Accessibility, NpgsqlDbType.Text, cancellationToken);
            await writer.WriteAsync(symbol.Modifiers.ToArray(), NpgsqlDbType.Array | NpgsqlDbType.Text, cancellationToken);
            await WriteNullableTextAsync(writer, symbol.Summary, cancellationToken);
        }

        await writer.CompleteAsync(cancellationToken);
        return ids;
    }

    private static async Task WriteDeclarationsAsync(NpgsqlConnection connection, int sourceId, CodeGraph graph, int[] symbolIds, Dictionary<string, long> documents, CancellationToken cancellationToken)
    {
        await using NpgsqlBinaryImporter writer = await connection.BeginBinaryImportAsync(
            "COPY code_declaration (source_id, symbol_id, document_id, start_line, end_line, name_line, name_column) FROM STDIN (FORMAT BINARY)", cancellationToken);
        foreach (GraphDeclaration declaration in graph.Declarations)
        {
            if (!TryId(symbolIds, declaration.Symbol, out int symbolId) || !documents.TryGetValue(declaration.Path, out long documentId))
                continue;

            await writer.StartRowAsync(cancellationToken);
            await writer.WriteAsync(sourceId, NpgsqlDbType.Integer, cancellationToken);
            await writer.WriteAsync(symbolId, NpgsqlDbType.Integer, cancellationToken);
            await writer.WriteAsync(documentId, NpgsqlDbType.Bigint, cancellationToken);
            await writer.WriteAsync(declaration.StartLine, NpgsqlDbType.Integer, cancellationToken);
            await writer.WriteAsync(declaration.EndLine, NpgsqlDbType.Integer, cancellationToken);
            await writer.WriteAsync(declaration.NameLine, NpgsqlDbType.Integer, cancellationToken);
            await writer.WriteAsync(declaration.NameColumn, NpgsqlDbType.Integer, cancellationToken);
        }

        await writer.CompleteAsync(cancellationToken);
    }

    private static async Task<int> WriteReferencesAsync(NpgsqlConnection connection, int sourceId, CodeGraph graph, int[] symbolIds, Dictionary<string, long> documents, CancellationToken cancellationToken)
    {
        int written = 0;
        await using NpgsqlBinaryImporter writer = await connection.BeginBinaryImportAsync(
            "COPY code_reference (source_id, symbol_id, document_id, line, \"column\", length, kind, from_id) FROM STDIN (FORMAT BINARY)", cancellationToken);
        foreach (GraphReference reference in graph.References)
        {
            if (!TryId(symbolIds, reference.Symbol, out int symbolId) || !documents.TryGetValue(reference.Path, out long documentId))
                continue;

            await writer.StartRowAsync(cancellationToken);
            await writer.WriteAsync(sourceId, NpgsqlDbType.Integer, cancellationToken);
            await writer.WriteAsync(symbolId, NpgsqlDbType.Integer, cancellationToken);
            await writer.WriteAsync(documentId, NpgsqlDbType.Bigint, cancellationToken);
            await writer.WriteAsync(reference.Line, NpgsqlDbType.Integer, cancellationToken);
            await writer.WriteAsync(reference.Column, NpgsqlDbType.Integer, cancellationToken);
            await writer.WriteAsync(reference.Length, NpgsqlDbType.Integer, cancellationToken);
            await writer.WriteAsync(reference.Kind, NpgsqlDbType.Text, cancellationToken);
            if (reference.From is { } caller && TryId(symbolIds, caller, out int fromId))
                await writer.WriteAsync(fromId, NpgsqlDbType.Integer, cancellationToken);
            else
                await writer.WriteNullAsync(cancellationToken);

            written++;
        }

        await writer.CompleteAsync(cancellationToken);
        return written;
    }

    // A row whose file is not in the dictionary drops. The rows of one pair of types in several files stay apart: the readers sum them.
    private static async Task WriteEdgesAsync(NpgsqlConnection connection, int sourceId, CodeGraph graph, int[] symbolIds, Dictionary<string, long> documents, CancellationToken cancellationToken)
    {
        await using (NpgsqlBinaryImporter writer = await connection.BeginBinaryImportAsync(
            "COPY code_type_edge (source_id, from_id, to_id, kind, count, document_id) FROM STDIN (FORMAT BINARY)", cancellationToken))
        {
            foreach (GraphTypeEdge edge in graph.TypeEdges)
            {
                if (!TryId(symbolIds, edge.From, out int fromId) || !TryId(symbolIds, edge.To, out int toId) || !documents.TryGetValue(edge.Path, out long documentId))
                    continue;

                await writer.StartRowAsync(cancellationToken);
                await writer.WriteAsync(sourceId, NpgsqlDbType.Integer, cancellationToken);
                await writer.WriteAsync(fromId, NpgsqlDbType.Integer, cancellationToken);
                await writer.WriteAsync(toId, NpgsqlDbType.Integer, cancellationToken);
                await writer.WriteAsync(edge.Kind, NpgsqlDbType.Text, cancellationToken);
                await writer.WriteAsync(edge.Count, NpgsqlDbType.Integer, cancellationToken);
                await writer.WriteAsync(documentId, NpgsqlDbType.Bigint, cancellationToken);
            }

            await writer.CompleteAsync(cancellationToken);
        }

        await using (NpgsqlBinaryImporter writer = await connection.BeginBinaryImportAsync(
            "COPY code_external_edge (source_id, from_id, target, assembly, kind, count, document_id) FROM STDIN (FORMAT BINARY)", cancellationToken))
        {
            foreach (GraphExternalEdge edge in graph.ExternalEdges)
            {
                if (!TryId(symbolIds, edge.From, out int fromId) || !documents.TryGetValue(edge.Path, out long documentId))
                    continue;

                await writer.StartRowAsync(cancellationToken);
                await writer.WriteAsync(sourceId, NpgsqlDbType.Integer, cancellationToken);
                await writer.WriteAsync(fromId, NpgsqlDbType.Integer, cancellationToken);
                await writer.WriteAsync(edge.Target, NpgsqlDbType.Text, cancellationToken);
                await writer.WriteAsync(edge.Assembly, NpgsqlDbType.Text, cancellationToken);
                await writer.WriteAsync(edge.Kind, NpgsqlDbType.Text, cancellationToken);
                await writer.WriteAsync(edge.Count, NpgsqlDbType.Integer, cancellationToken);
                await writer.WriteAsync(documentId, NpgsqlDbType.Bigint, cancellationToken);
            }

            await writer.CompleteAsync(cancellationToken);
        }

        await using NpgsqlBinaryImporter links = await connection.BeginBinaryImportAsync(
            "COPY code_member_link (source_id, member_id, target_id, kind, document_id) FROM STDIN (FORMAT BINARY)", cancellationToken);
        foreach (GraphMemberLink link in graph.MemberLinks)
        {
            if (!TryId(symbolIds, link.Member, out int member) || !TryId(symbolIds, link.Target, out int target) || !documents.TryGetValue(link.Path, out long documentId))
                continue;

            await links.StartRowAsync(cancellationToken);
            await links.WriteAsync(sourceId, NpgsqlDbType.Integer, cancellationToken);
            await links.WriteAsync(member, NpgsqlDbType.Integer, cancellationToken);
            await links.WriteAsync(target, NpgsqlDbType.Integer, cancellationToken);
            await links.WriteAsync(link.Kind, NpgsqlDbType.Text, cancellationToken);
            await links.WriteAsync(documentId, NpgsqlDbType.Bigint, cancellationToken);
        }

        await links.CompleteAsync(cancellationToken);
    }

    private static async Task WriteDiagnosticsAsync(NpgsqlConnection connection, int sourceId, CodeGraph graph, int[] projectIds, CancellationToken cancellationToken)
    {
        await using NpgsqlBinaryImporter writer = await connection.BeginBinaryImportAsync(
            "COPY code_diagnostic (source_id, project_id, severity, code, message, path, line) FROM STDIN (FORMAT BINARY)", cancellationToken);
        foreach (GraphDiagnostic diagnostic in graph.Diagnostics)
        {
            await writer.StartRowAsync(cancellationToken);
            await writer.WriteAsync(sourceId, NpgsqlDbType.Integer, cancellationToken);
            if (diagnostic.Project is { } project && project >= 0 && project < projectIds.Length)
                await writer.WriteAsync(projectIds[project], NpgsqlDbType.Integer, cancellationToken);
            else
                await writer.WriteNullAsync(cancellationToken);

            await writer.WriteAsync(diagnostic.Severity, NpgsqlDbType.Text, cancellationToken);
            await writer.WriteAsync(diagnostic.Code, NpgsqlDbType.Text, cancellationToken);
            await writer.WriteAsync(diagnostic.Message, NpgsqlDbType.Text, cancellationToken);
            await WriteNullableTextAsync(writer, diagnostic.Path, cancellationToken);
            if (diagnostic.Line is { } line)
                await writer.WriteAsync(line, NpgsqlDbType.Integer, cancellationToken);
            else
                await writer.WriteNullAsync(cancellationToken);
        }

        await writer.CompleteAsync(cancellationToken);
    }

    private static bool TryId(int[] ids, int index, out int id)
    {
        id = index >= 0 && index < ids.Length ? ids[index] : -1;
        return id >= 0;
    }

    private static async Task WriteNullableTextAsync(NpgsqlBinaryImporter writer, string? text, CancellationToken cancellationToken)
    {
        if (text is null)
            await writer.WriteNullAsync(cancellationToken);
        else
            await writer.WriteAsync(text, NpgsqlDbType.Text, cancellationToken);
    }
}

/// <summary>
/// The stored state of the graph of a source.
/// </summary>
public sealed class CodeGraphState
{
    /// <summary>Gets the end of the last successful build, or <see langword="null"/> when no build succeeded.</summary>
    public DateTime? BuiltAt { get; init; }

    /// <summary>Gets the hash of the input of the last build.</summary>
    public string? InputHash { get; init; }

    /// <summary>Gets the version of the extractor of the last successful build.</summary>
    public int? ExtractorVersion { get; init; }

    /// <summary>Gets the error of the last build, or <see langword="null"/>.</summary>
    public string? Error { get; init; }

    /// <summary>Gets the time of the last failed build, or <see langword="null"/> after a successful build.</summary>
    public DateTime? FailedAt { get; init; }

    /// <summary>Gets the time of the last incremental update, or <see langword="null"/> when no update followed the last full build.</summary>
    public DateTime? UpdatedAt { get; init; }

    /// <summary>Gets the number of incremental updates since the last full build.</summary>
    public int UpdateCount { get; init; }
}

/// <summary>
/// A file of a source with the facts that the graph depends on.
/// </summary>
public sealed class CodeGraphFile
{
    /// <summary>Gets the path relative to the folder of the source.</summary>
    public string Path { get; init; } = string.Empty;

    /// <summary>Gets the SHA-256 of the bytes.</summary>
    public string ContentHash { get; init; } = string.Empty;

    /// <summary>Gets the language.</summary>
    public string? Language { get; init; }
}
/// <summary>
/// The numbers of the rows that a build stored.
/// </summary>
/// <param name="Projects">The projects.</param>
/// <param name="Symbols">The symbols.</param>
/// <param name="References">The references.</param>
public sealed record CodeGraphCounts(int Projects, int Symbols, int References);
