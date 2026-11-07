using System.Text;
using System.Text.RegularExpressions;

using Dapper;

using Npgsql;

using ProjectRag.Core.Sources;
using ProjectRag.Core.Storage;

namespace ProjectRag.Core.Code.Graph;

/// <summary>
/// The queries over the code graph: the state, the symbol search, the detail of a symbol, the references, the outline of a file and the lookup of a symbol by text.
/// </summary>
/// <remarks>
/// The service reads the tables that <see cref="CodeGraphStore"/> writes. It knows no Roslyn.
/// The relations and the maps for the MCP tools are in the other part of the class.
/// </remarks>
/// <param name="database">The database.</param>
/// <param name="sources">The store of the sources.</param>
public sealed partial class CodeGraphService(Database database, SourceStore sources)
{
    /// <summary>The largest number of symbols that a search returns.</summary>
    public const int MaxSearchLimit = 200;

    /// <summary>The largest number of references on a page.</summary>
    public const int MaxReferenceLimit = 500;

    private const int MaxEdges = 200;
    private const int MaxExternal = 100;
    private const int MaxDiagnostics = 200;

    private const string SymbolColumns = """
        s.symbol_id, s.source_id, src.name AS source, s.project_id, p.name AS project, s.kind, s.name, s.full_name, s.doc_id, s.signature, s.accessibility,
        left(s.summary, 300) AS summary, d.document_id, d.path, d.name_line AS line
        """;

    private const string SymbolFrom = """
        FROM code_symbol s
        JOIN code_project p ON p.project_id = s.project_id
        JOIN source src ON src.source_id = s.source_id
        LEFT JOIN LATERAL (
            SELECT cd.document_id, doc.path, cd.name_line
            FROM code_declaration cd
            JOIN document doc ON doc.document_id = cd.document_id
            WHERE cd.symbol_id = s.symbol_id
            ORDER BY doc.path COLLATE "C", cd.start_line
            LIMIT 1) d ON true
        """;

    private static readonly string[] TypeKinds = [.. CodeSymbolKinds.Types];

    /// <summary>
    /// Reads the state of the graph of a code source with its projects, solutions and problems.
    /// </summary>
    /// <param name="sourceId">The key of the code source.</param>
    /// <param name="cancellationToken">The token that cancels the queries.</param>
    /// <returns>The status, or <see langword="null"/> when the source does not exist.</returns>
    /// <exception cref="CodeSearchException">The source is not a code source.</exception>
    public async Task<CodeGraphStatus?> GetStatusAsync(int sourceId, CancellationToken cancellationToken)
    {
        if (await sources.GetAsync(sourceId, null, cancellationToken) is not { } source)
            return null;

        if (source.Kind != SourceKind.Code)
            throw new CodeSearchException("sourceId", $"The source {source.Name} is not a code source.");

        await using NpgsqlConnection connection = await database.OpenAsync(cancellationToken);
        StateRow? state = await connection.QuerySingleOrDefaultAsync<StateRow>(new CommandDefinition(
            "SELECT built_at, duration_ms, updated_at, update_count, update_duration_ms, sdk_version, error, project_count, symbol_count, reference_count FROM code_graph_state WHERE source_id = @SourceId",
            new { SourceId = sourceId }, cancellationToken: cancellationToken));

        List<ProjectRow> projectRows = [.. await connection.QueryAsync<ProjectRow>(new CommandDefinition(
            """
            SELECT p.project_id, p.path, p.name, p.target_framework, p.is_test, p.document_count, p.error_count,
                   (SELECT count(*) FROM code_symbol s WHERE s.project_id = p.project_id) AS symbol_count
            FROM code_project p
            WHERE p.source_id = @SourceId
            ORDER BY p.name COLLATE "C", p.path COLLATE "C"
            """,
            new { SourceId = sourceId }, cancellationToken: cancellationToken))];
        ILookup<int, int> references = (await connection.QueryAsync<(int ProjectId, int ReferencedId)>(new CommandDefinition(
            "SELECT project_id, referenced_id FROM code_project_reference WHERE source_id = @SourceId ORDER BY project_id, referenced_id",
            new { SourceId = sourceId }, cancellationToken: cancellationToken))).ToLookup(static row => row.ProjectId, static row => row.ReferencedId);
        IReadOnlyList<CodeProject> projects = [.. projectRows.Select(row => new CodeProject(
            row.ProjectId, row.Path, row.Name, row.TargetFramework, row.IsTest, row.DocumentCount, row.ErrorCount, (int)row.SymbolCount, [.. references[row.ProjectId]]))];

        ILookup<int, int> solutionProjects = (await connection.QueryAsync<(int SolutionId, int ProjectId)>(new CommandDefinition(
            "SELECT solution_id, project_id FROM code_solution_project WHERE source_id = @SourceId ORDER BY solution_id, project_id",
            new { SourceId = sourceId }, cancellationToken: cancellationToken))).ToLookup(static row => row.SolutionId, static row => row.ProjectId);
        IReadOnlyList<CodeSolution> solutions = [.. (await connection.QueryAsync<(int SolutionId, string Path)>(new CommandDefinition(
            "SELECT solution_id, path FROM code_solution WHERE source_id = @SourceId ORDER BY path COLLATE \"C\"", new { SourceId = sourceId }, cancellationToken: cancellationToken)))
            .Select(row => new CodeSolution(row.SolutionId, row.Path, [.. solutionProjects[row.SolutionId]]))];

        IReadOnlyList<CodeDiagnostic> diagnostics = [.. (await connection.QueryAsync<DiagnosticRow>(new CommandDefinition(
            """
            SELECT p.name AS project, d.severity, d.code, d.message, d.path, d.line
            FROM code_diagnostic d
            LEFT JOIN code_project p ON p.project_id = d.project_id
            WHERE d.source_id = @SourceId
            ORDER BY (d.severity = 'error') DESC, p.name COLLATE "C", d.path COLLATE "C", d.line
            LIMIT @Limit
            """,
            new { SourceId = sourceId, Limit = MaxDiagnostics }, cancellationToken: cancellationToken)))
            .Select(static row => new CodeDiagnostic(row.Project, row.Severity, row.Code, row.Message, row.Path, row.Line))];

        return new CodeGraphStatus(
            sourceId, source.Name, state?.BuiltAt, state?.DurationMs, state?.UpdatedAt, state?.UpdateCount ?? 0, state?.UpdateDurationMs, state?.SdkVersion, state?.Error,
            state?.ProjectCount ?? 0, state?.SymbolCount ?? 0, state?.ReferenceCount ?? 0, projects, solutions, diagnostics);
    }

