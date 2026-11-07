using Dapper;

using Npgsql;

using NpgsqlTypes;

namespace ProjectRag.Core.Code.Graph;

// The incremental update of the graph: the merge of a patch, and the search for the files that a patch must read.
public sealed partial class CodeGraphStore
{
    /// <summary>
    /// Finds the files that an update must read: the changed files and the files that refer to symbols declared in them.
    /// </summary>
    /// <param name="sourceId">The key of the source.</param>
    /// <param name="changedPaths">The changed files, relative to the folder, with forward slashes.</param>
    /// <param name="cancellationToken">The token that cancels the query.</param>
    /// <returns>The changed files and the files with references to their symbols, in ordinal order.</returns>
    public async Task<IReadOnlyList<string>> FindAffectedFilesAsync(int sourceId, IReadOnlyCollection<string> changedPaths, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(changedPaths);
        HashSet<string> affected = new(changedPaths, StringComparer.Ordinal);
        if (affected.Count == 0)
            return [];

        await using NpgsqlConnection connection = await database.OpenAsync(cancellationToken);
        IEnumerable<string> referencing = await connection.QueryAsync<string>(new CommandDefinition(
            """
            SELECT d.path
            FROM document d
            WHERE d.document_id IN (
                SELECT r.document_id
                FROM code_reference r
                WHERE r.symbol_id IN (
                    SELECT cd.symbol_id
                    FROM code_declaration cd
                    JOIN document c ON c.document_id = cd.document_id
                    WHERE c.source_id = @SourceId AND c.path = ANY (@Paths)))
            """,
            new { SourceId = sourceId, Paths = affected.ToArray() }, commandTimeout: WriteTimeoutSeconds, cancellationToken: cancellationToken));
        affected.UnionWith(referencing);
        return [.. affected.Order(StringComparer.Ordinal)];
    }

