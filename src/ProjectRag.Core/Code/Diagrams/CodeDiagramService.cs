using System.Text;

using Dapper;

using Npgsql;

using ProjectRag.Core.Code.Graph;
using ProjectRag.Core.Sources;
using ProjectRag.Core.Storage;

namespace ProjectRag.Core.Code.Diagrams;

/// <summary>
/// Builds the diagrams of the code graph: the projects, the namespaces, the types of one namespace and the neighborhood of one type.
/// </summary>
/// <remarks>
/// The service reads the tables that <see cref="CodeGraphStore"/> writes. The database sums the edges between namespaces.
/// The walk around a type runs one query for each level, not one for each node. <see cref="DotWriter"/> and <see cref="PlantUmlWriter"/> write the text.
/// </remarks>
/// <param name="database">The database.</param>
/// <param name="sources">The store of the sources.</param>
public sealed partial class CodeDiagramService(Database database, SourceStore sources)
{
    /// <summary>The smallest value of <c>maxNodes</c>.</summary>
    public const int MinNodeLimit = 5;

    /// <summary>The largest value of <c>maxNodes</c>.</summary>
    public const int MaxNodeLimit = 150;

    /// <summary>The largest depth of the walk around a type.</summary>
    public const int MaxDepth = 3;

    /// <summary>The smallest value of <c>maxEdges</c>.</summary>
    public const int MinEdgeLimit = 5;

    /// <summary>The largest value of <c>maxEdges</c>.</summary>
    public const int MaxEdgeLimit = 500;

    private const int DefaultEdges = 60;
    private const int DefaultNamespaceNodes = 60;
    private const int DefaultTypeNodes = 40;
    private const int MemberLimit = 12;
    private const int MaxExternal = 10;
    private const int MaxChain = 20;

    private static readonly string[] TypeKinds = [.. CodeSymbolKinds.Types];

    private static readonly string[] DataKinds =
        [CodeSymbolKinds.Field, CodeSymbolKinds.Constant, CodeSymbolKinds.Property, CodeSymbolKinds.Event, CodeSymbolKinds.EnumMember];

    private static readonly string[] OperationKinds =
        [CodeSymbolKinds.Constructor, CodeSymbolKinds.Destructor, CodeSymbolKinds.Method, CodeSymbolKinds.Operator, CodeSymbolKinds.Conversion, CodeSymbolKinds.Indexer];

    /// <summary>
    /// Builds the diagram of the projects and their references.
    /// </summary>
    /// <param name="request">The query.</param>
    /// <param name="cancellationToken">The token that cancels the queries.</param>
    /// <returns>The diagram.</returns>
    /// <exception cref="CodeSearchException">A field of the query is not valid.</exception>
    /// <exception cref="CodeDiagramNotFoundException">The source does not exist or has no graph.</exception>
    public async Task<CodeDiagram> GetProjectsAsync(ProjectsDiagramRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        bool includeTests = request.IncludeTests ?? true;
        await using NpgsqlConnection connection = await database.OpenAsync(cancellationToken);
        Source source = await RequireGraphAsync(connection, request.SourceId, cancellationToken);

        List<ProjectRow> projects = [.. await connection.QueryAsync<ProjectRow>(new CommandDefinition(
            """
            SELECT p.project_id, p.path, p.name, p.is_test, count(s.symbol_id) AS types
            FROM code_project p
            LEFT JOIN code_symbol s ON s.project_id = p.project_id AND s.kind = ANY (@Kinds)
            WHERE p.source_id = @SourceId AND (@IncludeTests OR NOT p.is_test)
            GROUP BY p.project_id, p.path, p.name, p.is_test
            ORDER BY p.name COLLATE "C", p.path COLLATE "C"
            """,
            new { source.SourceId, Kinds = TypeKinds, IncludeTests = includeTests }, cancellationToken: cancellationToken))];
        List<(int ProjectId, int ReferencedId)> references = [.. await connection.QueryAsync<(int, int)>(new CommandDefinition(
            "SELECT project_id, referenced_id FROM code_project_reference WHERE source_id = @SourceId ORDER BY project_id, referenced_id",
            new { source.SourceId }, cancellationToken: cancellationToken))];

        HashSet<string> duplicated = [.. projects.GroupBy(static project => project.Name, StringComparer.Ordinal).Where(static group => group.Count() > 1).Select(static group => group.Key)];
        Dictionary<int, string> ids = projects.ToDictionary(static project => project.ProjectId, project => duplicated.Contains(project.Name) ? project.Path : project.Name);

        List<DiagramCluster> clusters = [];
        List<DiagramNode> nodes = [];
        foreach (ProjectRow project in projects)
        {
            string? cluster = null;
            int slash = project.Path.IndexOf('/', StringComparison.Ordinal);
            if (slash > 0)
            {
                string folder = project.Path[..slash];
                cluster = "folder:" + folder;
                if (clusters.TrueForAll(item => item.Id != cluster))
                    clusters.Add(new DiagramCluster(cluster, folder));
            }

            nodes.Add(new DiagramNode(
                ids[project.ProjectId], DiagramNodeKinds.Project, project.Name, null, project.Name, null, project.IsTest, false, false, (int)project.Types,
                Href(source.SourceId, DiagramViews.Namespaces, ("project", project.Name)), cluster, [], 0));
        }

        List<DiagramEdge> edges = [.. references
            .Where(reference => ids.ContainsKey(reference.ProjectId) && ids.ContainsKey(reference.ReferencedId))
            .Select(reference => new DiagramEdge(ids[reference.ProjectId], ids[reference.ReferencedId], DiagramEdgeStyles.Reference, [], 1))
            .OrderBy(edge => IndexOf(nodes, edge.From)).ThenBy(edge => IndexOf(nodes, edge.To))];
        clusters.Sort(static (a, b) => string.CompareOrdinal(a.Label, b.Label));
        return Build(DiagramViews.Projects, "Projects of " + source.Name, nodes, edges, clusters, false, []);
    }