    /// <summary>
    /// Searches the symbols by a name, as "Go to symbol" of an IDE does.
    /// </summary>
    /// <remarks>
    /// The classes of a match come in the order of <see cref="CodeMatchKinds.All"/>. A symbol appears once, in its first class.
    /// Within a class the types come first, then the shorter names. The fuzzy class orders by the similarity of the trigrams.
    /// </remarks>
    /// <param name="query">The name, a part of it, the humps of it (<c>SSync</c>), or a qualified name (<c>SceneHost.Update</c>).</param>
    /// <param name="sourceId">The key of the code source, or <see langword="null"/> for all code sources.</param>
    /// <param name="kind">A kind of symbol, or <c>type</c> for all kinds of types. <see langword="null"/> means all kinds.</param>
    /// <param name="project">The name of an assembly, or <see langword="null"/> for all projects.</param>
    /// <param name="limit">The most symbols, from 1 to <see cref="MaxSearchLimit"/>.</param>
    /// <param name="cancellationToken">The token that cancels the queries.</param>
    /// <returns>The matches in the order of the classes.</returns>
    /// <exception cref="CodeSearchException">The query is empty.</exception>
    public Task<IReadOnlyList<SymbolMatch>> SearchSymbolsAsync(string query, int? sourceId, string? kind, string? project, int limit, CancellationToken cancellationToken) =>
        SearchSymbolsAsync(query, sourceId, kind, project, limit, null, cancellationToken);

    /// <summary>
    /// Searches the symbols by a name with the classes of match that the caller allows.
    /// </summary>
    /// <param name="query">The name, a part of it, the humps of it (<c>SSync</c>), or a qualified name (<c>SceneHost.Update</c>).</param>
    /// <param name="sourceId">The key of the code source, or <see langword="null"/> for all code sources.</param>
    /// <param name="kind">A kind of symbol, or <c>type</c> for all kinds of types. <see langword="null"/> means all kinds.</param>
    /// <param name="project">The name of an assembly, or <see langword="null"/> for all projects.</param>
    /// <param name="limit">The most symbols, from 1 to <see cref="MaxSearchLimit"/>.</param>
    /// <param name="matchKinds">The classes of match to run, from <see cref="CodeMatchKinds"/>, or <see langword="null"/> for all classes.</param>
    /// <param name="cancellationToken">The token that cancels the queries.</param>
    /// <returns>The matches in the order of the classes.</returns>
    /// <exception cref="CodeSearchException">The query is empty.</exception>
    public async Task<IReadOnlyList<SymbolMatch>> SearchSymbolsAsync(string query, int? sourceId, string? kind, string? project, int limit, IReadOnlyCollection<string>? matchKinds, CancellationToken cancellationToken)
    {
        query = query?.Trim() ?? string.Empty;
        if (query.Length == 0)
            throw new CodeSearchException("q", "The query must not be empty.");

        limit = Math.Clamp(limit, 1, MaxSearchLimit);
        string escaped = CodeSearchStore.EscapeLike(query);
        string? humps = CodeHumps.BuildPattern(query);
        bool qualified = query.Contains('.', StringComparison.Ordinal);

        StringBuilder filter = new();
        DynamicParameters parameters = new();
        parameters.Add("Q", query);
        parameters.Add("TypeKinds", TypeKinds);
        if (sourceId is { } id)
        {
            filter.Append(" AND s.source_id = @SourceId");
            parameters.Add("SourceId", id);
        }

        if (!string.IsNullOrWhiteSpace(kind))
        {
            if (kind.Trim().Equals("type", StringComparison.OrdinalIgnoreCase))
            {
                filter.Append(" AND s.kind = ANY (@TypeKinds)");
            }
            else
            {
                filter.Append(" AND s.kind = @Kind");
                parameters.Add("Kind", kind.Trim().ToLowerInvariant());
            }
        }

        if (!string.IsNullOrWhiteSpace(project))
        {
            filter.Append(" AND s.project_id IN (SELECT project_id FROM code_project WHERE name = @Project)");
            parameters.Add("Project", project.Trim());
        }

        parameters.Add("Prefix", escaped + "%");
        parameters.Add("Contains", "%" + escaped + "%");
        parameters.Add("Suffix", "%." + escaped);
        parameters.Add("Humps", humps);
        parameters.Add("Escaped", escaped);

        List<(string Match, string Condition, bool Similarity)> stages =
        [
            (CodeMatchKinds.Exact, "s.name = @Q", false),
            (CodeMatchKinds.ExactIgnoreCase, "lower(s.name) = lower(@Q)", false),
            (CodeMatchKinds.Prefix, "s.name ILIKE @Prefix", false),
        ];
        if (humps is not null)
            stages.Add((CodeMatchKinds.Humps, "s.name ~ @Humps", false));

        if (qualified)
            stages.Add((CodeMatchKinds.Qualified, "(s.full_name ILIKE @Escaped OR s.full_name ILIKE @Suffix)", false));

        stages.Add((CodeMatchKinds.Substring, "s.name ILIKE @Contains", false));
        stages.Add((CodeMatchKinds.Fuzzy, "s.name % @Q", true));

        List<(int SymbolId, string Match)> found = [];
        HashSet<int> seen = [];
        await using NpgsqlConnection connection = await database.OpenAsync(cancellationToken);
        foreach ((string match, string condition, bool similarity) in stages)
        {
            if (found.Count >= limit)
                break;

            if (matchKinds is not null && !matchKinds.Contains(match))
                continue;

            parameters.Add("Seen", seen.ToArray());
            parameters.Add("Limit", limit - found.Count);
            // The collation "C" keeps the order the same on every server. A linguistic collation of the database can skip the dots of the names.
            string order = similarity
                ? "similarity(s.name, @Q) DESC, (s.kind = ANY (@TypeKinds)) DESC, length(s.name), s.full_name COLLATE \"C\", s.doc_id COLLATE \"C\""
                : "(s.kind = ANY (@TypeKinds)) DESC, length(s.name), s.full_name COLLATE \"C\", s.doc_id COLLATE \"C\"";
            IEnumerable<int> ids = await connection.QueryAsync<int>(new CommandDefinition(
                $"SELECT s.symbol_id FROM code_symbol s WHERE {condition}{filter} AND NOT (s.symbol_id = ANY (@Seen)) ORDER BY {order} LIMIT @Limit",
                parameters, cancellationToken: cancellationToken));
            foreach (int symbolId in ids)
            {
                if (seen.Add(symbolId))
                    found.Add((symbolId, match));
            }
        }

        Dictionary<int, SymbolSummary> summaries = await LoadSummariesAsync(connection, found.Select(static item => item.SymbolId).ToArray(), cancellationToken);
        return [.. found.Where(item => summaries.ContainsKey(item.SymbolId)).Select(item => new SymbolMatch(summaries[item.SymbolId], item.Match))];
    }

