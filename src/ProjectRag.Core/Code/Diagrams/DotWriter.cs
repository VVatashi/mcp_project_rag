using System.Globalization;
using System.Text;

using ProjectRag.Core.Code.Graph;

namespace ProjectRag.Core.Code.Diagrams;

/// <summary>
/// Writes a diagram as DOT text for Graphviz.
/// </summary>
/// <remarks>
/// The text holds no colors. The dashboard styles it with CSS through the classes of the nodes and the edges.
/// The writer is a pure function: the same diagram gives the same text.
/// </remarks>
public static class DotWriter
{
    private const int MaxLabeledEdges = 25;

    /// <summary>
    /// Writes a diagram as DOT text.
    /// </summary>
    /// <param name="diagram">The diagram.</param>
    /// <returns>The text with LF line ends.</returns>
    public static string Write(CodeDiagram diagram)
    {
        ArgumentNullException.ThrowIfNull(diagram);
        StringBuilder text = new();
        string direction = diagram.View is DiagramViews.Projects or DiagramViews.Namespaces ? "LR" : "TB";
        text.Append("digraph code {\n");
        text.Append("  graph [rankdir=").Append(direction).Append(", fontname=\"Helvetica\", fontsize=11, bgcolor=\"transparent\", nodesep=0.3, ranksep=0.5, splines=true, concentrate=false];\n");
        text.Append("  node [fontname=\"Helvetica\", fontsize=11];\n");
        text.Append("  edge [fontname=\"Helvetica\", fontsize=11];\n");

        Dictionary<string, int> indexes = new(StringComparer.Ordinal);
        for (int i = 0; i < diagram.Nodes.Count; i++)
            indexes.TryAdd(diagram.Nodes[i].Id, i);

        HashSet<int> placed = [];
        for (int c = 0; c < diagram.Clusters.Count; c++)
        {
            DiagramCluster cluster = diagram.Clusters[c];
            text.Append("  subgraph cluster_").Append(c.ToString(CultureInfo.InvariantCulture)).Append(" {\n");
            text.Append("    label=").Append(Quote(cluster.Label)).Append(";\n");
            text.Append("    class=\"cluster\";\n");
            for (int i = 0; i < diagram.Nodes.Count; i++)
            {
                if (diagram.Nodes[i].Cluster == cluster.Id && placed.Add(i))
                    AppendNode(text, "    ", i, diagram.Nodes[i]);
            }

            text.Append("  }\n");
        }

        for (int i = 0; i < diagram.Nodes.Count; i++)
        {
            if (!placed.Contains(i))
                AppendNode(text, "  ", i, diagram.Nodes[i]);
        }

        bool labels = diagram.Edges.Count <= MaxLabeledEdges;
        int maxTotal = diagram.Edges.Where(static edge => edge.Style == DiagramEdgeStyles.Dependency).Select(static edge => edge.Total).DefaultIfEmpty(1).Max();
        foreach (DiagramEdge edge in diagram.Edges)
        {
            if (indexes.TryGetValue(edge.From, out int from) && indexes.TryGetValue(edge.To, out int to))
                AppendEdge(text, from, to, edge, labels, maxTotal);
        }

        text.Append("}\n");
        return text.ToString();
    }

    private static void AppendNode(StringBuilder text, string indent, int index, DiagramNode node)
    {
        StringBuilder classes = new("node-");
        classes.Append(node.Kind);
        if (node.IsFocus)
            classes.Append(" is-focus");

        if (node.IsTest)
            classes.Append(" is-test");

        if (node.Kind == DiagramNodeKinds.External)
            classes.Append(" is-external");

        if (node.IsAbstract)
            classes.Append(" is-abstract");

        text.Append(indent).Append('n').Append(index.ToString(CultureInfo.InvariantCulture)).Append(" [class=").Append(Quote(classes.ToString()));
        if (node.Kind is DiagramNodeKinds.Project or DiagramNodeKinds.Namespace)
        {
            StringBuilder label = new(node.Label);
            if (node.Count is { } count)
                label.Append('\n').Append(count.ToString(CultureInfo.InvariantCulture)).Append(count == 1 ? " type" : " types");

            if (node.IsTest && node.Kind == DiagramNodeKinds.Project)
                label.Append("\n[test]");

            text.Append(", shape=box, style=rounded, label=").Append(Quote(label.ToString()));
        }
        else
        {
            text.Append(", shape=plain, label=");
            AppendTypeLabel(text, node);
        }

        if (node.Href is not null)
            text.Append(", URL=").Append(Quote(node.Href));

        text.Append(", tooltip=").Append(Quote(node.FullName ?? node.Label)).Append("];\n");
    }

