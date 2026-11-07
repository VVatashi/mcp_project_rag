using Dapper;

using Npgsql;

using ProjectRag.Core.Code.Graph;
using ProjectRag.Core.Sources;

namespace ProjectRag.Core.Code.Diagrams;

public sealed partial class CodeDiagramService
{
    private const string TypeColumns = "s.symbol_id, s.project_id, p.name AS project, p.is_test, s.kind, s.name, s.full_name, s.doc_id, s.signature, s.accessibility, s.modifiers, s.namespace";

    private const string TypeFrom = "FROM code_symbol s JOIN code_project p ON p.project_id = s.project_id";

    /// <summary>
    /// Builds the diagram of the types of one namespace.
    /// </summary>
    /// <param name="request">The query.</param>
    /// <param name="cancellationToken">The token that cancels the queries.</param>
    /// <returns>The diagram.</returns>
    /// <exception cref="CodeSearchException">A field of the query is not valid.</exception>
    /// <exception cref="CodeDiagramNotFoundException">The source has no graph, or the namespace has no types.</exception>
    public async Task<CodeDiagram> GetNamespaceAsync(NamespaceDiagramRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Namespace is null)
            throw new CodeSearchException("namespace", "The namespace is required.");

        string name = request.Namespace.Trim();
        string? project = string.IsNullOrWhiteSpace(request.Project) ? null : request.Project.Trim();
        DiagramMembers members = ParseMembers(request.Members, DiagramMembers.None);
        int maxNodes = ParseLimit(request.MaxNodes, DefaultTypeNodes);
        int maxEdges = ParseEdgeLimit(request.MaxEdges);
        await using NpgsqlConnection connection = await database.OpenAsync(cancellationToken);
        Source source = await RequireGraphAsync(connection, request.SourceId, cancellationToken);

        List<TypeRow> types = [.. await connection.QueryAsync<TypeRow>(new CommandDefinition(
            $"""
            SELECT {TypeColumns}
            {TypeFrom}
            WHERE s.source_id = @SourceId AND s.namespace = @Namespace AND s.kind = ANY (@Kinds) AND (@Project::text IS NULL OR p.name = @Project)
            """,
            new { source.SourceId, Namespace = name, Kinds = TypeKinds, Project = project }, cancellationToken: cancellationToken))];
        if (types.Count == 0)
            throw new CodeDiagramNotFoundException($"The code source {source.Name} has no types in the namespace {NamespaceLabel(name)}.");

        types.Sort(CompareTypes);
        List<EdgeRow> edgeRows = await LoadEdgesAsync(connection, [.. types.Select(static type => type.SymbolId)], [], cancellationToken);
        List<string> notes = [];
        bool truncated = false;
        if (types.Count > maxNodes)
        {
            truncated = true;
            Dictionary<int, long> scores = [];
            foreach (EdgeRow edge in edgeRows)
            {
                scores[edge.FromId] = scores.GetValueOrDefault(edge.FromId) + edge.Count;
                scores[edge.ToId] = scores.GetValueOrDefault(edge.ToId) + edge.Count;
            }

            notes.Add($"{types.Count - maxNodes} types with the fewest edges are hidden. The limit is {maxNodes} nodes.");
            types = [.. types.OrderByDescending(type => scores.GetValueOrDefault(type.SymbolId)).ThenBy(static type => type.FullName, StringComparer.Ordinal).ThenBy(static type => type.Project, StringComparer.Ordinal).Take(maxNodes)];
            types.Sort(CompareTypes);
        }

        int[] ids = [.. types.Select(static type => type.SymbolId)];
        HashSet<int> shown = [.. ids];
        edgeRows.RemoveAll(edge => !shown.Contains(edge.FromId) || !shown.Contains(edge.ToId));
        Dictionary<int, MemberSet> memberSets = members == DiagramMembers.None ? [] : await LoadMembersAsync(connection, ids, members, cancellationToken);