    /// <summary>
    /// Merges a patch of some files into the graph of a source.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The merge deletes the declarations, references, edges and links of the files of the patch and writes the new ones.
    /// A symbol of the patch keeps its key when the same project already has a symbol with its documentation ID, so references from other files stay valid.
    /// A stub resolves to the key of a stored symbol, and a row that points to a stub without a match drops.
    /// A symbol that keeps no declaration is deleted with every row that points to it and with the symbols inside it.
    /// </para>
    /// <para>
    /// All steps run in one transaction. A cancel or an error keeps the old graph.
    /// </para>
    /// </remarks>
    /// <param name="sourceId">The key of the source.</param>
    /// <param name="patch">The new graph of some files.</param>
    /// <param name="inputHash">The hash of the input of the update.</param>
    /// <param name="extractorVersion">The version of the extractor.</param>
    /// <param name="duration">The time of the update.</param>
    /// <param name="cancellationToken">The token that cancels the write. A cancel keeps the old graph.</param>
    /// <returns>The numbers of the stored rows of the whole graph.</returns>
    /// <exception cref="InvalidOperationException">The source has no graph, a file of the patch is not a document of the source, or the patch names a project that the graph does not have.</exception>
    public async Task<CodeGraphCounts> MergeAsync(int sourceId, CodeGraphPatch patch, string inputHash, int extractorVersion, TimeSpan duration, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(patch);
        ArgumentException.ThrowIfNullOrEmpty(inputHash);
        CodeGraph graph = patch.Graph;
        await using NpgsqlConnection connection = await database.OpenAsync(cancellationToken);
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);
        if (!await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS (SELECT 1 FROM code_graph_state WHERE source_id = @SourceId AND built_at IS NOT NULL)", new { SourceId = sourceId }, transaction, cancellationToken: cancellationToken)))
        {
            throw new InvalidOperationException("The source has no graph to update.");
        }

        string[] paths = [.. patch.Files.Distinct(StringComparer.Ordinal)];
        Dictionary<string, long> files = (await connection.QueryAsync<(string Path, long DocumentId)>(new CommandDefinition(
            "SELECT path, document_id FROM document WHERE source_id = @SourceId AND path = ANY (@Paths)", new { SourceId = sourceId, Paths = paths }, transaction, cancellationToken: cancellationToken)))
            .ToDictionary(static row => row.Path, static row => row.DocumentId, StringComparer.Ordinal);
        if (paths.FirstOrDefault(path => !files.ContainsKey(path)) is { } missing)
            throw new InvalidOperationException($"The patch names the file {missing}, which is not a document of the source.");

        Dictionary<string, int> storedProjects = (await connection.QueryAsync<(string Path, int ProjectId)>(new CommandDefinition(
            "SELECT path, project_id FROM code_project WHERE source_id = @SourceId", new { SourceId = sourceId }, transaction, cancellationToken: cancellationToken)))
            .ToDictionary(static row => row.Path, static row => row.ProjectId, StringComparer.Ordinal);
        int[] projectIds = new int[graph.Projects.Count];
        for (int i = 0; i < projectIds.Length; i++)
        {
            projectIds[i] = storedProjects.TryGetValue(graph.Projects[i].Path, out int projectId)
                ? projectId
                : throw new InvalidOperationException($"The patch names the project {graph.Projects[i].Path}, which the graph does not have.");
        }

        long[] documentIds = [.. files.Values];
        int[] candidates = [.. await connection.QueryAsync<int>(new CommandDefinition(
            "SELECT DISTINCT symbol_id FROM code_declaration WHERE document_id = ANY (@DocumentIds)", new { DocumentIds = documentIds }, transaction, WriteTimeoutSeconds, cancellationToken: cancellationToken))];
        foreach (string table in new[] { "code_declaration", "code_reference", "code_type_edge", "code_external_edge", "code_member_link" })
        {
            await connection.ExecuteAsync(new CommandDefinition(
                $"DELETE FROM {table} WHERE document_id = ANY (@DocumentIds)", new { DocumentIds = documentIds }, transaction, WriteTimeoutSeconds, cancellationToken: cancellationToken));
        }

        int[] symbolIds = await MergeSymbolsAsync(connection, transaction, sourceId, patch, projectIds, files, cancellationToken);
        await WriteDeclarationsAsync(connection, sourceId, graph, symbolIds, files, cancellationToken);
        await WriteReferencesAsync(connection, sourceId, graph, symbolIds, files, cancellationToken);
        await WriteEdgesAsync(connection, sourceId, graph, symbolIds, files, cancellationToken);
        await DeleteGoneSymbolsAsync(connection, transaction, candidates, cancellationToken);
        await ReplaceDiagnosticsAsync(connection, transaction, sourceId, patch, projectIds, cancellationToken);

        (long projects, long symbols, long references) = await connection.QuerySingleAsync<(long, long, long)>(new CommandDefinition(
            """
            SELECT (SELECT count(*) FROM code_project WHERE source_id = @SourceId),
                   (SELECT count(*) FROM code_symbol WHERE source_id = @SourceId),
                   (SELECT count(*) FROM code_reference WHERE source_id = @SourceId)
            """,
            new { SourceId = sourceId }, transaction, WriteTimeoutSeconds, cancellationToken: cancellationToken));
        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE code_graph_state
            SET input_hash = @InputHash, extractor_version = @ExtractorVersion, error = NULL, failed_at = NULL, updated_at = now(), update_count = update_count + 1,
                update_duration_ms = @DurationMs, project_count = @Projects, symbol_count = @Symbols, reference_count = @References
            WHERE source_id = @SourceId
            """,
            new
            {
                SourceId = sourceId,
                InputHash = inputHash,
                ExtractorVersion = extractorVersion,
                DurationMs = (int)Math.Min(duration.TotalMilliseconds, int.MaxValue),
                Projects = (int)projects,
                Symbols = (int)symbols,
                References = (int)Math.Min(references, int.MaxValue),
            },
            transaction, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        return new CodeGraphCounts((int)projects, (int)symbols, (int)Math.Min(references, int.MaxValue));
    }

    // Returns the key of each symbol of the patch by its index, or -1 for a symbol that the merge drops.
    // A symbol of the patch takes the key of the stored symbol with the same project and documentation ID, updates its fields, or gets a new key. A stub only looks up its key.
    private static async Task<int[]> MergeSymbolsAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, int sourceId, CodeGraphPatch patch, int[] projectIds, Dictionary<string, long> files, CancellationToken cancellationToken)
    {
        CodeGraph graph = patch.Graph;
        int count = graph.Symbols.Count;
        bool[] isStub = new bool[count];
        foreach (int stub in patch.Stubs)
        {
            if (stub >= 0 && stub < count)
                isStub[stub] = true;
        }

        bool[] alive = new bool[count];
        foreach (GraphDeclaration declaration in graph.Declarations)
        {
            if (declaration.Symbol >= 0 && declaration.Symbol < count && !isStub[declaration.Symbol] && files.ContainsKey(declaration.Path))
                alive[declaration.Symbol] = true;
        }

        for (int i = 0; i < count; i++)
        {
            if (graph.Symbols[i].Project < 0 || graph.Symbols[i].Project >= projectIds.Length)
                isStub[i] = alive[i] = false;
        }

        List<int> looked = [];
        for (int i = 0; i < count; i++)
        {
            if (alive[i] || isStub[i])
                looked.Add(i);
        }

        Dictionary<(int, string), int> stored = (await connection.QueryAsync<(int ProjectId, string DocId, int SymbolId)>(new CommandDefinition(
            """
            SELECT s.project_id, s.doc_id, s.symbol_id
            FROM unnest(@Projects, @DocIds) AS k(project_id, doc_id)
            JOIN code_symbol s ON s.project_id = k.project_id AND s.doc_id = k.doc_id
            """,
            new { Projects = looked.Select(i => projectIds[graph.Symbols[i].Project]).ToArray(), DocIds = looked.Select(i => graph.Symbols[i].DocId).ToArray() },
            transaction, WriteTimeoutSeconds, cancellationToken: cancellationToken)))
            .ToDictionary(static row => (row.ProjectId, row.DocId), static row => row.SymbolId);

        HashSet<(int, string)> declared = [.. looked.Where(i => alive[i]).Select(i => (projectIds[graph.Symbols[i].Project], graph.Symbols[i].DocId))];
        foreach (int i in looked.Where(i => isStub[i]))
            alive[i] = stored.ContainsKey((projectIds[graph.Symbols[i].Project], graph.Symbols[i].DocId)) || declared.Contains((projectIds[graph.Symbols[i].Project], graph.Symbols[i].DocId));

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

        int[] ids = new int[count];
        Array.Fill(ids, -1);
        Dictionary<(int, string), int> assigned = [];
        List<int> toInsert = [];
        List<int> toUpdate = [];
        for (int i = 0; i < count; i++)
        {
            if (!alive[i] || isStub[i])
                continue;

            (int, string) key = (projectIds[graph.Symbols[i].Project], graph.Symbols[i].DocId);
            if (assigned.TryGetValue(key, out int known))
            {
                ids[i] = known;
            }
            else if (stored.TryGetValue(key, out int existing))
            {
                ids[i] = assigned[key] = existing;
                toUpdate.Add(i);
            }
            else
            {
                ids[i] = assigned[key] = -2;
                toInsert.Add(i);
            }
        }

        int[] reserved = await ReserveAsync(connection, transaction, "code_symbol", "symbol_id", toInsert.Count, cancellationToken);
        for (int n = 0; n < toInsert.Count; n++)
        {
            int i = toInsert[n];
            ids[i] = reserved[n];
            assigned[(projectIds[graph.Symbols[i].Project], graph.Symbols[i].DocId)] = reserved[n];
        }

        // The symbols that repeat a key of the patch take the key of the first one.
        for (int i = 0; i < count; i++)
        {
            if (alive[i] && !isStub[i] && ids[i] < 0)
                ids[i] = assigned[(projectIds[graph.Symbols[i].Project], graph.Symbols[i].DocId)];
        }

        foreach (int i in looked.Where(i => isStub[i] && alive[i]))
        {
            (int, string) key = (projectIds[graph.Symbols[i].Project], graph.Symbols[i].DocId);
            ids[i] = assigned.TryGetValue(key, out int fromPatch) ? fromPatch : stored.GetValueOrDefault(key, -1);
        }

        await UpdateSymbolsAsync(connection, transaction, graph, toUpdate, ids, cancellationToken);
        await InsertSymbolsAsync(connection, sourceId, graph, toInsert, ids, projectIds, cancellationToken);
        return ids;
    }

    // Writes only the rows that differ from the stored ones, so a patch of a few edited files leaves the other symbols alone.
    private static async Task UpdateSymbolsAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, CodeGraph graph, List<int> indexes, int[] ids, CancellationToken cancellationToken)
    {
        if (indexes.Count == 0)
            return;

        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE code_symbol s
            SET parent_id = u.parent_id, kind = u.kind, name = u.name, full_name = u.full_name, signature = u.signature, namespace = u.namespace,
                accessibility = u.accessibility, modifiers = string_to_array(u.modifiers, ','), summary = u.summary
            FROM unnest(@Ids, @Parents, @Kinds, @Names, @FullNames, @Signatures, @Namespaces, @Accessibilities, @Modifiers, @Summaries)
                AS u(symbol_id, parent_id, kind, name, full_name, signature, namespace, accessibility, modifiers, summary)
            WHERE s.symbol_id = u.symbol_id
              AND (s.parent_id, s.kind, s.name, s.full_name, s.signature, s.namespace, s.accessibility, s.modifiers, s.summary)
                  IS DISTINCT FROM (u.parent_id, u.kind, u.name, u.full_name, u.signature, u.namespace, u.accessibility, string_to_array(u.modifiers, ','), u.summary)
            """,
            new
            {
                Ids = indexes.Select(i => ids[i]).ToArray(),
                Parents = indexes.Select(i => ParentId(graph.Symbols[i], ids)).ToArray(),
                Kinds = indexes.Select(i => graph.Symbols[i].Kind).ToArray(),
                Names = indexes.Select(i => graph.Symbols[i].Name).ToArray(),
                FullNames = indexes.Select(i => graph.Symbols[i].FullName).ToArray(),
                Signatures = indexes.Select(i => graph.Symbols[i].Signature).ToArray(),
                Namespaces = indexes.Select(i => graph.Symbols[i].Namespace).ToArray(),
                Accessibilities = indexes.Select(i => graph.Symbols[i].Accessibility).ToArray(),
                Modifiers = indexes.Select(i => string.Join(',', graph.Symbols[i].Modifiers)).ToArray(),
                Summaries = indexes.Select(i => graph.Symbols[i].Summary).ToArray(),
            },
            transaction, WriteTimeoutSeconds, cancellationToken: cancellationToken));
    }

    private static async Task InsertSymbolsAsync(
        NpgsqlConnection connection, int sourceId, CodeGraph graph, List<int> indexes, int[] ids, int[] projectIds, CancellationToken cancellationToken)
    {
        if (indexes.Count == 0)
            return;

        await using NpgsqlBinaryImporter writer = await connection.BeginBinaryImportAsync(
            "COPY code_symbol (symbol_id, source_id, project_id, parent_id, kind, name, full_name, doc_id, signature, namespace, accessibility, modifiers, summary) FROM STDIN (FORMAT BINARY)",
            cancellationToken);
        foreach (int i in indexes)
        {
            GraphSymbol symbol = graph.Symbols[i];
            await writer.StartRowAsync(cancellationToken);
            await writer.WriteAsync(ids[i], NpgsqlDbType.Integer, cancellationToken);
            await writer.WriteAsync(sourceId, NpgsqlDbType.Integer, cancellationToken);
            await writer.WriteAsync(projectIds[symbol.Project], NpgsqlDbType.Integer, cancellationToken);
            if (ParentId(symbol, ids) is { } parentId)
                await writer.WriteAsync(parentId, NpgsqlDbType.Integer, cancellationToken);
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
    }

    private static int? ParentId(GraphSymbol symbol, int[] ids) => symbol.Parent is { } parent && parent >= 0 && parent < ids.Length && ids[parent] >= 0 ? ids[parent] : null;

    // A symbol that lost its last declaration is gone, with the symbols inside it and every row that points to it.
    private static async Task DeleteGoneSymbolsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, int[] candidates, CancellationToken cancellationToken)
    {
        if (candidates.Length == 0)
            return;

        int[] gone = [.. await connection.QueryAsync<int>(new CommandDefinition(
            """
            WITH RECURSIVE gone(symbol_id) AS (
                SELECT c.symbol_id FROM unnest(@Candidates) AS c(symbol_id)
                WHERE NOT EXISTS (SELECT 1 FROM code_declaration d WHERE d.symbol_id = c.symbol_id)
                UNION
                SELECT s.symbol_id FROM code_symbol s JOIN gone g ON s.parent_id = g.symbol_id
            )
            SELECT symbol_id FROM gone
            """,
            new { Candidates = candidates }, transaction, WriteTimeoutSeconds, cancellationToken: cancellationToken))];
        if (gone.Length == 0)
            return;

        foreach (string statement in new[]
        {
            "DELETE FROM code_reference WHERE symbol_id = ANY (@Gone)",
            "DELETE FROM code_reference WHERE from_id = ANY (@Gone)",
            "DELETE FROM code_declaration WHERE symbol_id = ANY (@Gone)",
            "DELETE FROM code_type_edge WHERE from_id = ANY (@Gone)",
            "DELETE FROM code_type_edge WHERE to_id = ANY (@Gone)",
            "DELETE FROM code_external_edge WHERE from_id = ANY (@Gone)",
            "DELETE FROM code_member_link WHERE member_id = ANY (@Gone)",
            "DELETE FROM code_member_link WHERE target_id = ANY (@Gone)",
            "DELETE FROM code_symbol WHERE symbol_id = ANY (@Gone)",
        })
        {
            await connection.ExecuteAsync(new CommandDefinition(statement, new { Gone = gone }, transaction, WriteTimeoutSeconds, cancellationToken: cancellationToken));
        }
    }

    // The error counts and the diagnostics of the projects that the patch checked replace the stored ones. The other projects keep theirs.
    private static async Task ReplaceDiagnosticsAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, int sourceId, CodeGraphPatch patch, int[] projectIds, CancellationToken cancellationToken)
    {
        int[] checkedProjects = [.. patch.CheckedProjects.Where(project => project >= 0 && project < projectIds.Length).Order()];
        if (checkedProjects.Length == 0)
            return;

        int[] keys = [.. checkedProjects.Select(project => projectIds[project])];
        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM code_diagnostic WHERE source_id = @SourceId AND project_id = ANY (@Keys)", new { SourceId = sourceId, Keys = keys }, transaction, cancellationToken: cancellationToken));
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE code_project p SET error_count = u.error_count FROM unnest(@Keys, @Errors) AS u(project_id, error_count) WHERE p.project_id = u.project_id",
            new { Keys = keys, Errors = checkedProjects.Select(project => patch.Graph.Projects[project].ErrorCount).ToArray() }, transaction, cancellationToken: cancellationToken));
        CodeGraph own = patch.Graph with { Diagnostics = [.. patch.Graph.Diagnostics.Where(diagnostic => diagnostic.Project is { } project && patch.CheckedProjects.Contains(project))] };
        await WriteDiagnosticsAsync(connection, sourceId, own, projectIds, cancellationToken);
    }
}