    /// <summary>
    /// Reads a symbol with its relations.
    /// </summary>
    /// <param name="symbolId">The key of the symbol.</param>
    /// <param name="cancellationToken">The token that cancels the queries.</param>
    /// <returns>The detail, or <see langword="null"/> when the symbol does not exist.</returns>
    public async Task<SymbolDetail?> GetDetailAsync(int symbolId, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await database.OpenAsync(cancellationToken);
        return await BuildDetailAsync(connection, symbolId, cancellationToken);
    }

    /// <summary>
    /// Reads a symbol by its documentation comment ID, which survives a new build of the graph.
    /// </summary>
    /// <param name="sourceId">The key of the code source.</param>
    /// <param name="docId">The documentation comment ID.</param>
    /// <param name="project">The name of the assembly, or <see langword="null"/>. Without it the first project by name wins.</param>
    /// <param name="cancellationToken">The token that cancels the queries.</param>
    /// <returns>The detail, or <see langword="null"/> when there is no such symbol.</returns>
    public async Task<SymbolDetail?> GetDetailAsync(int sourceId, string docId, string? project, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await database.OpenAsync(cancellationToken);
        int? symbolId = await connection.ExecuteScalarAsync<int?>(new CommandDefinition(
            """
            SELECT s.symbol_id
            FROM code_symbol s
            JOIN code_project p ON p.project_id = s.project_id
            WHERE s.source_id = @SourceId AND s.doc_id = @DocId AND (@Project::text IS NULL OR p.name = @Project)
            ORDER BY p.name COLLATE "C", s.symbol_id
            LIMIT 1
            """,
            new { SourceId = sourceId, DocId = docId, Project = string.IsNullOrWhiteSpace(project) ? null : project.Trim() }, cancellationToken: cancellationToken));
        return symbolId is { } id ? await BuildDetailAsync(connection, id, cancellationToken) : null;
    }

    /// <summary>
    /// Reads a page of the references to one symbol.
    /// </summary>
    /// <param name="symbolId">The key of the symbol.</param>
    /// <param name="kind">The kind of the use, or <see langword="null"/> for all kinds.</param>
    /// <param name="offset">The number of references to skip.</param>
    /// <param name="limit">The most references, from 1 to <see cref="MaxReferenceLimit"/>.</param>
    /// <param name="cancellationToken">The token that cancels the queries.</param>
    /// <returns>The page, or <see langword="null"/> when the symbol does not exist.</returns>
    public async Task<CodeReferencePage?> GetReferencesAsync(int symbolId, string? kind, int offset, int limit, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await database.OpenAsync(cancellationToken);
        if (!await connection.ExecuteScalarAsync<bool>(new CommandDefinition("SELECT EXISTS (SELECT 1 FROM code_symbol WHERE symbol_id = @SymbolId)", new { SymbolId = symbolId }, cancellationToken: cancellationToken)))
            return null;

        return await GetReferencesAsync(connection, [symbolId], kind, null, offset, limit, cancellationToken);
    }

    /// <summary>
    /// Reads a page of the references to several symbols, for example the overloads of a method.
    /// </summary>
    /// <param name="symbolIds">The keys of the symbols.</param>
    /// <param name="kind">The kind of the use, or <see langword="null"/> for all kinds.</param>
    /// <param name="path">A file or a folder (with a final slash) to keep, or <see langword="null"/> for all files.</param>
    /// <param name="offset">The number of references to skip.</param>
    /// <param name="limit">The most references, from 1 to <see cref="MaxReferenceLimit"/>.</param>
    /// <param name="cancellationToken">The token that cancels the queries.</param>
    /// <returns>The page.</returns>
    public async Task<CodeReferencePage> GetReferencesAsync(IReadOnlyCollection<int> symbolIds, string? kind, string? path, int offset, int limit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(symbolIds);
        await using NpgsqlConnection connection = await database.OpenAsync(cancellationToken);
        return await GetReferencesAsync(connection, symbolIds, kind, path, offset, limit, cancellationToken);
    }