        List<DiagramNode> nodes = [.. types.Select(type => TypeNode(source.SourceId, type, false, memberSets.GetValueOrDefault(type.SymbolId)))];
        Dictionary<int, string> nodeIds = types.ToDictionary(static type => type.SymbolId, static type => TypeNodeId(type));
        List<DiagramEdge> edges = BuildTypeEdges(edgeRows, nodeIds);
        SortEdges(edges, nodes);
        if (LimitEdges(edges, maxEdges) is { } hiddenEdges)
        {
            truncated = true;
            notes.Add(EdgeNote(hiddenEdges));
        }

        if (request.IncludeExternal ?? false)
        {
            List<ExternalRow> external = [.. await connection.QueryAsync<ExternalRow>(new CommandDefinition(
                """
                SELECT from_id, target, assembly, kind, sum(count) AS count
                FROM code_external_edge
                WHERE from_id = ANY (@Ids) AND kind IN ('inherit', 'implement')
                GROUP BY from_id, target, assembly, kind
                """,
                new { Ids = ids }, cancellationToken: cancellationToken))];
            AddExternal(external, nodeIds, nodes, edges);
        }

        SortEdges(edges, nodes);
        string title = name.Length == 0 ? "Types of the global namespace" : "Types of " + name;
        return Build(DiagramViews.Namespace, title, nodes, edges, [], truncated, notes);
    }

    /// <summary>
    /// Builds the diagram of the neighborhood of one type.
    /// </summary>
    /// <remarks>
    /// The inherit and implement edges of the focus come first: the bases up the chain, then the direct derived types. The other neighbors follow, level by level, by the sum of their edges.
    /// </remarks>
    /// <param name="request">The query.</param>
    /// <param name="cancellationToken">The token that cancels the queries.</param>
    /// <returns>The diagram.</returns>
    /// <exception cref="CodeSearchException">A field of the query is not valid.</exception>
    /// <exception cref="CodeDiagramNotFoundException">The source has no graph, or it has no such symbol.</exception>
    public async Task<CodeDiagram> GetTypeAsync(TypeDiagramRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.DocId))
            throw new CodeSearchException("docId", "The documentation comment ID is required.");

        int depth = request.Depth ?? 1;
        if (depth is < 1 or > MaxDepth)
            throw new CodeSearchException("depth", $"The depth must be from 1 to {MaxDepth}.");

        CodeRelationDirection direction = ParseDirection(request.Direction);
        string[] useKinds = ParseKinds(request.Kinds);
        DiagramMembers members = ParseMembers(request.Members, DiagramMembers.Public);
        int maxNodes = ParseLimit(request.MaxNodes, DefaultTypeNodes);
        bool includeTests = request.IncludeTests ?? true;
        string docId = request.DocId.Trim();
        string? project = string.IsNullOrWhiteSpace(request.Project) ? null : request.Project.Trim();

        await using NpgsqlConnection connection = await database.OpenAsync(cancellationToken);
        Source source = await RequireGraphAsync(connection, request.SourceId, cancellationToken);
        int focusId = await ResolveFocusAsync(connection, source, docId, project, cancellationToken);

        List<int> order = [focusId];
        HashSet<int> known = [focusId];
        int hidden = 0;
        bool outgoing = direction != CodeRelationDirection.UsedBy;
        bool incoming = direction != CodeRelationDirection.Uses;

        async Task<List<int>> StepAsync(int[] frontier, bool inheritOnly, bool useOutgoing, bool useIncoming, bool byTotal)
        {
            int room = Math.Max(maxNodes - order.Count, 0);
            List<NeighborRow> rows = await LoadNeighborsAsync(
                connection, source.SourceId, frontier, [.. known], inheritOnly, useOutgoing, useIncoming, byTotal, useKinds, includeTests, Math.Max(room, 1), cancellationToken);
            if (rows.Count == 0)
                return [];

            List<int> taken = [.. rows.Take(room).Select(static row => row.SymbolId)];
            hidden += (int)(rows[0].Candidates - taken.Count);
            foreach (int id in taken)
            {
                known.Add(id);
                order.Add(id);
            }

            return taken;
        }

        List<int> close = [];
        if (outgoing)
        {
            int[] frontier = [focusId];
            for (int level = 0; level < MaxChain && frontier.Length > 0; level++)
            {
                List<int> bases = await StepAsync(frontier, true, true, false, false);
                close.AddRange(bases);
                frontier = [.. bases];
            }
        }

        if (incoming)
            close.AddRange(await StepAsync([focusId], true, false, true, false));

        Dictionary<int, int> levels = new() { [focusId] = 0 };
        foreach (int id in close)
            levels.TryAdd(id, 1);

        int[] walk = [focusId];
        for (int level = 1; level <= depth && walk.Length > 0; level++)
        {
            List<int> added = await StepAsync(walk, false, outgoing, incoming, true);
            foreach (int id in added)
                levels.TryAdd(id, level);

            walk = level == 1 ? [.. close, .. added] : [.. added];
            if (order.Count >= maxNodes)
                break;
        }

        int[] ids = [.. order];
        Dictionary<int, TypeRow> rowsById = (await connection.QueryAsync<TypeRow>(new CommandDefinition(
            $"SELECT {TypeColumns} {TypeFrom} WHERE s.symbol_id = ANY (@Ids)", new { Ids = ids }, cancellationToken: cancellationToken))).ToDictionary(static row => row.SymbolId);
        ids = [.. ids.Where(rowsById.ContainsKey)];

        bool neighborMembers = request.NeighborMembers ?? false;
        int[] withMembers = members == DiagramMembers.None ? [] : neighborMembers ? ids : [focusId];
        Dictionary<int, MemberSet> memberSets = withMembers.Length == 0 ? [] : await LoadMembersAsync(connection, withMembers, members, cancellationToken);

        bool neighborEdges = request.NeighborEdges ?? false;
        List<DiagramNode> nodes = [.. ids.Select(id => TypeNode(source.SourceId, rowsById[id], id == focusId, memberSets.GetValueOrDefault(id), neighborEdges))];
        Dictionary<int, string> nodeIds = ids.ToDictionary(static id => id, id => TypeNodeId(rowsById[id]));
        List<DiagramEdge> edges = BuildTypeEdges(await LoadEdgesAsync(connection, ids, useKinds, cancellationToken), nodeIds);
        if (!neighborEdges)
        {
            string focusNode = nodeIds[focusId];
            HashSet<DiagramEdge> tree = TreeEdges(edges, levels.Where(pair => nodeIds.ContainsKey(pair.Key)).ToDictionary(pair => nodeIds[pair.Key], static pair => pair.Value));
            edges.RemoveAll(edge => edge.Style == DiagramEdgeStyles.Dependency && edge.From != focusNode && edge.To != focusNode && !tree.Contains(edge));
        }

        if ((request.IncludeExternal ?? false) && outgoing)
        {
            List<ExternalRow> external = [.. await connection.QueryAsync<ExternalRow>(new CommandDefinition(
                """
                SELECT from_id, target, assembly, kind, sum(count) AS count
                FROM code_external_edge
                WHERE from_id = @Focus AND (cardinality(@UseKinds) = 0 OR kind = ANY (@UseKinds))
                GROUP BY from_id, target, assembly, kind
                """,
                new { Focus = focusId, UseKinds = useKinds }, cancellationToken: cancellationToken))];
            AddExternal(external, nodeIds, nodes, edges);
        }

        SortEdges(edges, nodes);
        List<string> notes = [];
        if (hidden > 0)
            notes.Add($"{hidden} more neighbors are not shown. The limit is {maxNodes} nodes.");

        if (nodes.Count > 0 && rowsById[focusId].DocId != docId)
            notes.Add($"{docId} is a member. The diagram shows its type.");

        return Build(DiagramViews.Type, "Neighborhood of " + nodes[0].Label, nodes, edges, [], hidden > 0, notes);
    }

    private static CodeRelationDirection ParseDirection(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return CodeRelationDirection.Both;

        return value.Trim().ToLowerInvariant() switch
        {
            "both" => CodeRelationDirection.Both,
            "uses" => CodeRelationDirection.Uses,
            "usedby" => CodeRelationDirection.UsedBy,
            _ => throw new CodeSearchException("direction", "The direction must be uses, usedBy or both."),
        };
    }

    private static DiagramMembers ParseMembers(string? value, DiagramMembers fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallback;

        return value.Trim().ToLowerInvariant() switch
        {
            "none" => DiagramMembers.None,
            "public" => DiagramMembers.Public,
            "all" => DiagramMembers.All,
            _ => throw new CodeSearchException("members", "The members must be none, public or all."),
        };
    }

    private static string[] ParseKinds(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return [];

        List<string> kinds = [];
        foreach (string part in value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            string kind = part.ToLowerInvariant();
            if (!CodeUseKinds.All.Contains(kind))
                throw new CodeSearchException("kinds", $"The kind {part} is not known. Use {string.Join(", ", CodeUseKinds.All)}.");

            if (!kinds.Contains(kind))
                kinds.Add(kind);
        }

        return [.. kinds];
    }

    // A member resolves to the type around it. When several projects hold the doc id, the first project by name wins.
    private static async Task<int> ResolveFocusAsync(NpgsqlConnection connection, Source source, string docId, string? project, CancellationToken cancellationToken)
    {
        int? start = await connection.ExecuteScalarAsync<int?>(new CommandDefinition(
            """
            SELECT s.symbol_id
            FROM code_symbol s
            JOIN code_project p ON p.project_id = s.project_id
            WHERE s.source_id = @SourceId AND s.doc_id = @DocId AND (@Project::text IS NULL OR p.name = @Project)
            ORDER BY p.name COLLATE "C", s.symbol_id
            LIMIT 1
            """,
            new { source.SourceId, DocId = docId, Project = project }, cancellationToken: cancellationToken));
        int? focus = start is { } id
            ? await connection.ExecuteScalarAsync<int?>(new CommandDefinition(
                """
                WITH RECURSIVE up AS (
                    SELECT symbol_id, parent_id, kind, 0 AS level FROM code_symbol WHERE symbol_id = @Start
                    UNION ALL
                    SELECT s.symbol_id, s.parent_id, s.kind, up.level + 1 FROM code_symbol s JOIN up ON s.symbol_id = up.parent_id
                )
                SELECT symbol_id FROM up WHERE kind = ANY (@Kinds) ORDER BY level LIMIT 1
                """,
                new { Start = id, Kinds = TypeKinds }, cancellationToken: cancellationToken))
            : null;
        return focus ?? throw new CodeDiagramNotFoundException($"The code source {source.Name} has no symbol {docId}.");
    }

    // A node that the walk found beyond the first level keeps its heaviest edge to a node of a lower level, so it does not float alone.
    private static HashSet<DiagramEdge> TreeEdges(List<DiagramEdge> edges, Dictionary<string, int> levels)
    {
        HashSet<DiagramEdge> tree = [];
        foreach (IGrouping<string, DiagramEdge> group in edges
            .Where(static edge => edge.Style == DiagramEdgeStyles.Dependency)
            .Select(edge => (Edge: edge, Deeper: Deeper(edge, levels)))
            .Where(static pair => pair.Deeper is not null)
            .GroupBy(static pair => pair.Deeper!, static pair => pair.Edge))
        {
            tree.Add(group.MaxBy(static edge => edge.Total)!);
        }

        return tree;
    }

    private static string? Deeper(DiagramEdge edge, Dictionary<string, int> levels)
    {
        if (!levels.TryGetValue(edge.From, out int from) || !levels.TryGetValue(edge.To, out int to) || from == to)
            return null;

        (string node, int level) = from > to ? (edge.From, from) : (edge.To, to);
        return level >= 2 ? node : null;
    }

    // One query for a level of the walk. The result holds the best candidates and the number of all candidates.
    private static async Task<List<NeighborRow>> LoadNeighborsAsync(
        NpgsqlConnection connection, int sourceId, int[] frontier, int[] known, bool inheritOnly, bool outgoing, bool incoming, bool byTotal, string[] useKinds, bool includeTests, int limit, CancellationToken cancellationToken)
    {
        string order = byTotal ? "total DESC, s.full_name COLLATE \"C\", s.doc_id COLLATE \"C\"" : "s.full_name COLLATE \"C\", s.doc_id COLLATE \"C\"";
        return [.. await connection.QueryAsync<NeighborRow>(new CommandDefinition(
            $"""
            WITH hits AS (
                SELECT e.to_id AS other, e.kind, e.count FROM code_type_edge e
                WHERE @Outgoing AND e.source_id = @SourceId AND e.from_id = ANY (@Frontier) AND e.to_id <> e.from_id
                UNION ALL
                SELECT e.from_id AS other, e.kind, e.count FROM code_type_edge e
                WHERE @Incoming AND e.source_id = @SourceId AND e.to_id = ANY (@Frontier) AND e.from_id <> e.to_id
            )
            SELECT s.symbol_id, sum(h.count) AS total, count(*) OVER () AS candidates
            FROM hits h
            JOIN code_symbol s ON s.symbol_id = h.other
            JOIN code_project p ON p.project_id = s.project_id
            WHERE NOT (h.other = ANY (@Known))
              AND (NOT @InheritOnly OR h.kind IN ('inherit', 'implement'))
              AND (cardinality(@UseKinds) = 0 OR h.kind = ANY (@UseKinds))
              AND (@IncludeTests OR NOT p.is_test)
            GROUP BY s.symbol_id, s.full_name, s.doc_id
            ORDER BY {order}
            LIMIT @Limit
            """,
            new { SourceId = sourceId, Frontier = frontier, Known = known, InheritOnly = inheritOnly, Outgoing = outgoing, Incoming = incoming, UseKinds = useKinds, IncludeTests = includeTests, Limit = limit },
            cancellationToken: cancellationToken))];
    }

    private static async Task<List<EdgeRow>> LoadEdgesAsync(NpgsqlConnection connection, int[] ids, string[] useKinds, CancellationToken cancellationToken) =>
        [.. await connection.QueryAsync<EdgeRow>(new CommandDefinition(
            """
            SELECT from_id, to_id, kind, sum(count) AS count
            FROM code_type_edge
            WHERE from_id = ANY (@Ids) AND to_id = ANY (@Ids) AND from_id <> to_id AND (cardinality(@UseKinds) = 0 OR kind = ANY (@UseKinds))
            GROUP BY from_id, to_id, kind
            """,
            new { Ids = ids, UseKinds = useKinds }, cancellationToken: cancellationToken))];

    // The members of all types come in one query, in the order of the lines of their first declaration.
    private static async Task<Dictionary<int, MemberSet>> LoadMembersAsync(NpgsqlConnection connection, int[] typeIds, DiagramMembers mode, CancellationToken cancellationToken)
    {
        string[] kinds = [.. DataKinds, .. OperationKinds];
        IEnumerable<MemberRow> rows = await connection.QueryAsync<MemberRow>(new CommandDefinition(
            """
            SELECT s.parent_id, s.kind, s.signature, s.accessibility, s.modifiers
            FROM code_symbol s
            LEFT JOIN LATERAL (
                SELECT doc.path, cd.start_line
                FROM code_declaration cd
                JOIN document doc ON doc.document_id = cd.document_id
                WHERE cd.symbol_id = s.symbol_id
                ORDER BY doc.path COLLATE "C", cd.start_line
                LIMIT 1) d ON true
            WHERE s.parent_id = ANY (@Ids) AND s.kind = ANY (@Kinds)
              AND (@All OR s.accessibility IN ('public', 'protected', 'protected internal', 'private protected'))
            ORDER BY s.parent_id, d.path COLLATE "C", d.start_line, s.symbol_id
            """,
            new { Ids = typeIds, Kinds = kinds, All = mode == DiagramMembers.All }, cancellationToken: cancellationToken));

        Dictionary<int, MemberSet> sets = [];
        foreach (IGrouping<int, MemberRow> group in rows.GroupBy(static row => row.ParentId))
        {
            List<DiagramMember> data = [.. group.Where(static row => DataKinds.Contains(row.Kind)).Select(ToMember)];
            List<DiagramMember> operations = [.. group.Where(static row => !DataKinds.Contains(row.Kind)).Select(ToMember)];
            int hiddenCount = Math.Max(data.Count - MemberLimit, 0) + Math.Max(operations.Count - MemberLimit, 0);
            sets[group.Key] = new MemberSet([.. data.Take(MemberLimit), .. operations.Take(MemberLimit)], hiddenCount);
        }

        return sets;
    }

    private static DiagramMember ToMember(MemberRow row) =>
        new(row.Kind, CodeDiagramText.MemberText(row.Kind, row.Signature), CodeDiagramText.Visibility(row.Accessibility), row.Modifiers.Contains("static"), row.Modifiers.Contains("abstract"));

    private static DiagramNode TypeNode(int sourceId, TypeRow row, bool focus, MemberSet? members, bool neighborEdges = false) =>
        new(
            TypeNodeId(row), row.Kind, CodeDiagramText.TypeLabel(row.Name, row.Signature), row.FullName, row.Project, row.DocId, row.IsTest, focus,
            row.Modifiers.Contains("abstract"), null,
            Href(sourceId, DiagramViews.Type, ("id", row.DocId), ("project", row.Project), ("neighborEdges", neighborEdges ? "1" : null)), null,
            members?.Items ?? [], members?.Hidden ?? 0);

    private static string TypeNodeId(TypeRow row) => row.Project + "|" + row.DocId;

    private static int CompareTypes(TypeRow a, TypeRow b)
    {
        int name = string.CompareOrdinal(a.FullName, b.FullName);
        if (name != 0)
            return name;

        int project = string.CompareOrdinal(a.Project, b.Project);
        return project != 0 ? project : string.CompareOrdinal(a.DocId, b.DocId);
    }

    // An inheritance edge points from the base to the derived type. The other uses of the pair make one dependency edge from the user to the used type.
    private static List<DiagramEdge> BuildTypeEdges(IEnumerable<EdgeRow> rows, Dictionary<int, string> nodeIds)
    {
        List<DiagramEdge> edges = [];
        foreach (IGrouping<(int FromId, int ToId), EdgeRow> pair in rows.GroupBy(static row => (row.FromId, row.ToId)))
        {
            if (!nodeIds.TryGetValue(pair.Key.FromId, out string? from) || !nodeIds.TryGetValue(pair.Key.ToId, out string? to))
                continue;

            Dictionary<string, long> uses = [];
            foreach (EdgeRow row in pair)
            {
                if (row.Kind is CodeUseKinds.Inherit or CodeUseKinds.Implement)
                {
                    int count = Clamp(row.Count);
                    edges.Add(new DiagramEdge(to, from, row.Kind == CodeUseKinds.Inherit ? DiagramEdgeStyles.Inherit : DiagramEdgeStyles.Implement, [new UseCount(row.Kind, count)], count));
                }
                else
                {
                    uses[row.Kind] = uses.GetValueOrDefault(row.Kind) + row.Count;
                }
            }

            if (uses.Count > 0)
                edges.Add(new DiagramEdge(from, to, DiagramEdgeStyles.Dependency, ToCounts(uses), Clamp(uses.Values.Sum())));
        }

        return edges;
    }

    // The most used external types, at most MaxExternal, become nodes. An external base points to the type, a type points to the other external types it uses.
    private static void AddExternal(List<ExternalRow> rows, Dictionary<int, string> nodeIds, List<DiagramNode> nodes, List<DiagramEdge> edges)
    {
        string[] targets = [.. rows.GroupBy(static row => row.Target)
            .OrderByDescending(static group => group.Sum(static row => row.Count)).ThenBy(static group => group.Key, StringComparer.Ordinal)
            .Take(MaxExternal).Select(static group => group.Key)];
        foreach (string target in targets)
        {
            string id = "external:" + target;
            nodes.Add(new DiagramNode(id, DiagramNodeKinds.External, CodeDiagramText.ExternalLabel(target), target, rows.First(row => row.Target == target).Assembly, null, false, false, false, null, null, null, [], 0));
            foreach (IGrouping<int, ExternalRow> owner in rows.Where(row => row.Target == target && nodeIds.ContainsKey(row.FromId)).GroupBy(static row => row.FromId))
            {
                string user = nodeIds[owner.Key];
                Dictionary<string, long> uses = [];
                foreach (ExternalRow row in owner)
                {
                    if (row.Kind is CodeUseKinds.Inherit or CodeUseKinds.Implement)
                    {
                        int count = Clamp(row.Count);
                        edges.Add(new DiagramEdge(id, user, row.Kind == CodeUseKinds.Inherit ? DiagramEdgeStyles.Inherit : DiagramEdgeStyles.Implement, [new UseCount(row.Kind, count)], count));
                    }
                    else
                    {
                        uses[row.Kind] = uses.GetValueOrDefault(row.Kind) + row.Count;
                    }
                }

                if (uses.Count > 0)
                    edges.Add(new DiagramEdge(user, id, DiagramEdgeStyles.Dependency, ToCounts(uses), Clamp(uses.Values.Sum())));
            }
        }
    }

    private static void SortEdges(List<DiagramEdge> edges, List<DiagramNode> nodes)
    {
        Dictionary<string, int> index = new(StringComparer.Ordinal);
        for (int i = 0; i < nodes.Count; i++)
            index.TryAdd(nodes[i].Id, i);

        List<DiagramEdge> sorted = [.. edges
            .OrderBy(edge => index.GetValueOrDefault(edge.From))
            .ThenBy(edge => index.GetValueOrDefault(edge.To))
            .ThenBy(static edge => edge.Style == DiagramEdgeStyles.Inherit ? 0 : edge.Style == DiagramEdgeStyles.Implement ? 1 : 2)];
        edges.Clear();
        edges.AddRange(sorted);
    }

    private static int Clamp(long value) => (int)Math.Min(value, int.MaxValue);

    private sealed record MemberSet(IReadOnlyList<DiagramMember> Items, int Hidden);

    private sealed class TypeRow
    {
        public int SymbolId { get; init; }

        public string Project { get; init; } = string.Empty;

        public bool IsTest { get; init; }

        public string Kind { get; init; } = string.Empty;

        public string Name { get; init; } = string.Empty;

        public string FullName { get; init; } = string.Empty;

        public string DocId { get; init; } = string.Empty;

        public string Signature { get; init; } = string.Empty;

        public string[] Modifiers { get; init; } = [];
    }

    private sealed class MemberRow
    {
        public int ParentId { get; init; }

        public string Kind { get; init; } = string.Empty;

        public string Signature { get; init; } = string.Empty;

        public string Accessibility { get; init; } = string.Empty;

        public string[] Modifiers { get; init; } = [];
    }

    private sealed class EdgeRow
    {
        public int FromId { get; init; }

        public int ToId { get; init; }

        public string Kind { get; init; } = string.Empty;

        public long Count { get; init; }
    }

    private sealed class ExternalRow
    {
        public int FromId { get; init; }

        public string Target { get; init; } = string.Empty;

        public string Assembly { get; init; } = string.Empty;

        public string Kind { get; init; } = string.Empty;

        public long Count { get; init; }
    }

    private sealed class NeighborRow
    {
        public int SymbolId { get; init; }

        public long Total { get; init; }

        public long Candidates { get; init; }
    }
}