    private static void AppendTypeLabel(StringBuilder text, DiagramNode node)
    {
        text.Append("<<TABLE BORDER=\"1\" CELLBORDER=\"0\" CELLSPACING=\"0\" CELLPADDING=\"4\">");
        if (Stereotype(node) is { } stereotype)
            text.Append("<TR><TD>«").Append(Html(stereotype)).Append("»</TD></TR>");

        string name = Html(node.Label);
        text.Append("<TR><TD><B>").Append(node.IsAbstract ? "<I>" + name + "</I>" : name).Append("</B></TD></TR>");

        List<DiagramMember> data = [.. node.Members.Where(static member => IsData(member.Kind))];
        List<DiagramMember> operations = [.. node.Members.Where(static member => !IsData(member.Kind))];
        if (data.Count > 0)
            AppendCompartment(text, data, node.HiddenMembers > 0 && operations.Count == 0 ? node.HiddenMembers : 0);

        if (operations.Count > 0)
            AppendCompartment(text, operations, node.HiddenMembers);

        text.Append("</TABLE>>");
    }

    private static void AppendCompartment(StringBuilder text, List<DiagramMember> members, int hidden)
    {
        text.Append("<HR/><TR><TD ALIGN=\"LEFT\" BALIGN=\"LEFT\">");
        foreach (DiagramMember member in members)
        {
            string line = Html(member.Visibility) + " " + Html(member.Text);
            if (member.IsAbstract)
                line = "<I>" + line + "</I>";

            if (member.IsStatic)
                line = "<U>" + line + "</U>";

            text.Append(line).Append("<BR ALIGN=\"LEFT\"/>");
        }

        if (hidden > 0)
            text.Append("… ").Append(hidden.ToString(CultureInfo.InvariantCulture)).Append(" more<BR ALIGN=\"LEFT\"/>");

        text.Append("</TD></TR>");
    }

    private static void AppendEdge(StringBuilder text, int from, int to, DiagramEdge edge, bool labels, int maxTotal)
    {
        text.Append("  n").Append(from.ToString(CultureInfo.InvariantCulture)).Append(" -> n").Append(to.ToString(CultureInfo.InvariantCulture));
        text.Append(" [class=").Append(Quote("edge-" + edge.Style));
        switch (edge.Style)
        {
            case DiagramEdgeStyles.Inherit:
                text.Append(", dir=back, arrowtail=empty, style=solid");
                break;
            case DiagramEdgeStyles.Implement:
                text.Append(", dir=back, arrowtail=empty, style=dashed");
                break;
            case DiagramEdgeStyles.Dependency:
                text.Append(", style=dashed, arrowhead=vee, penwidth=").Append(PenWidth(edge.Total, maxTotal));
                if (labels)
                    text.Append(", label=").Append(Quote(edge.Total.ToString(CultureInfo.InvariantCulture)));

                break;
            default:
                text.Append(", style=solid, arrowhead=vee");
                break;
        }

        string tooltip = edge.Kinds.Count > 0
            ? string.Join(", ", edge.Kinds.Select(static kind => kind.Kind + " " + kind.Count.ToString(CultureInfo.InvariantCulture)))
            : edge.Style;
        text.Append(", tooltip=").Append(Quote(tooltip)).Append("];\n");
    }

    private static string PenWidth(int total, int maxTotal)
    {
        double width = maxTotal <= 1 ? 1 : 1 + (2 * Math.Log(Math.Max(total, 1)) / Math.Log(maxTotal));
        return Math.Clamp(width, 1, 3).ToString("0.##", CultureInfo.InvariantCulture);
    }

    private static string? Stereotype(DiagramNode node) => node.Kind switch
    {
        CodeSymbolKinds.Interface => "interface",
        CodeSymbolKinds.Enum => "enum",
        CodeSymbolKinds.Record => "record",
        CodeSymbolKinds.Struct => "struct",
        CodeSymbolKinds.RecordStruct => "record struct",
        CodeSymbolKinds.Delegate => "delegate",
        DiagramNodeKinds.External => "external",
        CodeSymbolKinds.Class when node.IsAbstract => "abstract",
        _ => null,
    };

    private static bool IsData(string kind) => kind is CodeSymbolKinds.Field or CodeSymbolKinds.Constant or CodeSymbolKinds.Property or CodeSymbolKinds.Event or CodeSymbolKinds.EnumMember;

    private static string Html(string text) =>
        text.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal).Replace("\"", "&quot;", StringComparison.Ordinal);

    private static string Quote(string text) =>
        "\"" + text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal).Replace("\r", string.Empty, StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal) + "\"";
}