    /// <summary>
    /// Builds the diagram of the namespaces and the dependencies between them.
    /// </summary>
    /// <param name="request">The query.</param>
    /// <param name="cancellationToken">The token that cancels the queries.</param>
    /// <returns>The diagram.</returns>
    /// <exception cref="CodeSearchException">A field of the query is not valid.</exception>
    /// <exception cref="CodeDiagramNotFoundException">The source or the project does not exist, or the source has no graph.</exception>
    public async Task<CodeDiagram> GetNamespacesAsync(NamespacesDiagramRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        bool includeTests = request.IncludeTests ?? true;
        int minWeight = request.MinWeight ?? 1;
        if (minWeight < 1)
            throw new CodeSearchException("minWeight", "The minimum weight must be 1 or more.");

        int maxNodes = ParseLimit(request.MaxNodes, DefaultNamespaceNodes);
        int maxEdges = ParseEdgeLimit(request.MaxEdges);
        string? project = string.IsNullOrWhiteSpace(request.Project) ? null : request.Project.Trim();
        await using NpgsqlConnection connection = await database.OpenAsync(cancellationToken);
        Source source = await RequireGraphAsync(connection, request.SourceId, cancellationToken);
        if (project is not null && !await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS (SELECT 1 FROM code_project WHERE source_id = @SourceId AND name = @Project)", new { source.SourceId, Project = project }, cancellationToken: cancellationToken)))
        {
            throw new CodeDiagramNotFoundException($"The code source {source.Name} has no project {project}.");
        }

        List<NamespaceRow> rows = [.. await connection.QueryAsync<NamespaceRow>(new CommandDefinition(
            """
            SELECT p.name AS project, s.namespace, bool_or(p.is_test) AS is_test, count(*) AS types
            FROM code_symbol s
            JOIN code_project p ON p.project_id = s.project_id
            WHERE s.source_id = @SourceId AND s.kind = ANY (@Kinds) AND (@IncludeTests OR NOT p.is_test) AND (@Project::text IS NULL OR p.name = @Project)
            GROUP BY p.name, s.namespace
            """,
            new { source.SourceId, Kinds = TypeKinds, IncludeTests = includeTests, Project = project }, cancellationToken: cancellationToken))];
        List<NamespaceEdgeRow> edgeRows = [.. await connection.QueryAsync<NamespaceEdgeRow>(new CommandDefinition(
            """
            SELECT pa.name AS from_project, a.namespace AS from_namespace, pb.name AS to_project, b.namespace AS to_namespace, e.kind, sum(e.count) AS count
            FROM code_type_edge e
            JOIN code_symbol a ON a.symbol_id = e.from_id
            JOIN code_project pa ON pa.project_id = a.project_id
            JOIN code_symbol b ON b.symbol_id = e.to_id
            JOIN code_project pb ON pb.project_id = b.project_id
            WHERE e.source_id = @SourceId
              AND (pa.name <> pb.name OR a.namespace <> b.namespace)
              AND (@IncludeTests OR (NOT pa.is_test AND NOT pb.is_test))
              AND (@Project::text IS NULL OR (pa.name = @Project AND pb.name = @Project))
            GROUP BY pa.name, a.namespace, pb.name, b.namespace, e.kind
            """,
            new { source.SourceId, IncludeTests = includeTests, Project = project }, cancellationToken: cancellationToken))];

        rows.Sort(static (a, b) => CompareKeys((a.Project, a.Namespace), (b.Project, b.Namespace)));
        Dictionary<(string, string), NamespaceRow> byKey = rows.ToDictionary(static row => (row.Project, row.Namespace));
        Dictionary<(string, string, string, string), Dictionary<string, long>> sums = [];
        foreach (NamespaceEdgeRow row in edgeRows)
        {
            if (!byKey.ContainsKey((row.FromProject, row.FromNamespace)) || !byKey.ContainsKey((row.ToProject, row.ToNamespace)))
                continue;

            (string, string, string, string) key = (row.FromProject, row.FromNamespace, row.ToProject, row.ToNamespace);
            if (!sums.TryGetValue(key, out Dictionary<string, long>? kinds))
                sums[key] = kinds = [];

            kinds[row.Kind] = kinds.GetValueOrDefault(row.Kind) + row.Count;
        }

        List<(string From, string To, IReadOnlyList<UseCount> Kinds, int Total)> kept = [];
        Dictionary<(string, string), long> scores = [];
        foreach (((string fromProject, string fromNamespace, string toProject, string toNamespace), Dictionary<string, long> kinds) in sums)
        {
            IReadOnlyList<UseCount> counts = ToCounts(kinds);
            int total = counts.Sum(static count => count.Count);
            if (total < minWeight)
                continue;

            kept.Add((NamespaceKey(fromProject, fromNamespace), NamespaceKey(toProject, toNamespace), counts, total));
            scores[(fromProject, fromNamespace)] = scores.GetValueOrDefault((fromProject, fromNamespace)) + total;
            scores[(toProject, toNamespace)] = scores.GetValueOrDefault((toProject, toNamespace)) + total;
        }

        List<string> notes = [];
        bool truncated = false;
        List<NamespaceRow> shown = rows;
        if (rows.Count > maxNodes)
        {
            truncated = true;
            shown = [.. rows.OrderByDescending(row => scores.GetValueOrDefault((row.Project, row.Namespace))).ThenBy(static row => row.Project, StringComparer.Ordinal).ThenBy(static row => row.Namespace, StringComparer.Ordinal).Take(maxNodes)];
            shown.Sort(static (a, b) => CompareKeys((a.Project, a.Namespace), (b.Project, b.Namespace)));
            notes.Add($"{rows.Count - maxNodes} namespaces with the weakest dependencies are hidden. The limit is {maxNodes} nodes.");
        }

        bool perProject = project is null;
        List<DiagramCluster> clusters = [];
        List<DiagramNode> nodes = [];
        Dictionary<string, string> ids = [];
        foreach (NamespaceRow row in shown)
        {
            string id = perProject ? NamespaceKey(row.Project, row.Namespace) : NamespaceLabel(row.Namespace);
            ids[NamespaceKey(row.Project, row.Namespace)] = id;
            string? cluster = null;
            if (perProject)
            {
                cluster = "project:" + row.Project;
                if (clusters.TrueForAll(item => item.Id != cluster))
                    clusters.Add(new DiagramCluster(cluster, row.Project));
            }

            nodes.Add(new DiagramNode(
                id, DiagramNodeKinds.Namespace, NamespaceLabel(row.Namespace), row.Namespace, row.Project, null, row.IsTest, false, false, (int)row.Types,
                Href(source.SourceId, DiagramViews.Namespace, ("namespace", row.Namespace), ("project", row.Project)), cluster, [], 0));
        }

        List<DiagramEdge> edges = [.. kept
            .Where(edge => ids.ContainsKey(edge.From) && ids.ContainsKey(edge.To))
            .Select(edge => new DiagramEdge(ids[edge.From], ids[edge.To], DiagramEdgeStyles.Dependency, edge.Kinds, edge.Total))
            .OrderBy(edge => IndexOf(nodes, edge.From)).ThenBy(edge => IndexOf(nodes, edge.To))];
        if (LimitEdges(edges, maxEdges) is { } hiddenEdges)
        {
            truncated = true;
            notes.Add(EdgeNote(hiddenEdges));
        }

        string title = project is null ? "Namespaces of " + source.Name : "Namespaces of " + project;
        return Build(DiagramViews.Namespaces, title, nodes, edges, clusters, truncated, notes);
    }

    private async Task<Source> RequireGraphAsync(NpgsqlConnection connection, int? sourceId, CancellationToken cancellationToken)
    {
        if (sourceId is not { } id)
            throw new CodeSearchException("sourceId", "The source is required.");

        Source source = await sources.GetAsync(id, null, cancellationToken) ?? throw new CodeDiagramNotFoundException($"The knowledge base has no source {id}.");
        if (source.Kind != SourceKind.Code)
            throw new CodeSearchException("sourceId", $"The source {source.Name} is not a code source.");

        if (!await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS (SELECT 1 FROM code_graph_state WHERE source_id = @SourceId AND built_at IS NOT NULL)", new { source.SourceId }, cancellationToken: cancellationToken)))
        {
            throw new CodeDiagramNotFoundException($"The code source {source.Name} has no code graph yet. Wait for the indexer to build it, or check the status of the graph.");
        }

        return source;
    }

    private static int ParseLimit(int? value, int fallback)
    {
        int limit = value ?? fallback;
        return limit is < MinNodeLimit or > MaxNodeLimit
            ? throw new CodeSearchException("maxNodes", $"The limit of nodes must be from {MinNodeLimit} to {MaxNodeLimit}.")
            : limit;
    }

    private static int ParseEdgeLimit(int? value)
    {
        int limit = value ?? DefaultEdges;
        return limit is < MinEdgeLimit or > MaxEdgeLimit
            ? throw new CodeSearchException("maxEdges", $"The limit of edges must be from {MinEdgeLimit} to {MaxEdgeLimit}.")
            : limit;
    }

    // Keeps the heaviest dependency edges. The inherit and implement edges stay and do not count. Returns the number of removed edges, or null when none.
    private static int? LimitEdges(List<DiagramEdge> edges, int maxEdges)
    {
        List<DiagramEdge> dependencies = [.. edges.Where(static edge => edge.Style == DiagramEdgeStyles.Dependency)];
        if (dependencies.Count <= maxEdges)
            return null;

        HashSet<DiagramEdge> dropped = [.. dependencies.Select(static (edge, index) => (edge, index)).OrderByDescending(static pair => pair.edge.Total).ThenBy(static pair => pair.index).Skip(maxEdges).Select(static pair => pair.edge)];
        edges.RemoveAll(dropped.Contains);
        return dropped.Count;
    }

    private static string EdgeNote(int hidden) => $"{hidden} lighter edges are hidden. Raise maxEdges or minWeight.";

    private static CodeDiagram Build(string view, string title, List<DiagramNode> nodes, List<DiagramEdge> edges, List<DiagramCluster> clusters, bool truncated, List<string> notes)
    {
        CodeDiagram diagram = new(view, title, nodes, edges, clusters, truncated, notes, string.Empty);
        return diagram with { Dot = DotWriter.Write(diagram) };
    }

    private static string Href(int sourceId, string view, params (string Key, string? Value)[] values)
    {
        StringBuilder address = new("/code/diagram?source=");
        address.Append(sourceId).Append("&view=").Append(view);
        foreach ((string key, string? value) in values)
        {
            if (value is not null)
                address.Append('&').Append(key).Append('=').Append(Uri.EscapeDataString(value));
        }

        return address.ToString();
    }

    private static int IndexOf(List<DiagramNode> nodes, string id) => nodes.FindIndex(node => node.Id == id);

    private static string NamespaceKey(string project, string name) => project + "::" + NamespaceLabel(name);

    private static string NamespaceLabel(string name) => name.Length == 0 ? "(global)" : name;

    private static int CompareKeys((string Project, string Namespace) a, (string Project, string Namespace) b)
    {
        int project = string.CompareOrdinal(a.Project, b.Project);
        return project != 0 ? project : string.CompareOrdinal(a.Namespace, b.Namespace);
    }

    private static int KindOrder(string kind)
    {
        for (int i = 0; i < CodeUseKinds.All.Count; i++)
        {
            if (CodeUseKinds.All[i] == kind)
                return i;
        }

        return CodeUseKinds.All.Count;
    }

    private static IReadOnlyList<UseCount> ToCounts(Dictionary<string, long> kinds) =>
        [.. kinds.OrderBy(static pair => KindOrder(pair.Key)).ThenBy(static pair => pair.Key, StringComparer.Ordinal).Select(static pair => new UseCount(pair.Key, (int)Math.Min(pair.Value, int.MaxValue)))];

    private sealed class ProjectRow
    {
        public int ProjectId { get; init; }

        public string Path { get; init; } = string.Empty;

        public string Name { get; init; } = string.Empty;

        public bool IsTest { get; init; }

        public long Types { get; init; }
    }

    private sealed class NamespaceRow
    {
        public string Project { get; init; } = string.Empty;

        public string Namespace { get; init; } = string.Empty;

        public bool IsTest { get; init; }

        public long Types { get; init; }
    }

    private sealed class NamespaceEdgeRow
    {
        public string FromProject { get; init; } = string.Empty;

        public string FromNamespace { get; init; } = string.Empty;

        public string ToProject { get; init; } = string.Empty;

        public string ToNamespace { get; init; } = string.Empty;

        public string Kind { get; init; } = string.Empty;

        public long Count { get; init; }
    }
}
