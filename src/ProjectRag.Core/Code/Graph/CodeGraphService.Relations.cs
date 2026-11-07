using Dapper;

using Npgsql;

namespace ProjectRag.Core.Code.Graph;

public sealed partial class CodeGraphService
{
    /// <summary>The most nodes that a walk over the relations of a symbol returns in one direction.</summary>
    public const int MaxRelationNodes = 60;

    /// <summary>The largest depth of a walk over the edges between types.</summary>
    public const int MaxRelationDepth = 3;

    private const int MaxMapTypes = 300;
    private const int MaxMapDependencies = 50;
    private const int TopNeighbors = 10;

    /// <summary>
    /// Reads the relations of a symbol for the tools of the agents.
    /// </summary>
    /// <remarks>
    /// For a type the walk follows the edges between types, level by level, and stops at <see cref="MaxRelationNodes"/> nodes in a direction.
    /// For a member the relations are the callers and the callees, and the depth stays 1.
    /// </remarks>
    /// <param name="group">The symbols, from <see cref="ResolveAsync(string?, string, CancellationToken)"/>.</param>
    /// <param name="options">The direction, the kinds, the depth and the filters.</param>
    /// <param name="cancellationToken">The token that cancels the queries.</param>
    /// <returns>The relations.</returns>
    public async Task<CodeRelations> GetRelationsAsync(CodeSymbolGroup group, CodeRelationOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(options);
        int depth = group.IsType ? Math.Clamp(options.Depth, 1, MaxRelationDepth) : 1;
        int[] ids = group.Ids;
        await using NpgsqlConnection connection = await database.OpenAsync(cancellationToken);
        HashSet<int> testProjects = options.IncludeTests
            ? []
            : [.. await connection.QueryAsync<int>(new CommandDefinition(
                "SELECT project_id FROM code_project WHERE source_id = @SourceId AND is_test", new { group.Primary.SourceId }, cancellationToken: cancellationToken))];

        IReadOnlyList<SymbolSummary> bases = [];
        IReadOnlyList<SymbolSummary> derived = [];
        IReadOnlyList<SymbolSummary> overrides = [];
        IReadOnlyList<SymbolSummary> overriddenBy = [];
        IReadOnlyList<ExternalUse> external = [];
        if (group.IsType)
        {
            bases = await LoadSymbolsAsync(connection,
                "SELECT DISTINCT to_id FROM code_type_edge WHERE from_id = ANY (@Ids) AND kind IN ('inherit', 'implement') AND NOT (to_id = ANY (@Ids))", ids, testProjects, cancellationToken);
            derived = await LoadSymbolsAsync(connection,
                "SELECT DISTINCT from_id FROM code_type_edge WHERE to_id = ANY (@Ids) AND kind IN ('inherit', 'implement') AND NOT (from_id = ANY (@Ids))", ids, testProjects, cancellationToken);
            if (options.IncludeExternal)
                external = await LoadExternalAsync(connection, ids, cancellationToken);
        }
        else
        {
            overrides = await LoadSymbolsAsync(connection, "SELECT DISTINCT target_id FROM code_member_link WHERE member_id = ANY (@Ids)", ids, testProjects, cancellationToken);
            overriddenBy = await LoadSymbolsAsync(connection, "SELECT DISTINCT member_id FROM code_member_link WHERE target_id = ANY (@Ids)", ids, testProjects, cancellationToken);
        }

        (IReadOnlyList<RelationNode> uses, bool usesTruncated) = options.Direction == CodeRelationDirection.UsedBy
            ? ([], false)
            : await WalkAsync(connection, group, outgoing: true, depth, options, testProjects, cancellationToken);
        (IReadOnlyList<RelationNode> usedBy, bool usedByTruncated) = options.Direction == CodeRelationDirection.Uses
            ? ([], false)
            : await WalkAsync(connection, group, outgoing: false, depth, options, testProjects, cancellationToken);
        return new CodeRelations(group, depth, bases, derived, overrides, overriddenBy, uses, usedBy, external, usesTruncated, usedByTruncated);
    }

