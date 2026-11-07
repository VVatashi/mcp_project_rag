using System.Globalization;
using System.Text;

using ProjectRag.Core.Code.Graph;

namespace ProjectRag.Core.Code.Diagrams;

/// <summary>
/// Writes a diagram as PlantUML text, for an export.
/// </summary>
/// <remarks>The writer is a pure function: the same diagram gives the same text.</remarks>
public static class PlantUmlWriter
{
    /// <summary>
    /// Writes a diagram as PlantUML text.
    /// </summary>
    /// <param name="diagram">The diagram.</param>
    /// <returns>The text with LF line ends.</returns>
    public static string Write(CodeDiagram diagram)
    {
        ArgumentNullException.ThrowIfNull(diagram);
        StringBuilder text = new();
        text.Append("@startuml\n");
        text.Append("hide empty members\n");
        text.Append("skinparam classAttributeIconSize 0\n");
        if (diagram.Title.Length > 0)
            text.Append("title ").Append(Line(diagram.Title)).Append('\n');

        Dictionary<string, int> indexes = new(StringComparer.Ordinal);
        for (int i = 0; i < diagram.Nodes.Count; i++)
            indexes.TryAdd(diagram.Nodes[i].Id, i);

        HashSet<int> placed = [];
        foreach (DiagramCluster cluster in diagram.Clusters)
        {
            text.Append("package ").Append(Quote(cluster.Label)).Append(" {\n");
            for (int i = 0; i < diagram.Nodes.Count; i++)
            {
                if (diagram.Nodes[i].Cluster == cluster.Id && placed.Add(i))
                    AppendNode(text, "  ", i, diagram.Nodes[i]);
            }

            text.Append("}\n");
        }

        for (int i = 0; i < diagram.Nodes.Count; i++)
        {
            if (!placed.Contains(i))
                AppendNode(text, string.Empty, i, diagram.Nodes[i]);
        }

        foreach (DiagramEdge edge in diagram.Edges)
        {
            if (!indexes.TryGetValue(edge.From, out int from) || !indexes.TryGetValue(edge.To, out int to))
                continue;

            string left = Alias(from);
            string right = Alias(to);
            text.Append(edge.Style switch
            {
                DiagramEdgeStyles.Inherit => $"{left} <|-- {right}",
                DiagramEdgeStyles.Implement => $"{left} <|.. {right}",
                DiagramEdgeStyles.Dependency => $"{left} ..> {right} : {edge.Total.ToString(CultureInfo.InvariantCulture)}",
                _ => $"{left} --> {right}",
            }).Append('\n');
        }

        text.Append("@enduml\n");
        return text.ToString();
    }

    private static void AppendNode(StringBuilder text, string indent, int index, DiagramNode node)
    {
        string alias = Alias(index);
        if (node.Kind is DiagramNodeKinds.Project or DiagramNodeKinds.Namespace)
        {
            string label = node.Label;
            if (node.Count is { } count)
                label += "\\n" + count.ToString(CultureInfo.InvariantCulture) + (count == 1 ? " type" : " types");

            text.Append(indent).Append("component \"").Append(Line(label).Replace('"', '\'')).Append("\" as ").Append(alias).Append('\n');
            return;
        }

        string keyword = node.Kind switch
        {
            CodeSymbolKinds.Interface => "interface",
            CodeSymbolKinds.Enum => "enum",
            _ when node.IsAbstract => "abstract class",
            _ => "class",
        };
        string? stereotype = node.Kind switch
        {
            CodeSymbolKinds.Record => "record",
            CodeSymbolKinds.Struct => "struct",
            CodeSymbolKinds.RecordStruct => "record struct",
            CodeSymbolKinds.Delegate => "delegate",
            DiagramNodeKinds.External => "external",
            _ => null,
        };
        text.Append(indent).Append(keyword).Append(' ').Append(Quote(node.Label)).Append(" as ").Append(alias);
        if (stereotype is not null)
            text.Append(" <<").Append(stereotype).Append(">>");

        if (node.Members.Count == 0 && node.HiddenMembers == 0)
        {
            text.Append('\n');
            return;
        }

        text.Append(" {\n");
        bool isEnum = node.Kind == CodeSymbolKinds.Enum;
        foreach (DiagramMember member in node.Members)
        {
            text.Append(indent).Append("  ");
            if (!isEnum)
            {
                text.Append(member.Visibility);
                if (member.IsStatic)
                    text.Append("{static} ");

                if (member.IsAbstract)
                    text.Append("{abstract} ");
            }

            text.Append(Line(member.Text)).Append('\n');
        }

        if (node.HiddenMembers > 0)
            text.Append(indent).Append("  … ").Append(node.HiddenMembers.ToString(CultureInfo.InvariantCulture)).Append(" more\n");

        text.Append(indent).Append("}\n");
    }

    private static string Alias(int index) => "n" + index.ToString(CultureInfo.InvariantCulture);

    private static string Quote(string text) => "\"" + Line(text).Replace('"', '\'') + "\"";

    private static string Line(string text) => text.Replace("\r", string.Empty, StringComparison.Ordinal).Replace('\n', ' ');
}