    /// <summary>
    /// Reads the symbols that a file declares as a tree.
    /// </summary>
    /// <param name="documentId">The key of the document.</param>
    /// <param name="cancellationToken">The token that cancels the queries.</param>
    /// <returns>The roots in the order of their lines. The place of each node is its first declaration in this file. A file without symbols gives an empty list.</returns>
    public async Task<IReadOnlyList<OutlineNode>> GetOutlineAsync(long documentId, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await database.OpenAsync(cancellationToken);
        List<OutlineRow> rows = [.. await connection.QueryAsync<OutlineRow>(new CommandDefinition(
            $"""
            SELECT {SymbolColumns.Replace("d.document_id, d.path, d.name_line AS line", "cd.document_id, doc.path, cd.name_line AS line", StringComparison.Ordinal)}, s.parent_id
            FROM code_declaration cd
            JOIN document doc ON doc.document_id = cd.document_id
            JOIN code_symbol s ON s.symbol_id = cd.symbol_id
            JOIN code_project p ON p.project_id = s.project_id
            JOIN source src ON src.source_id = s.source_id
            WHERE cd.document_id = @DocumentId
            ORDER BY cd.start_line, cd.name_line, cd.name_column
            """,
            new { DocumentId = documentId }, cancellationToken: cancellationToken))];

        Dictionary<int, OutlineBuilder> nodes = [];
        List<OutlineBuilder> roots = [];
        foreach (OutlineRow row in rows)
        {
            if (!nodes.ContainsKey(row.SymbolId))
                nodes[row.SymbolId] = new OutlineBuilder(row.ToSummary());
        }

        HashSet<int> placed = [];
        foreach (OutlineRow row in rows)
        {
            if (!placed.Add(row.SymbolId))
                continue;

            OutlineBuilder node = nodes[row.SymbolId];
            if (row.ParentId is { } parent && parent != row.SymbolId && nodes.TryGetValue(parent, out OutlineBuilder? owner))
                owner.Children.Add(node);
            else
                roots.Add(node);
        }

        return [.. roots.Select(static node => node.Build())];
    }

    /// <summary>
    /// Finds a code source for a tool: by its name, or the only code source when the name is missing.
    /// </summary>
    /// <param name="codebase">The name of the code source, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">The token that cancels the queries.</param>
    /// <returns>The source.</returns>
    /// <exception cref="CodeSearchException">The name is unknown, the source is no code source, or the name is missing and the knowledge base has no or several code sources.</exception>
    public async Task<Source> FindCodebaseAsync(string? codebase, CancellationToken cancellationToken)
    {
        IReadOnlyList<Source> all = await sources.ListAsync(null, cancellationToken);
        if (!string.IsNullOrWhiteSpace(codebase))
        {
            Source named = all.FirstOrDefault(source => source.Name == codebase.Trim())
                ?? throw new CodeSearchException("codebase", $"The knowledge base has no source {codebase}. Call list_sources.");
            return named.Kind == SourceKind.Code ? named : throw new CodeSearchException("codebase", $"The source {codebase} is not a code source.");
        }

        Source[] code = [.. all.Where(static source => source.Kind == SourceKind.Code)];
        return code.Length switch
        {
            1 => code[0],
            0 => throw new CodeSearchException("codebase", "The knowledge base has no code source. Add one in the dashboard."),
            _ => throw new CodeSearchException("codebase", $"The knowledge base has several code sources. Name one in codebase: {string.Join(", ", code.Select(static source => source.Name))}."),
        };
    }

    /// <summary>
    /// Finds a code source for a tool and makes sure that it has a graph.
    /// </summary>
    /// <param name="codebase">The name of the code source, or <see langword="null"/> when the knowledge base has one.</param>
    /// <param name="cancellationToken">The token that cancels the queries.</param>
    /// <returns>The source.</returns>
    /// <exception cref="CodeSearchException">The source is missing, or it has no graph. The message tells what to do.</exception>
    public async Task<Source> RequireGraphAsync(string? codebase, CancellationToken cancellationToken)
    {
        Source source = await FindCodebaseAsync(codebase, cancellationToken);
        await using NpgsqlConnection connection = await database.OpenAsync(cancellationToken);
        StateRow? state = await connection.QuerySingleOrDefaultAsync<StateRow>(new CommandDefinition(
            "SELECT built_at, error FROM code_graph_state WHERE source_id = @SourceId", new { source.SourceId }, cancellationToken: cancellationToken));
        if (state?.BuiltAt is not null)
            return source;

        string reason = state?.Error is { } error
            ? $"The last build failed: {error}"
            : "The indexer has not built it. It builds the graph when the server has a code extractor and the folder holds a .csproj file.";
        throw new CodeSearchException("codebase", $"The code source {source.Name} has no code graph. {reason} Call index_source to build it, and use code_grep and code_read meanwhile.");
    }

    /// <summary>
    /// Finds the symbols that a text names.
    /// </summary>
    /// <remarks>
    /// The text is a documentation comment ID (<c>T:Ns.Type</c>, <c>M:Ns.Type.Method</c>), a full name, a qualified suffix (<c>SceneHost.Update</c>) or a simple name.
    /// The first kind of match that finds a symbol wins. Overloads, which share the project and the full name, make one group.
    /// A constructor does not compete with its type.
    /// </remarks>
    /// <param name="codebase">The name of the code source, or <see langword="null"/> when the knowledge base has one.</param>
    /// <param name="text">The text.</param>
    /// <param name="cancellationToken">The token that cancels the queries.</param>
    /// <returns>The source and the groups. The list is empty when nothing matches.</returns>
    /// <exception cref="CodeSearchException">The source is missing, or it has no graph.</exception>
    public async Task<CodeResolution> ResolveAsync(string? codebase, string text, CancellationToken cancellationToken)
    {
        Source source = await RequireGraphAsync(codebase, cancellationToken);
        return new CodeResolution(source, await ResolveAsync(source.SourceId, text, cancellationToken));
    }