    /// <summary>
    /// Reads the projects of a code source with the number of types of each, for the map without arguments.
    /// </summary>
    /// <param name="sourceId">The key of the code source.</param>
    /// <param name="cancellationToken">The token that cancels the queries.</param>
    /// <returns>The overview, or <see langword="null"/> when the source does not exist.</returns>
    public async Task<CodeMapOverview?> GetMapOverviewAsync(int sourceId, CancellationToken cancellationToken)
    {
        if (await GetStatusAsync(sourceId, cancellationToken) is not { } status)
            return null;

        await using NpgsqlConnection connection = await database.OpenAsync(cancellationToken);
        Dictionary<int, int> counts = (await connection.QueryAsync<(int ProjectId, long Count)>(new CommandDefinition(
            "SELECT project_id, count(*) FROM code_symbol WHERE source_id = @SourceId AND kind = ANY (@Kinds) GROUP BY project_id",
            new { SourceId = sourceId, Kinds = TypeKinds }, cancellationToken: cancellationToken))).ToDictionary(static row => row.ProjectId, static row => (int)row.Count);
        return new CodeMapOverview(status, counts);
    }

    /// <summary>
    /// Reads the namespaces of a project and the dependencies between them.
    /// </summary>
    /// <param name="sourceId">The key of the code source.</param>
    /// <param name="project">The name of the assembly or the project file.</param>
    /// <param name="cancellationToken">The token that cancels the queries.</param>
    /// <returns>The map, or <see langword="null"/> when the source has no such project.</returns>
    public async Task<CodeProjectMap?> GetProjectMapAsync(int sourceId, string project, CancellationToken cancellationToken)
    {
        if (await GetStatusAsync(sourceId, cancellationToken) is not { } status)
            return null;

        string wanted = project.Trim();
        CodeProject? found = status.Projects.FirstOrDefault(item => item.Name == wanted) ?? status.Projects.FirstOrDefault(item => item.Path == wanted)
            ?? status.Projects.FirstOrDefault(item => item.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase));
        if (found is null)
            return null;

