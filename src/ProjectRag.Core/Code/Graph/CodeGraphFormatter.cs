using System.Globalization;
using System.Text;

namespace ProjectRag.Core.Code.Graph;

/// <summary>
/// Writes the answers of the code graph tools as compact text with relative paths and <c>path:line</c> places.
/// </summary>
public static class CodeGraphFormatter
{
    private const string Dash = " — ";

    /// <summary>
    /// Cuts the first sentence of a summary.
    /// </summary>
    /// <param name="summary">The summary, or <see langword="null"/>.</param>
    /// <returns>The first sentence on one line, or an empty text.</returns>
    public static string FirstSentence(string? summary)
    {
        if (string.IsNullOrWhiteSpace(summary))
            return string.Empty;

        string text = string.Join(' ', summary.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '.' && (i == text.Length - 1 || text[i + 1] == ' '))
                return text[..(i + 1)];
        }

        return text;
    }

    /// <summary>
    /// Writes the place of a symbol.
    /// </summary>
    /// <param name="symbol">The symbol.</param>
    /// <returns>The text <c>path:line</c>, or a dash when the symbol has no declaration.</returns>
    public static string Place(SymbolSummary symbol)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        return symbol.Path is null ? "-" : symbol.Line is { } line ? string.Create(CultureInfo.InvariantCulture, $"{symbol.Path}:{line}") : symbol.Path;
    }

    /// <summary>
    /// Writes the result of the symbol search.
    /// </summary>
    /// <param name="matches">The matches.</param>
    /// <returns>A line for each match: <c>kind fullName — signature — project — path:line</c>, then the first sentence of the summary, indented.</returns>
    public static string Symbols(IReadOnlyList<SymbolMatch> matches)
    {
        ArgumentNullException.ThrowIfNull(matches);
        if (matches.Count == 0)
            return "No symbols match.";

        StringBuilder text = new();
        foreach (SymbolMatch match in matches)
        {
            text.Append(match.Kind).Append(' ').Append(match.FullName).Append(Dash).Append(match.Signature).Append(Dash).Append(match.Project).Append(Dash).AppendLine(Place(match));
            if (FirstSentence(match.Summary) is { Length: > 0 } summary)
                text.Append("  ").AppendLine(summary);
        }

        return text.ToString();
    }

    /// <summary>
    /// Writes the message for a text that names several symbols.
    /// </summary>
    /// <param name="text">The text of the agent.</param>
    /// <param name="groups">The groups that match.</param>
    /// <returns>The message with up to 10 candidates and their IDs.</returns>
    public static string Ambiguous(string text, IReadOnlyList<CodeSymbolGroup> groups)
    {
        ArgumentNullException.ThrowIfNull(groups);
        StringBuilder message = new();
        message.Append(CultureInfo.InvariantCulture, $"The symbol {text} is ambiguous: {groups.Count} symbols match. Repeat the call with the full name or the ID of one:");
        foreach (CodeSymbolGroup group in groups.Take(10))
        {
            message.AppendLine();
            message.Append(CultureInfo.InvariantCulture, $"{group.Primary.Kind} {group.Primary.FullName}{Dash}{group.Primary.Project}{Dash}{Place(group.Primary)}{Dash}{group.Primary.DocId}");
        }

        if (groups.Count > 10)
        {
            message.AppendLine();
            message.Append(CultureInfo.InvariantCulture, $"and {groups.Count - 10} more.");
        }

        return message.ToString();
    }

    /// <summary>
    /// Writes a page of references.
    /// </summary>
    /// <param name="page">The page, ordered by path and line.</param>
    /// <param name="limit">The limit of the request.</param>
    /// <returns>The files with their lines: <c>  line:col [kind] text   (in From.fullName)</c>.</returns>
    public static string References(CodeReferencePage page, int limit)
    {
        ArgumentNullException.ThrowIfNull(page);
        if (page.Items.Count == 0)
            return "No references.";

        StringBuilder text = new();
        long documentId = -1;
        foreach (CodeReference reference in page.Items)
        {
            if (reference.DocumentId != documentId)
            {
                documentId = reference.DocumentId;
                text.AppendLine(reference.Path);
            }

            text.Append(CultureInfo.InvariantCulture, $"  {reference.Line}:{reference.Column} [{reference.Kind}] {(reference.TextStart > 0 ? "…" : string.Empty)}{reference.Text.Trim()}");
            if (reference.From is { } from)
                text.Append(CultureInfo.InvariantCulture, $"   (in {from.FullName})");

            text.AppendLine();
        }

        if (page.Total > page.Items.Count)
            text.AppendLine(CultureInfo.InvariantCulture, $"Showing {page.Items.Count} of {page.Total} references (limit {limit}). Narrow the request with kind or path.");

        return text.ToString();
    }

    /// <summary>
    /// Writes the outline of a file.
    /// </summary>
    /// <param name="path">The path of the file.</param>
    /// <param name="nodes">The roots of the outline.</param>
    /// <returns>The path, then the tree with two spaces for each level: <c>line: signature</c>.</returns>
    public static string OutlineFile(string path, IReadOnlyList<OutlineNode> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        StringBuilder text = new();
        text.AppendLine(path);
        if (nodes.Count == 0)
        {
            text.AppendLine("  No symbols. The graph holds no declaration in this file.");
            return text.ToString();
        }

        foreach (OutlineNode node in nodes)
            AppendNode(text, node, 1);

        return text.ToString();
    }

    /// <summary>
    /// Writes the members of a type with its bases and its derived types.
    /// </summary>
    /// <param name="detail">The detail of the type.</param>
    /// <returns>The text.</returns>
    public static string OutlineType(SymbolDetail detail)
    {
        ArgumentNullException.ThrowIfNull(detail);
        SymbolSummary type = detail.Symbol;
        StringBuilder text = new();
        text.Append(type.Kind).Append(' ').Append(type.FullName).Append(Dash).Append(type.Project).Append(Dash).AppendLine(Place(type));
        text.AppendLine(type.Signature);
        if (FirstSentence(type.Summary) is { Length: > 0 } summary)
            text.AppendLine(summary);

        if (detail.Declarations.Count > 1)
            text.AppendLine("Partial: " + string.Join(", ", detail.Declarations.Select(static declaration => $"{declaration.Path}:{declaration.NameLine}")));

        AppendList(text, "Bases", detail.Bases);
        AppendList(text, "Derived", detail.Derived);
        text.AppendLine(CultureInfo.InvariantCulture, $"Members ({detail.Members.Count}):");
        foreach (SymbolSummary member in detail.Members)
        {
            string place = member.Path == type.Path ? member.Line?.ToString(CultureInfo.InvariantCulture) ?? "-" : Place(member);
            text.Append(CultureInfo.InvariantCulture, $"  {place}: {member.Signature}");
            if (FirstSentence(member.Summary) is { Length: > 0 } sentence)
                text.Append(Dash).Append(sentence);

            text.AppendLine();
        }

        return text.ToString();
    }

    /// <summary>
    /// Writes the relations of a symbol.
    /// </summary>
    /// <param name="relations">The relations.</param>
    /// <returns>The text.</returns>
    public static string Relations(CodeRelations relations)
    {
        ArgumentNullException.ThrowIfNull(relations);
        CodeSymbolGroup group = relations.Group;
        SymbolSummary symbol = group.Primary;
        StringBuilder text = new();
        text.Append(symbol.Kind).Append(' ').Append(symbol.FullName).Append(Dash).Append(symbol.Project).Append(Dash).AppendLine(Place(symbol));
        if (group.Symbols.Count > 1)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"{group.Symbols.Count} overloads or forms:");
            foreach (SymbolSummary overload in group.Symbols)
                text.AppendLine(CultureInfo.InvariantCulture, $"  {overload.Signature}");
        }
        else
        {
            text.AppendLine(symbol.Signature);
        }

        if (group.IsType)
        {
            AppendList(text, "Bases", relations.Bases);
            AppendList(text, "Derived", relations.Derived);
        }
        else
        {
            AppendList(text, "Overrides or implements", relations.Overrides);
            AppendList(text, "Overridden or implemented by", relations.OverriddenBy);
        }

        string uses = group.IsType ? "Uses (types)" : "Callees (uses)";
        string usedBy = group.IsType ? "Used by (types)" : "Callers (used by)";
        AppendNodes(text, uses, relations.Uses, relations.Depth, relations.UsesTruncated);
        AppendNodes(text, usedBy, relations.UsedBy, relations.Depth, relations.UsedByTruncated);
        if (relations.External.Count > 0)
        {
            text.AppendLine("External uses:");
            foreach (ExternalUse use in relations.External)
                text.AppendLine(CultureInfo.InvariantCulture, $"  {use.Target} ({use.Assembly}){Dash}{Kinds(use.Kinds)}");
        }

        return text.ToString();
    }

    /// <summary>
    /// Writes the overview of the graph: the state, the projects and the solutions.
    /// </summary>
    /// <param name="overview">The overview.</param>
    /// <returns>The text.</returns>
    public static string MapOverview(CodeMapOverview overview)
    {
        ArgumentNullException.ThrowIfNull(overview);
        CodeGraphStatus status = overview.Status;
        StringBuilder text = new();
        text.Append(CultureInfo.InvariantCulture, $"Code graph of {status.Source}: ");
        text.AppendLine(status.BuiltAt is { } builtAt
            ? string.Create(CultureInfo.InvariantCulture, $"built {builtAt:yyyy-MM-dd HH:mm} UTC in {(status.DurationMs ?? 0) / 1000.0:F1} s, SDK {status.SdkVersion ?? "unknown"}, {status.ProjectCount} projects, {status.SymbolCount} symbols, {status.ReferenceCount} references")
            : "not built yet");
        if (status.BuiltAt is not null)
        {
            text.AppendLine(status.UpdatedAt is { } updatedAt
                ? string.Create(CultureInfo.InvariantCulture, $"Updated {status.UpdateCount} times since the full build, the last time {updatedAt:yyyy-MM-dd HH:mm} UTC in {(status.UpdateDurationMs ?? 0) / 1000.0:F1} s")
                : "No update since the full build");
        }

        if (status.Error is { } error)
            text.AppendLine(CultureInfo.InvariantCulture, $"The last build failed, the old graph stays: {error}");

        Dictionary<int, string> names = status.Projects.ToDictionary(static project => project.ProjectId, static project => project.Name);
        text.AppendLine(CultureInfo.InvariantCulture, $"Projects ({status.Projects.Count}):");
        foreach (CodeProject project in status.Projects)
        {
            text.Append(CultureInfo.InvariantCulture, $"  {project.Name}{(project.IsTest ? " [test]" : string.Empty)} {project.TargetFramework ?? "no framework"}{Dash}{project.Path}{Dash}files {project.DocumentCount}, types {overview.TypeCounts.GetValueOrDefault(project.ProjectId)}, errors {project.ErrorCount}");
            if (project.References.Count > 0)
                text.Append(" -> ").Append(string.Join(", ", project.References.Select(id => names.GetValueOrDefault(id, "?"))));

            text.AppendLine();
        }

        if (status.Solutions.Count > 0)
        {
            text.AppendLine("Solutions:");
            foreach (CodeSolution solution in status.Solutions)
                text.AppendLine(CultureInfo.InvariantCulture, $"  {solution.Path}: {string.Join(", ", solution.ProjectIds.Select(id => names.GetValueOrDefault(id, "?")))}");
        }

        int errors = status.Diagnostics.Count(static diagnostic => diagnostic.Severity == "error");
        if (errors > 0)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"Problems ({status.Diagnostics.Count}, {errors} errors), the first ones:");
            foreach (CodeDiagnostic diagnostic in status.Diagnostics.Take(5))
                text.AppendLine(CultureInfo.InvariantCulture, $"  {diagnostic.Severity} {diagnostic.Code} {diagnostic.Message}{(diagnostic.Path is null ? string.Empty : $" ({diagnostic.Path}:{diagnostic.Line})")}");
        }

        return text.ToString();
    }

    /// <summary>
    /// Writes the namespaces of a project and the dependencies between them.
    /// </summary>
    /// <param name="map">The map.</param>
    /// <returns>The text.</returns>
    public static string ProjectMap(CodeProjectMap map)
    {
        ArgumentNullException.ThrowIfNull(map);
        StringBuilder text = new();
        text.AppendLine(CultureInfo.InvariantCulture, $"Project {map.Project.Name}{(map.Project.IsTest ? " [test]" : string.Empty)} {map.Project.TargetFramework ?? "no framework"}{Dash}{map.Project.Path}");
        text.AppendLine(CultureInfo.InvariantCulture, $"Namespaces ({map.Namespaces.Count}):");
        foreach (NamespaceCount item in map.Namespaces)
            text.AppendLine(CultureInfo.InvariantCulture, $"  {(item.Namespace.Length == 0 ? "(global)" : item.Namespace)}{Dash}{item.Types} types");

        text.AppendLine(CultureInfo.InvariantCulture, $"Dependencies between the namespaces of the project, the strongest {map.Dependencies.Count}:");
        foreach (NamespaceDependency item in map.Dependencies)
            text.AppendLine(CultureInfo.InvariantCulture, $"  {(item.From.Length == 0 ? "(global)" : item.From)} -> {(item.To.Length == 0 ? "(global)" : item.To)}: {item.Weight}");

        return text.ToString();
    }

    /// <summary>
    /// Writes the types of a namespace with their neighbors.
    /// </summary>
    /// <param name="map">The map.</param>
    /// <returns>The text.</returns>
    public static string NamespaceMap(CodeNamespaceMap map)
    {
        ArgumentNullException.ThrowIfNull(map);
        StringBuilder text = new();
        if (map.Types.Count == 0)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"No types in the namespace {map.Namespace}.");
            if (map.Similar.Count > 0)
                text.AppendLine("Similar namespaces: " + string.Join(", ", map.Similar));

            return text.ToString();
        }

        text.AppendLine(CultureInfo.InvariantCulture, $"Namespace {map.Namespace}: {map.Types.Count} types");
        foreach (NamespaceType type in map.Types)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"{type.Symbol.Kind} {type.Symbol.FullName}{Dash}{type.Symbol.Project}{Dash}{Place(type.Symbol)}");
            if (type.TopUses.Count > 0)
                text.AppendLine("  uses: " + string.Join(", ", type.TopUses.Select(static edge => $"{edge.Symbol.FullName} ({edge.Total})")));

            if (type.TopUsedBy.Count > 0)
                text.AppendLine("  used by: " + string.Join(", ", type.TopUsedBy.Select(static edge => $"{edge.Symbol.FullName} ({edge.Total})")));
        }

        return text.ToString();
    }

    private static void AppendNode(StringBuilder text, OutlineNode node, int level)
    {
        text.Append(' ', level * 2).AppendLine(CultureInfo.InvariantCulture, $"{node.Symbol.Line}: {node.Symbol.Signature}");
        foreach (OutlineNode child in node.Children)
            AppendNode(text, child, level + 1);
    }

    private static void AppendList(StringBuilder text, string title, IReadOnlyList<SymbolSummary> symbols)
    {
        if (symbols.Count == 0)
            return;

        text.AppendLine(CultureInfo.InvariantCulture, $"{title}:");
        foreach (SymbolSummary symbol in symbols)
            text.AppendLine(CultureInfo.InvariantCulture, $"  {symbol.Kind} {symbol.FullName}{Dash}{symbol.Project}{Dash}{Place(symbol)}");
    }

    private static void AppendNodes(StringBuilder text, string title, IReadOnlyList<RelationNode> nodes, int depth, bool truncated)
    {
        if (nodes.Count == 0)
            return;

        text.AppendLine(CultureInfo.InvariantCulture, $"{title}:");
        foreach (RelationNode node in nodes)
        {
            text.Append("  ");
            if (depth > 1)
                text.Append(CultureInfo.InvariantCulture, $"[{node.Depth}] ");

            text.Append(CultureInfo.InvariantCulture, $"{node.Symbol.Kind} {node.Symbol.FullName}{Dash}{Kinds(node.Kinds)}{Dash}{node.Symbol.Project}{Dash}{Place(node.Symbol)}");
            if (node.Via is { } via)
                text.Append(CultureInfo.InvariantCulture, $" (via {via.FullName})");

            text.AppendLine();
        }

        if (truncated)
            text.AppendLine(CultureInfo.InvariantCulture, $"  The list stops at {CodeGraphService.MaxRelationNodes} nodes. Narrow it with kinds or includeTests.");
    }

    private static string Kinds(IReadOnlyList<UseCount> kinds) => string.Join(", ", kinds.Select(static kind => string.Create(CultureInfo.InvariantCulture, $"{kind.Kind} {kind.Count}")));
}