    /// <summary>
    /// Finds the symbols that a text names in a code source.
    /// </summary>
    /// <param name="sourceId">The key of the code source.</param>
    /// <param name="text">The text, see <see cref="ResolveAsync(string?, string, CancellationToken)"/>.</param>
    /// <param name="cancellationToken">The token that cancels the queries.</param>
    /// <returns>The groups. The list is empty when nothing matches.</returns>
    public async Task<IReadOnlyList<CodeSymbolGroup>> ResolveAsync(int sourceId, string text, CancellationToken cancellationToken)
    {
        text = text?.Trim() ?? string.Empty;
        if (text.Length == 0)
            throw new CodeSearchException("symbol", "The symbol must not be empty.");

        await using NpgsqlConnection connection = await database.OpenAsync(cancellationToken);
        List<int> ids = [];
        if (DocIdRegex().IsMatch(text))
            ids = await ResolveIdsAsync(connection, sourceId, "s.doc_id = @Plain OR s.doc_id LIKE @Pattern || '(%' OR s.doc_id LIKE @Pattern || '`%'", text, cancellationToken);

        if (ids.Count == 0)
        {
            string name = StripNoise(text);
            ids = await ResolveIdsAsync(connection, sourceId, "s.full_name = @Plain", name, cancellationToken);
            if (ids.Count == 0 && name.Contains('.', StringComparison.Ordinal))
                ids = await ResolveIdsAsync(connection, sourceId, "s.full_name LIKE '%.' || @Pattern", name, cancellationToken);

            if (ids.Count == 0 && !name.Contains('.', StringComparison.Ordinal))
                ids = await ResolveIdsAsync(connection, sourceId, "s.name = @Plain", name, cancellationToken);

            if (ids.Count == 0)
                ids = await ResolveIdsAsync(connection, sourceId, "lower(s.full_name) = lower(@Plain) OR lower(s.full_name) LIKE '%.' || lower(@Pattern) OR lower(s.name) = lower(@Plain)", name, cancellationToken);
        }
        Dictionary<int, SymbolSummary> summaries = await LoadSummariesAsync(connection, ids, cancellationToken);
        List<SymbolSummary> found = [.. ids.Where(summaries.ContainsKey).Select(id => summaries[id])];
        if (found.Any(static symbol => symbol.Kind is not (CodeSymbolKinds.Constructor or CodeSymbolKinds.Destructor)))
            found.RemoveAll(static symbol => symbol.Kind is CodeSymbolKinds.Constructor or CodeSymbolKinds.Destructor);

        return [.. found
            .GroupBy(static symbol => (symbol.ProjectId, symbol.FullName, IsType: CodeSymbolKinds.IsType(symbol.Kind)))
            .Select(static group => new CodeSymbolGroup([.. group.OrderBy(static symbol => symbol.DocId, StringComparer.Ordinal)]))
            .OrderBy(static group => group.Primary.Project, StringComparer.Ordinal)
            .ThenBy(static group => group.Primary.FullName, StringComparer.Ordinal)];
    }

    /// <summary>
    /// Finds the one group of symbols that a text names.
    /// </summary>
    /// <param name="codebase">The name of the code source, or <see langword="null"/> when the knowledge base has one.</param>
    /// <param name="text">The text, see <see cref="ResolveAsync(string?, string, CancellationToken)"/>.</param>
    /// <param name="cancellationToken">The token that cancels the queries.</param>
    /// <returns>The source and the group.</returns>
    /// <exception cref="CodeSearchException">The source or the graph is missing, nothing matches, or several groups match. The message lists up to 10 candidates with their IDs.</exception>
    public async Task<(Source Source, CodeSymbolGroup Group)> ResolveOneAsync(string? codebase, string text, CancellationToken cancellationToken)
    {
        CodeResolution resolution = await ResolveAsync(codebase, text, cancellationToken);
        return resolution.Groups.Count switch
        {
            1 => (resolution.Source, resolution.Groups[0]),
            0 => throw new CodeSearchException("symbol", $"The code source {resolution.Source.Name} has no symbol {text}. Call code_find_symbol to search by a part of the name."),
            _ => throw new CodeSearchException("symbol", CodeGraphFormatter.Ambiguous(text, resolution.Groups)),
        };
    }

    // The condition uses @Plain for an equality and @Pattern, the same text with the marks of LIKE escaped, for a pattern.
    private static async Task<List<int>> ResolveIdsAsync(NpgsqlConnection connection, int sourceId, string condition, string text, CancellationToken cancellationToken) =>
        [.. await connection.QueryAsync<int>(new CommandDefinition(
            $"SELECT s.symbol_id FROM code_symbol s WHERE s.source_id = @SourceId AND ({condition}) ORDER BY s.symbol_id LIMIT 200",
            new { SourceId = sourceId, Plain = text, Pattern = CodeSearchStore.EscapeLike(text) }, cancellationToken: cancellationToken))];
    // An agent may write List<T> or Update(double). The graph names have neither.
    private static string StripNoise(string text)
    {
        int paren = text.IndexOf('(', StringComparison.Ordinal);
        if (paren >= 0)
            text = text[..paren];

        return GenericRegex().Replace(text, string.Empty).Trim();
    }