        await using NpgsqlConnection connection = await database.OpenAsync(cancellationToken);
        IReadOnlyList<NamespaceCount> namespaces = [.. (await connection.QueryAsync<(string Namespace, long Count)>(new CommandDefinition(
            """
            SELECT namespace, count(*)
            FROM code_symbol
            WHERE project_id = @ProjectId AND kind = ANY (@Kinds)
            GROUP BY namespace
            ORDER BY namespace COLLATE "C"
            """,
            new { found.ProjectId, Kinds = TypeKinds }, cancellationToken: cancellationToken))).Select(static row => new NamespaceCount(row.Namespace, (int)row.Count))];
        IReadOnlyList<NamespaceDependency> dependencies = [.. (await connection.QueryAsync<(string From, string To, long Weight)>(new CommandDefinition(
            """
            SELECT a.namespace, b.namespace, sum(e.count)
            FROM code_type_edge e
            JOIN code_symbol a ON a.symbol_id = e.from_id
            JOIN code_symbol b ON b.symbol_id = e.to_id
            WHERE a.project_id = @ProjectId AND b.project_id = @ProjectId AND a.namespace <> b.namespace
            GROUP BY a.namespace, b.namespace
            ORDER BY sum(e.count) DESC, a.namespace COLLATE "C", b.namespace COLLATE "C"
            LIMIT @Limit
            """,
            new { found.ProjectId, Limit = MaxMapDependencies }, cancellationToken: cancellationToken)))
            .Select(static row => new NamespaceDependency(row.From, row.To, (int)Math.Min(row.Weight, int.MaxValue)))];
        return new CodeProjectMap(found, namespaces, dependencies);
    }

    /// <summary>
    /// Reads the types of a namespace with the types that they use most and the types that use them most.
    /// </summary>
    /// <param name="sourceId">The key of the code source.</param>
    /// <param name="namespaceName">The namespace.</param>
    /// <param name="project">The name of an assembly to keep, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">The token that cancels the queries.</param>
    /// <returns>The map. It has no types when the namespace is unknown, and then <see cref="CodeNamespaceMap.Similar"/> names namespaces that hold the text.</returns>
    public async Task<CodeNamespaceMap> GetNamespaceMapAsync(int sourceId, string namespaceName, string? project, CancellationToken cancellationToken)
    {
        namespaceName = namespaceName.Trim();
        string? projectName = string.IsNullOrWhiteSpace(project) ? null : project.Trim();
        await using NpgsqlConnection connection = await database.OpenAsync(cancellationToken);
        int[] ids = [.. await connection.QueryAsync<int>(new CommandDefinition(
            """
            SELECT s.symbol_id
            FROM code_symbol s
            JOIN code_project p ON p.project_id = s.project_id
            WHERE s.source_id = @SourceId AND s.namespace = @Namespace AND s.kind = ANY (@Kinds) AND (@Project::text IS NULL OR p.name = @Project)
            ORDER BY s.full_name COLLATE "C", s.symbol_id
            LIMIT @Limit
            """,
            new { SourceId = sourceId, Namespace = namespaceName, Kinds = TypeKinds, Project = projectName, Limit = MaxMapTypes }, cancellationToken: cancellationToken))];
        if (ids.Length == 0)
        {
            string[] similar = [.. await connection.QueryAsync<string>(new CommandDefinition(
                """
                SELECT namespace FROM code_symbol
                WHERE source_id = @SourceId AND namespace ILIKE @Pattern
                GROUP BY namespace
                ORDER BY namespace COLLATE "C"
                LIMIT 10
                """,
                new { SourceId = sourceId, Pattern = "%" + CodeSearchStore.EscapeLike(namespaceName) + "%" }, cancellationToken: cancellationToken))];
            return new CodeNamespaceMap(namespaceName, [], similar);
        }

        Dictionary<int, List<UseRow>> usesByType = await TypeEdgesByOwnerAsync(connection, ids, outgoing: true, cancellationToken);
        Dictionary<int, List<UseRow>> usedByByType = await TypeEdgesByOwnerAsync(connection, ids, outgoing: false, cancellationToken);

        HashSet<int> wanted = [.. ids];
        Dictionary<int, List<(int Other, List<UseCount> Kinds, int Total)>> top = [];
        foreach (int id in ids)
        {
            List<(int, List<UseCount>, int)> uses = [.. GroupUses(usesByType.GetValueOrDefault(id) ?? [], [id]).Take(TopNeighbors)];
            List<(int, List<UseCount>, int)> usedBy = [.. GroupUses(usedByByType.GetValueOrDefault(id) ?? [], [id]).Take(TopNeighbors)];
            foreach ((int other, _, _) in uses)
                wanted.Add(other);

            foreach ((int other, _, _) in usedBy)
                wanted.Add(other);

            top[id] = [.. uses];
            top[-id] = [.. usedBy];
        }

        Dictionary<int, SymbolSummary> summaries = await LoadSummariesAsync(connection, wanted, cancellationToken);
        List<NamespaceType> types = [];
        foreach (int id in ids)
        {
            if (!summaries.TryGetValue(id, out SymbolSummary? symbol))
                continue;

            types.Add(new NamespaceType(symbol, ToEdges(top[id], summaries), ToEdges(top[-id], summaries)));
        }

        return new CodeNamespaceMap(namespaceName, types, []);
    }

    private static async Task<Dictionary<int, List<UseRow>>> TypeEdgesByOwnerAsync(NpgsqlConnection connection, int[] ids, bool outgoing, CancellationToken cancellationToken)
    {
        string owner = outgoing ? "from_id" : "to_id";
        string other = outgoing ? "to_id" : "from_id";
        IEnumerable<OwnedUseRow> rows = await connection.QueryAsync<OwnedUseRow>(new CommandDefinition(
            $"SELECT {owner} AS owner, {other} AS other, kind, sum(count) AS count FROM code_type_edge WHERE {owner} = ANY (@Ids) GROUP BY {owner}, {other}, kind",
            new { Ids = ids }, cancellationToken: cancellationToken));
        return rows.GroupBy(static row => row.Owner).ToDictionary(static group => group.Key, static group => group.Select(static row => new UseRow { Other = row.Other, Kind = row.Kind, Count = row.Count }).ToList());
    }

    private static async Task<IReadOnlyList<SymbolSummary>> LoadSymbolsAsync(
        NpgsqlConnection connection, string idSql, int[] ids, HashSet<int> testProjects, CancellationToken cancellationToken)
    {
        int[] found = [.. await connection.QueryAsync<int>(new CommandDefinition(idSql, new { Ids = ids }, cancellationToken: cancellationToken))];
        Dictionary<int, SymbolSummary> summaries = await LoadSummariesAsync(connection, found, cancellationToken);
        return [.. summaries.Values.Where(symbol => !testProjects.Contains(symbol.ProjectId)).OrderBy(static symbol => symbol.FullName, StringComparer.Ordinal).ThenBy(static symbol => symbol.DocId, StringComparer.Ordinal)];
    }

    private static async Task<(IReadOnlyList<RelationNode> Nodes, bool Truncated)> WalkAsync(
        NpgsqlConnection connection, CodeSymbolGroup group, bool outgoing, int depth, CodeRelationOptions options, HashSet<int> testProjects, CancellationToken cancellationToken)
    {
        Dictionary<int, SymbolSummary> known = group.Symbols.ToDictionary(static symbol => symbol.SymbolId);
        HashSet<int> visited = [.. group.Ids];
        int[] frontier = group.Ids;
        List<RelationNode> nodes = [];
        bool truncated = false;
        for (int level = 1; level <= depth && frontier.Length > 0; level++)
        {
            List<(int Via, UseRow Row)> rows = [];
            if (group.IsType)
            {
                string owner = outgoing ? "from_id" : "to_id";
                string other = outgoing ? "to_id" : "from_id";
                foreach (OwnedUseRow row in await connection.QueryAsync<OwnedUseRow>(new CommandDefinition(
                    $"SELECT {owner} AS owner, {other} AS other, kind, sum(count) AS count FROM code_type_edge WHERE {owner} = ANY (@Ids) GROUP BY {owner}, {other}, kind",
                    new { Ids = frontier }, cancellationToken: cancellationToken)))
                {
                    rows.Add((row.Owner, new UseRow { Other = row.Other, Kind = row.Kind, Count = row.Count }));
                }
            }
            else
            {
                IReadOnlyList<UseRow> memberRows = outgoing ? await MemberUsesAsync(connection, frontier, cancellationToken) : await MemberUsedByAsync(connection, frontier, cancellationToken);
                rows.AddRange(memberRows.Select(static row => (0, row)));
            }

            IEnumerable<(int Via, UseRow Row)> kept = rows.Where(item => !visited.Contains(item.Row.Other) && (options.Kinds is null || options.Kinds.Contains(item.Row.Kind)));
            List<(int Other, List<UseCount> Kinds, int Total)> grouped = [.. GroupUses(kept.Select(static item => item.Row), [])];
            Dictionary<int, int> via = kept.GroupBy(static item => item.Row.Other)
                .ToDictionary(static group => group.Key, static group => group.OrderByDescending(static item => item.Row.Count).First().Via);

            Dictionary<int, SymbolSummary> summaries = await LoadSummariesAsync(connection, grouped.Select(static item => item.Other).ToArray(), cancellationToken);
            foreach ((int id, SymbolSummary summary) in summaries)
                known[id] = summary;

            List<(int Other, List<UseCount> Kinds, int Total)> candidates = [.. grouped
                .Where(item => summaries.TryGetValue(item.Other, out SymbolSummary? symbol) && !testProjects.Contains(symbol.ProjectId))
                .OrderByDescending(static item => item.Total)
                .ThenBy(item => summaries[item.Other].FullName, StringComparer.Ordinal)];
            int room = MaxRelationNodes - nodes.Count;
            if (candidates.Count > room)
            {
                truncated = true;
                candidates = candidates[..Math.Max(0, room)];
            }

            foreach ((int other, List<UseCount> kinds, int total) in candidates)
            {
                visited.Add(other);
                SymbolSummary? from = level > 1 && via.TryGetValue(other, out int viaId) ? known.GetValueOrDefault(viaId) : null;
                nodes.Add(new RelationNode(summaries[other], level, kinds, total, from));
            }

            // A level also marks the hidden nodes as visited, so a test type or a cut node does not come back one level lower.
            foreach (int hidden in grouped.Select(static item => item.Other))
                visited.Add(hidden);

            frontier = [.. candidates.Select(static item => item.Other)];
            if (truncated)
                break;
        }

        return (nodes, truncated);
    }

    private sealed class OwnedUseRow
    {
        public int Owner { get; init; }

        public int Other { get; init; }

        public string Kind { get; init; } = string.Empty;

        public long Count { get; init; }
    }
}

