using ProjectRag.Core.Code.Graph;

namespace ProjectRag.Core.Code.Diagrams;

/// <summary>
/// A diagram of the code graph as a model of nodes, edges and clusters, with its DOT text.
/// </summary>
/// <param name="View">The view, one of <see cref="DiagramViews"/>.</param>
/// <param name="Title">The title.</param>
/// <param name="Nodes">The nodes in a stable order. The DOT ids are <c>n0</c>, <c>n1</c> and so on in this order.</param>
/// <param name="Edges">The edges in a stable order.</param>
/// <param name="Clusters">The groups of nodes.</param>
/// <param name="Truncated">Tells that the limit of nodes cut the diagram.</param>
/// <param name="Notes">Short sentences that explain the cuts.</param>
/// <param name="Dot">The same diagram as DOT text.</param>
public sealed record CodeDiagram(
    string View,
    string Title,
    IReadOnlyList<DiagramNode> Nodes,
    IReadOnlyList<DiagramEdge> Edges,
    IReadOnlyList<DiagramCluster> Clusters,
    bool Truncated,
    IReadOnlyList<string> Notes,
    string Dot);

/// <summary>
/// A node of a diagram: a project, a namespace, a type or an external type.
/// </summary>
/// <param name="Id">The identity of the node within the diagram.</param>
/// <param name="Kind"><c>project</c>, <c>namespace</c>, <c>external</c> or a kind from <see cref="CodeSymbolKinds"/>.</param>
/// <param name="Label">The name to show. A type shows its type parameters, such as <c>Registry&lt;T&gt;</c>.</param>
/// <param name="FullName">The full name, or <see langword="null"/> for a project.</param>
/// <param name="Project">The name of the assembly, or <see langword="null"/>.</param>
/// <param name="DocId">The documentation comment ID of a type, or <see langword="null"/>.</param>
/// <param name="IsTest">Tells that the node belongs to a test project.</param>
/// <param name="IsFocus">Tells that the node is the type that the diagram centers on.</param>
/// <param name="IsAbstract">Tells that the node is an abstract type.</param>
/// <param name="Count">The number of types in a project or a namespace, or <see langword="null"/>.</param>
/// <param name="Href">The address in the dashboard, or <see langword="null"/> for an external type.</param>
/// <param name="Cluster">The id of the cluster of the node, or <see langword="null"/>.</param>
/// <param name="Members">The members to show.</param>
/// <param name="HiddenMembers">The number of members that the limit left out.</param>
public sealed record DiagramNode(
    string Id,
    string Kind,
    string Label,
    string? FullName,
    string? Project,
    string? DocId,
    bool IsTest,
    bool IsFocus,
    bool IsAbstract,
    int? Count,
    string? Href,
    string? Cluster,
    IReadOnlyList<DiagramMember> Members,
    int HiddenMembers);

/// <summary>
/// A member line of a type node.
/// </summary>
/// <param name="Kind">The kind of the member, one of <see cref="CodeSymbolKinds"/>.</param>
/// <param name="Text">The signature without the accessibility and the modifiers.</param>
/// <param name="Visibility"><c>+</c> public, <c>-</c> private, <c>#</c> protected, <c>~</c> internal.</param>
/// <param name="IsStatic">Tells that the member is static.</param>
/// <param name="IsAbstract">Tells that the member is abstract.</param>
public sealed record DiagramMember(string Kind, string Text, string Visibility, bool IsStatic, bool IsAbstract);

/// <summary>
/// An edge of a diagram.
/// </summary>
/// <param name="From">The id of the node where the edge starts. For an inheritance edge this is the base.</param>
/// <param name="To">The id of the node where the edge ends. For an inheritance edge this is the derived type.</param>
/// <param name="Style">One of <see cref="DiagramEdgeStyles"/>.</param>
/// <param name="Kinds">The uses by kind.</param>
/// <param name="Total">The sum of the counts.</param>
public sealed record DiagramEdge(string From, string To, string Style, IReadOnlyList<UseCount> Kinds, int Total);

/// <summary>
/// A group of nodes.
/// </summary>
/// <param name="Id">The identity of the cluster.</param>
/// <param name="Label">The name to show.</param>
public sealed record DiagramCluster(string Id, string Label);

/// <summary>
/// The views of the diagrams.
/// </summary>
public static class DiagramViews
{
    /// <summary>The projects and their references.</summary>
    public const string Projects = "projects";

    /// <summary>The namespaces and the dependencies between them.</summary>
    public const string Namespaces = "namespaces";

    /// <summary>The types of one namespace.</summary>
    public const string Namespace = "namespace";

    /// <summary>The neighborhood of one type.</summary>
    public const string Type = "type";
}

/// <summary>
/// The styles of the edges of a diagram.
/// </summary>
public static class DiagramEdgeStyles
{
    /// <summary>A derived type inherits a base class.</summary>
    public const string Inherit = "inherit";