    private static async Task<SymbolDetail?> BuildDetailAsync(NpgsqlConnection connection, int symbolId, CancellationToken cancellationToken)
    {
        Dictionary<int, SymbolSummary> self = await LoadSummariesAsync(connection, [symbolId], cancellationToken);
        if (!self.TryGetValue(symbolId, out SymbolSummary? symbol))
            return null;

        (string[] modifiers, int? parentId) = await connection.QuerySingleAsync<(string[] Modifiers, int? ParentId)>(new CommandDefinition(
            "SELECT modifiers, parent_id FROM code_symbol WHERE symbol_id = @SymbolId", new { SymbolId = symbolId }, cancellationToken: cancellationToken));
        SymbolSummary? parent = parentId is { } pid ? (await LoadSummariesAsync(connection, [pid], cancellationToken)).GetValueOrDefault(pid) : null;
        IReadOnlyList<CodeDeclaration> declarations = [.. (await connection.QueryAsync<DeclarationRow>(new CommandDefinition(
            """
            SELECT cd.document_id, doc.path, cd.start_line, cd.end_line, cd.name_line, cd.name_column
            FROM code_declaration cd
            JOIN document doc ON doc.document_id = cd.document_id
            WHERE cd.symbol_id = @SymbolId
            ORDER BY doc.path COLLATE "C", cd.start_line
            """,
            new { SymbolId = symbolId }, cancellationToken: cancellationToken)))
            .Select(static row => new CodeDeclaration(row.DocumentId, row.Path, row.StartLine, row.EndLine, row.NameLine, row.NameColumn))];
        int referenceCount = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT count(*)::integer FROM code_reference WHERE symbol_id = @SymbolId", new { SymbolId = symbolId }, cancellationToken: cancellationToken));

        int[] ids = [symbolId];
        bool isType = CodeSymbolKinds.IsType(symbol.Kind);
        IReadOnlyList<UseRow> uses = isType ? await TypeUsesAsync(connection, ids, cancellationToken) : await MemberUsesAsync(connection, ids, cancellationToken);
        IReadOnlyList<UseRow> usedBy = isType ? await TypeUsedByAsync(connection, ids, cancellationToken) : await MemberUsedByAsync(connection, ids, cancellationToken);
        List<(int Other, List<UseCount> Kinds, int Total)> usesGrouped = [.. GroupUses(uses, ids).Take(MaxEdges)];
        List<(int Other, List<UseCount> Kinds, int Total)> usedByGrouped = [.. GroupUses(usedBy, ids).Take(MaxEdges)];

        IReadOnlyList<int> memberIds = [];
        IReadOnlyList<(int Id, string Kind)> baseIds = [];
        IReadOnlyList<(int Id, string Kind)> derivedIds = [];
        IReadOnlyList<int> overrideIds = [];
        IReadOnlyList<int> overriddenByIds = [];
        IReadOnlyList<ExternalUse> external = [];
        if (isType)
        {
            memberIds = [.. await connection.QueryAsync<int>(new CommandDefinition("SELECT symbol_id FROM code_symbol WHERE parent_id = @SymbolId", new { SymbolId = symbolId }, cancellationToken: cancellationToken))];
            baseIds = [.. await connection.QueryAsync<(int, string)>(new CommandDefinition(
                "SELECT DISTINCT to_id, kind FROM code_type_edge WHERE from_id = @SymbolId AND kind IN ('inherit', 'implement') AND to_id <> @SymbolId", new { SymbolId = symbolId }, cancellationToken: cancellationToken))];
            derivedIds = [.. await connection.QueryAsync<(int, string)>(new CommandDefinition(
                "SELECT DISTINCT from_id, kind FROM code_type_edge WHERE to_id = @SymbolId AND kind IN ('inherit', 'implement') AND from_id <> @SymbolId", new { SymbolId = symbolId }, cancellationToken: cancellationToken))];
            external = await LoadExternalAsync(connection, ids, cancellationToken);
        }
        else
        {
            overrideIds = [.. await connection.QueryAsync<int>(new CommandDefinition("SELECT DISTINCT target_id FROM code_member_link WHERE member_id = @SymbolId", new { SymbolId = symbolId }, cancellationToken: cancellationToken))];
            overriddenByIds = [.. await connection.QueryAsync<int>(new CommandDefinition("SELECT DISTINCT member_id FROM code_member_link WHERE target_id = @SymbolId", new { SymbolId = symbolId }, cancellationToken: cancellationToken))];
        }

        HashSet<int> wanted = [.. memberIds, .. baseIds.Select(static item => item.Id), .. derivedIds.Select(static item => item.Id), .. overrideIds, .. overriddenByIds,
            .. usesGrouped.Select(static edge => edge.Other), .. usedByGrouped.Select(static edge => edge.Other)];
        Dictionary<int, SymbolSummary> summaries = await LoadSummariesAsync(connection, wanted, cancellationToken);

        return new SymbolDetail(
            symbol,
            modifiers,
            parent,
            declarations,
            [.. memberIds.Where(summaries.ContainsKey).Select(id => summaries[id]).OrderBy(static member => member.Path, StringComparer.Ordinal).ThenBy(static member => member.Line).ThenBy(static member => member.Name, StringComparer.Ordinal)],
            [.. baseIds.Where(item => summaries.ContainsKey(item.Id)).OrderBy(static item => item.Kind == CodeUseKinds.Inherit ? 0 : 1).Select(item => summaries[item.Id]).DistinctBy(static item => item.SymbolId)],
            [.. derivedIds.Where(item => summaries.ContainsKey(item.Id)).Select(item => summaries[item.Id]).DistinctBy(static item => item.SymbolId).OrderBy(static item => item.FullName, StringComparer.Ordinal)],
            [.. overrideIds.Where(summaries.ContainsKey).Select(id => summaries[id]).OrderBy(static item => item.FullName, StringComparer.Ordinal)],
            [.. overriddenByIds.Where(summaries.ContainsKey).Select(id => summaries[id]).OrderBy(static item => item.FullName, StringComparer.Ordinal)],
            ToEdges(usesGrouped, summaries),
            ToEdges(usedByGrouped, summaries),
            external,
            referenceCount);
    }