/// <summary>
/// The direction of a walk over the relations.
/// </summary>
public enum CodeRelationDirection
{
    /// <summary>What the symbol uses and what uses it.</summary>
    Both,

    /// <summary>What the symbol uses.</summary>
    Uses,

    /// <summary>What uses the symbol.</summary>
    UsedBy,
}

/// <summary>
/// The options of <see cref="CodeGraphService.GetRelationsAsync"/>.
/// </summary>
/// <param name="Direction">The direction.</param>
/// <param name="Kinds">The kinds of uses to keep, or <see langword="null"/> for all.</param>
/// <param name="Depth">The depth of the walk over types, from 1 to <see cref="CodeGraphService.MaxRelationDepth"/>.</param>
/// <param name="IncludeExternal">Tells that the answer lists the external types that a type uses.</param>
/// <param name="IncludeTests">Tells that the answer keeps the symbols of test projects.</param>
public sealed record CodeRelationOptions(CodeRelationDirection Direction = CodeRelationDirection.Both, IReadOnlySet<string>? Kinds = null, int Depth = 1, bool IncludeExternal = false, bool IncludeTests = true);

/// <summary>
/// A symbol that a walk reached.
/// </summary>
/// <param name="Symbol">The symbol.</param>
/// <param name="Depth">The level of the walk, from 1.</param>
/// <param name="Kinds">The uses by kind between the node and the symbol of the level before.</param>
/// <param name="Total">The sum of the counts.</param>
/// <param name="Via">The symbol of the level before for a node deeper than 1, else <see langword="null"/>.</param>
public sealed record RelationNode(SymbolSummary Symbol, int Depth, IReadOnlyList<UseCount> Kinds, int Total, SymbolSummary? Via);