    /// <summary>A type implements an interface.</summary>
    public const string Implement = "implement";

    /// <summary>A node uses another node.</summary>
    public const string Dependency = "dependency";

    /// <summary>A project references another project.</summary>
    public const string Reference = "reference";
}

/// <summary>
/// The kind of node that is not a symbol: a project, a namespace or an external type.
/// </summary>
public static class DiagramNodeKinds
{
    /// <summary>A project.</summary>
    public const string Project = "project";

    /// <summary>A namespace.</summary>
    public const string Namespace = "namespace";

    /// <summary>A type of a library outside the code base.</summary>
    public const string External = "external";
}

/// <summary>
/// The members that a type node shows.
/// </summary>
public enum DiagramMembers
{
    /// <summary>No members.</summary>
    None,

    /// <summary>Public and protected members.</summary>
    Public,

    /// <summary>All members.</summary>
    All,
}

/// <summary>
/// The query of the diagram of projects.
/// </summary>
/// <param name="SourceId">The key of the code source.</param>
/// <param name="IncludeTests">Tells to keep the test projects. The default is <see langword="true"/>.</param>
public sealed record ProjectsDiagramRequest(int? SourceId, bool? IncludeTests = null);

/// <summary>
/// The query of the diagram of namespaces.
/// </summary>
/// <param name="SourceId">The key of the code source.</param>
/// <param name="Project">The name of an assembly to keep, or <see langword="null"/> for the whole code base.</param>
/// <param name="IncludeTests">Tells to keep the test projects. The default is <see langword="true"/>.</param>
/// <param name="MinWeight">The smallest total of an edge to keep. The default is 1.</param>
/// <param name="MaxNodes">The most nodes, from 5 to 150. The default is 60.</param>
/// <param name="MaxEdges">The most dependency edges, from 5 to 500. The default is 60.</param>
public sealed record NamespacesDiagramRequest(int? SourceId, string? Project = null, bool? IncludeTests = null, int? MinWeight = null, int? MaxNodes = null, int? MaxEdges = null);

/// <summary>
/// The query of the diagram of one namespace.
/// </summary>
/// <param name="SourceId">The key of the code source.</param>
/// <param name="Namespace">The namespace. An empty text names the global namespace.</param>
/// <param name="Project">The name of an assembly to keep, or <see langword="null"/>.</param>
/// <param name="IncludeExternal">Tells to add the external base types and interfaces. The default is <see langword="false"/>.</param>
/// <param name="Members"><c>none</c>, <c>public</c> or <c>all</c>. The default is <c>none</c>.</param>
/// <param name="MaxNodes">The most type nodes, from 5 to 150. The default is 40.</param>
/// <param name="MaxEdges">The most dependency edges, from 5 to 500. The default is 60. The inherit and implement edges are always kept.</param>
public sealed record NamespaceDiagramRequest(int? SourceId, string? Namespace, string? Project = null, bool? IncludeExternal = null, string? Members = null, int? MaxNodes = null, int? MaxEdges = null);

/// <summary>
/// The query of the diagram of the neighborhood of one type.
/// </summary>
/// <param name="SourceId">The key of the code source.</param>
/// <param name="DocId">The documentation comment ID of the type, or of a member of it.</param>
/// <param name="Project">The name of the assembly, or <see langword="null"/>.</param>
/// <param name="Depth">The depth of the walk, from 1 to 3. The default is 1.</param>
/// <param name="Direction"><c>uses</c>, <c>usedBy</c> or <c>both</c>. The default is <c>both</c>.</param>
/// <param name="Kinds">A comma list of the kinds of uses to follow, or an empty text for all.</param>
/// <param name="IncludeTests">Tells to keep the types of test projects. The default is <see langword="true"/>.</param>
/// <param name="IncludeExternal">Tells to add the external types that the focus uses. The default is <see langword="false"/>.</param>
/// <param name="Members"><c>none</c>, <c>public</c> or <c>all</c> for the focus. The default is <c>public</c>.</param>
/// <param name="NeighborMembers">Tells that the other nodes show members too. The default is <see langword="false"/>.</param>
/// <param name="MaxNodes">The most nodes, from 5 to 150. The default is 40.</param>
/// <param name="NeighborEdges">Tells to keep the dependency edges between the neighbors. The default is <see langword="false"/>: only the edges of the focus and the inherit and implement edges stay.</param>
public sealed record TypeDiagramRequest(
    int? SourceId,
    string? DocId,
    string? Project = null,
    int? Depth = null,
    string? Direction = null,
    string? Kinds = null,
    bool? IncludeTests = null,
    bool? IncludeExternal = null,
    string? Members = null,
    bool? NeighborMembers = null,
    int? MaxNodes = null,
    bool? NeighborEdges = null);

/// <summary>
/// The source, the symbol or the project of a diagram does not exist, or the source has no graph.
/// </summary>
/// <param name="message">The message.</param>
public sealed class CodeDiagramNotFoundException(string message) : Exception(message);