    private static async Task<CodeReferencePage> GetReferencesAsync(
        NpgsqlConnection connection, IReadOnlyCollection<int> symbolIds, string? kind, string? path, int offset, int limit, CancellationToken cancellationToken)
    {
        limit = Math.Clamp(limit, 1, MaxReferenceLimit);
        offset = Math.Max(0, offset);
        StringBuilder filter = new("r.symbol_id = ANY (@Ids)");
        DynamicParameters parameters = new();
        parameters.Add("Ids", symbolIds.ToArray());
        parameters.Add("Offset", offset);
        parameters.Add("Limit", limit);
        if (!string.IsNullOrWhiteSpace(kind))
        {
            filter.Append(" AND r.kind = @Kind");
            parameters.Add("Kind", kind.Trim().ToLowerInvariant());
        }

        if (!string.IsNullOrWhiteSpace(path))
        {
            string clean = path.Trim().Replace('\\', '/').TrimEnd('/');
            filter.Append(" AND (doc.path = @Path OR doc.path LIKE @Folder)");
            parameters.Add("Path", clean);
            parameters.Add("Folder", CodeSearchStore.EscapeLike(clean) + "/%");
        }

        long total = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            $"SELECT count(*) FROM code_reference r JOIN document doc ON doc.document_id = r.document_id WHERE {filter}", parameters, cancellationToken: cancellationToken));
        List<ReferenceRow> rows = [.. await connection.QueryAsync<ReferenceRow>(new CommandDefinition(
            $"""
            SELECT r.document_id, doc.path, r.line, r."column", r.length, r.kind, r.from_id
            FROM code_reference r
            JOIN document doc ON doc.document_id = r.document_id
            WHERE {filter}
            ORDER BY doc.path COLLATE "C", r.line, r."column"
            OFFSET @Offset LIMIT @Limit
            """,
            parameters, cancellationToken: cancellationToken))];
        if (rows.Count == 0)
            return new CodeReferencePage(total, []);

        Dictionary<int, SymbolSummary> callers = await LoadSummariesAsync(connection, rows.Where(static row => row.FromId is not null).Select(static row => row.FromId!.Value).Distinct().ToArray(), cancellationToken);
        Dictionary<long, IReadOnlyList<string>> lines = [];
        foreach ((long documentId, string content) in await connection.QueryAsync<(long, string)>(new CommandDefinition(
            "SELECT document_id, content FROM document WHERE document_id = ANY (@Ids)", new { Ids = rows.Select(static row => row.DocumentId).Distinct().ToArray() }, cancellationToken: cancellationToken)))
        {
            lines[documentId] = CodeText.SplitLines(content);
        }

        return new CodeReferencePage(total, [.. rows.Select(row =>
        {
            string line = lines.TryGetValue(row.DocumentId, out IReadOnlyList<string>? all) && row.Line >= 1 && row.Line <= all.Count ? all[row.Line - 1] : string.Empty;
            (string text, int start) = CodeLineMatcher.Window(line, row.Column - 1, row.Length);
            return new CodeReference(row.DocumentId, row.Path, row.Line, row.Column, row.Length, row.Kind, text, start,
                row.FromId is { } from && callers.TryGetValue(from, out SymbolSummary? caller) ? caller : null);
        })]);
    }

    private static async Task<Dictionary<int, SymbolSummary>> LoadSummariesAsync(NpgsqlConnection connection, IReadOnlyCollection<int> ids, CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
            return [];

        IEnumerable<SymbolRow> rows = await connection.QueryAsync<SymbolRow>(new CommandDefinition(
            $"SELECT {SymbolColumns} {SymbolFrom} WHERE s.symbol_id = ANY (@Ids)", new { Ids = ids.ToArray() }, cancellationToken: cancellationToken));
        return rows.ToDictionary(static row => row.SymbolId, static row => row.ToSummary());
    }

    private static List<EdgeSummary> ToEdges(IEnumerable<(int Other, List<UseCount> Kinds, int Total)> grouped, Dictionary<int, SymbolSummary> summaries) =>
        [.. grouped.Where(edge => summaries.ContainsKey(edge.Other)).Select(edge => new EdgeSummary(summaries[edge.Other], edge.Kinds, edge.Total))];

    // Sums the rows by the other symbol and by kind. The result is sorted by the total, the largest first.
    private static IEnumerable<(int Other, List<UseCount> Kinds, int Total)> GroupUses(IEnumerable<UseRow> rows, IReadOnlyCollection<int> exclude) =>
        rows.Where(row => !exclude.Contains(row.Other))
            .GroupBy(static row => row.Other)
            .Select(static group => (
                Other: group.Key,
                Kinds: group.GroupBy(static row => row.Kind)
                    .Select(static kind => new UseCount(kind.Key, (int)Math.Min(kind.Sum(static row => row.Count), int.MaxValue)))
                    .OrderBy(static kind => KindOrder(kind.Kind)).ToList()))
            .Select(static item => (item.Other, item.Kinds, Total: (int)Math.Min(item.Kinds.Sum(static kind => (long)kind.Count), int.MaxValue)))
            .OrderByDescending(static item => item.Total)
            .ThenBy(static item => item.Other);

    private static int KindOrder(string kind)
    {
        for (int i = 0; i < CodeUseKinds.All.Count; i++)
        {
            if (CodeUseKinds.All[i] == kind)
                return i;
        }

        return CodeUseKinds.All.Count;
    }

    private static async Task<IReadOnlyList<UseRow>> TypeUsesAsync(NpgsqlConnection connection, int[] ids, CancellationToken cancellationToken) =>
        [.. await connection.QueryAsync<UseRow>(new CommandDefinition(
            "SELECT to_id AS other, kind, sum(count) AS count FROM code_type_edge WHERE from_id = ANY (@Ids) GROUP BY to_id, kind", new { Ids = ids }, cancellationToken: cancellationToken))];

    private static async Task<IReadOnlyList<UseRow>> TypeUsedByAsync(NpgsqlConnection connection, int[] ids, CancellationToken cancellationToken) =>
        [.. await connection.QueryAsync<UseRow>(new CommandDefinition(
            "SELECT from_id AS other, kind, sum(count) AS count FROM code_type_edge WHERE to_id = ANY (@Ids) GROUP BY from_id, kind", new { Ids = ids }, cancellationToken: cancellationToken))];

    private static async Task<IReadOnlyList<UseRow>> MemberUsesAsync(NpgsqlConnection connection, int[] ids, CancellationToken cancellationToken) =>
        [.. await connection.QueryAsync<UseRow>(new CommandDefinition(
            "SELECT symbol_id AS other, kind, count(*) AS count FROM code_reference WHERE from_id = ANY (@Ids) GROUP BY symbol_id, kind", new { Ids = ids }, cancellationToken: cancellationToken))];

    private static async Task<IReadOnlyList<UseRow>> MemberUsedByAsync(NpgsqlConnection connection, int[] ids, CancellationToken cancellationToken) =>
        [.. await connection.QueryAsync<UseRow>(new CommandDefinition(
            "SELECT from_id AS other, kind, count(*) AS count FROM code_reference WHERE symbol_id = ANY (@Ids) AND from_id IS NOT NULL GROUP BY from_id, kind", new { Ids = ids }, cancellationToken: cancellationToken))];

    private static async Task<IReadOnlyList<ExternalUse>> LoadExternalAsync(NpgsqlConnection connection, int[] ids, CancellationToken cancellationToken)
    {
        IEnumerable<ExternalRow> rows = await connection.QueryAsync<ExternalRow>(new CommandDefinition(
            "SELECT target, assembly, kind, sum(count) AS count FROM code_external_edge WHERE from_id = ANY (@Ids) GROUP BY target, assembly, kind", new { Ids = ids }, cancellationToken: cancellationToken));
        return [.. rows
            .GroupBy(static row => (row.Target, row.Assembly))
            .Select(static group =>
            {
                List<UseCount> kinds = [.. group.Select(static row => new UseCount(row.Kind, (int)Math.Min(row.Count, int.MaxValue))).OrderBy(static kind => KindOrder(kind.Kind))];
                return new ExternalUse(group.Key.Target, group.Key.Assembly, kinds, kinds.Sum(static kind => kind.Count));
            })
            .OrderByDescending(static use => use.Total)
            .ThenBy(static use => use.Target, StringComparer.Ordinal)
            .Take(MaxExternal)];
    }

    [GeneratedRegex(@"^[A-Z]:\S")]
    private static partial Regex DocIdRegex();

    [GeneratedRegex("<[^<>]*>")]
    private static partial Regex GenericRegex();

    private sealed class OutlineBuilder(SymbolSummary symbol)
    {
        public List<OutlineBuilder> Children { get; } = [];

        public OutlineNode Build() => new(symbol, [.. Children.Select(static child => child.Build())]);
    }

    private class SymbolRow
    {
        public int SymbolId { get; init; }

        public int SourceId { get; init; }

        public string Source { get; init; } = string.Empty;

        public int ProjectId { get; init; }

        public string Project { get; init; } = string.Empty;

        public string Kind { get; init; } = string.Empty;

        public string Name { get; init; } = string.Empty;

        public string FullName { get; init; } = string.Empty;

        public string DocId { get; init; } = string.Empty;

        public string Signature { get; init; } = string.Empty;

        public string Accessibility { get; init; } = string.Empty;

        public string? Summary { get; init; }

        public long? DocumentId { get; init; }

        public string? Path { get; init; }

        public int? Line { get; init; }

        public SymbolSummary ToSummary() => new(SymbolId, SourceId, Source, ProjectId, Project, Kind, Name, FullName, DocId, Signature, Accessibility, Summary, DocumentId, Path, Line);
    }

    private sealed class OutlineRow : SymbolRow
    {
        public int? ParentId { get; init; }
    }

    private sealed class StateRow
    {
        public DateTime? BuiltAt { get; init; }

        public int? DurationMs { get; init; }

        public DateTime? UpdatedAt { get; init; }

        public int UpdateCount { get; init; }

        public int? UpdateDurationMs { get; init; }

        public string? SdkVersion { get; init; }

        public string? Error { get; init; }

        public int ProjectCount { get; init; }

        public int SymbolCount { get; init; }

        public int ReferenceCount { get; init; }
    }

    private sealed class ProjectRow
    {
        public int ProjectId { get; init; }

        public string Path { get; init; } = string.Empty;

        public string Name { get; init; } = string.Empty;

        public string? TargetFramework { get; init; }

        public bool IsTest { get; init; }

        public int DocumentCount { get; init; }

        public int ErrorCount { get; init; }

        public long SymbolCount { get; init; }
    }

    private sealed class DiagnosticRow
    {
        public string? Project { get; init; }

        public string Severity { get; init; } = string.Empty;

        public string Code { get; init; } = string.Empty;

        public string Message { get; init; } = string.Empty;

        public string? Path { get; init; }

        public int? Line { get; init; }
    }

    private sealed class DeclarationRow
    {
        public long DocumentId { get; init; }

        public string Path { get; init; } = string.Empty;

        public int StartLine { get; init; }

        public int EndLine { get; init; }

        public int NameLine { get; init; }

        public int NameColumn { get; init; }
    }

    private sealed class ReferenceRow
    {
        public long DocumentId { get; init; }

        public string Path { get; init; } = string.Empty;

        public int Line { get; init; }

        public int Column { get; init; }

        public int Length { get; init; }

        public string Kind { get; init; } = string.Empty;

        public int? FromId { get; init; }
    }

    private sealed class UseRow
    {
        public int Other { get; init; }

        public string Kind { get; init; } = string.Empty;

        public long Count { get; init; }
    }

    private sealed class ExternalRow
    {
        public string Target { get; init; } = string.Empty;

        public string Assembly { get; init; } = string.Empty;

        public string Kind { get; init; } = string.Empty;

        public long Count { get; init; }
    }
}

/// <summary>
/// The symbols that share a project and a full name: the overloads of a method, or the generic forms of a type.
/// </summary>
/// <param name="Symbols">The symbols in the order of their IDs. The list is not empty.</param>
public sealed record CodeSymbolGroup(IReadOnlyList<SymbolSummary> Symbols)
{
    /// <summary>Gets the first symbol of the group.</summary>
    public SymbolSummary Primary => Symbols[0];

    /// <summary>Gets a value that tells that the group holds types.</summary>
    public bool IsType => CodeSymbolKinds.IsType(Primary.Kind);

    /// <summary>Gets the keys of the symbols.</summary>
    public int[] Ids => [.. Symbols.Select(static symbol => symbol.SymbolId)];
}

/// <summary>
/// The result of the lookup of a symbol by text.
/// </summary>
/// <param name="Source">The code source.</param>
/// <param name="Groups">The groups of symbols that match.</param>
public sealed record CodeResolution(Source Source, IReadOnlyList<CodeSymbolGroup> Groups);