/// <summary>
/// The relations of a symbol.
/// </summary>
/// <param name="Group">The symbols.</param>
/// <param name="Depth">The depth of the walk.</param>
/// <param name="Bases">For a type: the base class and the interfaces in the code base.</param>
/// <param name="Derived">For a type: the direct heirs.</param>
/// <param name="Overrides">For a member: what it overrides or implements.</param>
/// <param name="OverriddenBy">For a member: what overrides or implements it.</param>
/// <param name="Uses">What the symbol uses.</param>
/// <param name="UsedBy">What uses the symbol.</param>
/// <param name="External">The external types that a type uses.</param>
/// <param name="UsesTruncated">Tells that <paramref name="Uses"/> stops at the limit.</param>
/// <param name="UsedByTruncated">Tells that <paramref name="UsedBy"/> stops at the limit.</param>
public sealed record CodeRelations(
    CodeSymbolGroup Group,
    int Depth,
    IReadOnlyList<SymbolSummary> Bases,
    IReadOnlyList<SymbolSummary> Derived,
    IReadOnlyList<SymbolSummary> Overrides,
    IReadOnlyList<SymbolSummary> OverriddenBy,
    IReadOnlyList<RelationNode> Uses,
    IReadOnlyList<RelationNode> UsedBy,
    IReadOnlyList<ExternalUse> External,
    bool UsesTruncated,
    bool UsedByTruncated);

/// <summary>
/// The projects of a code source with the number of types of each.
/// </summary>
/// <param name="Status">The status of the graph.</param>
/// <param name="TypeCounts">The number of types by project key.</param>
public sealed record CodeMapOverview(CodeGraphStatus Status, IReadOnlyDictionary<int, int> TypeCounts);

/// <summary>
/// A namespace with the number of its types.
/// </summary>
/// <param name="Namespace">The namespace.</param>
/// <param name="Types">The number of types.</param>
public sealed record NamespaceCount(string Namespace, int Types);

/// <summary>
/// The dependency of a namespace on another, summed over the edges between their types.
/// </summary>
/// <param name="From">The namespace that uses.</param>
/// <param name="To">The namespace that is used.</param>
/// <param name="Weight">The sum of the counts of the edges.</param>
public sealed record NamespaceDependency(string From, string To, int Weight);

/// <summary>
/// The namespaces of a project.
/// </summary>
/// <param name="Project">The project.</param>
/// <param name="Namespaces">The namespaces with their numbers of types.</param>
/// <param name="Dependencies">The strongest dependencies between the namespaces of the project, at most 50.</param>
public sealed record CodeProjectMap(CodeProject Project, IReadOnlyList<NamespaceCount> Namespaces, IReadOnlyList<NamespaceDependency> Dependencies);

/// <summary>
/// A type of a namespace with its neighbors.
/// </summary>
/// <param name="Symbol">The type.</param>
/// <param name="TopUses">The types that it uses most, at most 10.</param>
/// <param name="TopUsedBy">The types that use it most, at most 10.</param>
public sealed record NamespaceType(SymbolSummary Symbol, IReadOnlyList<EdgeSummary> TopUses, IReadOnlyList<EdgeSummary> TopUsedBy);

/// <summary>
/// The types of a namespace.
/// </summary>
/// <param name="Namespace">The namespace.</param>
/// <param name="Types">The types.</param>
/// <param name="Similar">Namespaces that hold the text, when the namespace has no types.</param>
public sealed record CodeNamespaceMap(string Namespace, IReadOnlyList<NamespaceType> Types, IReadOnlyList<string> Similar);
