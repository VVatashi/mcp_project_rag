using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

using ProjectRag.Core.Code.Diagrams;
using ProjectRag.Core.Code.Graph;

namespace ProjectRag.Core.Tests.Code;

public sealed class DiagramWriterTests
{
    [Fact]
    public void Dot_EscapesTheHtmlLikeLabelsOfGenerics()
    {
        DiagramNode node = Node("a", label: "Dictionary<string, List<int>>", members:
        [
            new DiagramMember("method", "void Add(Dictionary<string, List<int>> map, string \"name\")", "+", false, false),
            new DiagramMember("field", "string Tom & Jerry", "-", false, false),
        ]);

        string dot = DotWriter.Write(Diagram(DiagramViews.Type, [node], []));

        Assert.Contains("<B>Dictionary&lt;string, List&lt;int&gt;&gt;</B>", dot, StringComparison.Ordinal);
        Assert.Contains("+ void Add(Dictionary&lt;string, List&lt;int&gt;&gt; map, string &quot;name&quot;)", dot, StringComparison.Ordinal);
        Assert.Contains("- string Tom &amp; Jerry", dot, StringComparison.Ordinal);
        Assert.DoesNotContain("<B>Dictionary<", dot, StringComparison.Ordinal);
        Assert.DoesNotContain("(Dictionary<", dot, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("interface", false, "«interface»")]
    [InlineData("enum", false, "«enum»")]
    [InlineData("record", false, "«record»")]
    [InlineData("struct", false, "«struct»")]
    [InlineData("record_struct", false, "«record struct»")]
    [InlineData("delegate", false, "«delegate»")]
    [InlineData("external", false, "«external»")]
    [InlineData("class", true, "«abstract»")]
    public void Dot_WritesTheStereotypeOfAKind(string kind, bool isAbstract, string stereotype)
    {
        string dot = DotWriter.Write(Diagram(DiagramViews.Type, [Node("a", kind: kind, isAbstract: isAbstract)], []));

        Assert.Contains("<TR><TD>" + stereotype + "</TD></TR>", dot, StringComparison.Ordinal);
    }

    [Fact]
    public void Dot_WritesNoStereotypeForAClass()
    {
        string dot = DotWriter.Write(Diagram(DiagramViews.Type, [Node("a")], []));

        Assert.DoesNotContain("«", dot, StringComparison.Ordinal);
        Assert.Contains("<TR><TD><B>Shape</B></TD></TR>", dot, StringComparison.Ordinal);
    }

    [Fact]
    public void Dot_StylesAbstractAndStaticTypesAndMembers()
    {
        DiagramNode node = Node("a", isAbstract: true, members:
        [
            new DiagramMember("field", "int Count", "+", true, false),
            new DiagramMember("method", "void Draw()", "#", false, true),
            new DiagramMember("method", "Shape Create()", "+", true, true),
        ]);

        string dot = DotWriter.Write(Diagram(DiagramViews.Type, [node], []));

        Assert.Contains("<B><I>Shape</I></B>", dot, StringComparison.Ordinal);
        Assert.Contains("<U>+ int Count</U>", dot, StringComparison.Ordinal);
        Assert.Contains("<I># void Draw()</I>", dot, StringComparison.Ordinal);
        Assert.Contains("<U><I>+ Shape Create()</I></U>", dot, StringComparison.Ordinal);
        Assert.Equal(2, Regex.Count(dot, "<HR/>"));
        Assert.Contains("</TR><HR/><TR>", dot, StringComparison.Ordinal);
    }

    [Fact]
    public void Dot_SeparatesTheCompartmentsAndLeftAlignsTheLines()
    {
        DiagramNode node = Node("a", members:
        [
            new DiagramMember("field", "int X", "-", false, false),
            new DiagramMember("property", "int Y", "+", false, false),
            new DiagramMember("method", "void Run()", "+", false, false),
        ]);

        string dot = DotWriter.Write(Diagram(DiagramViews.Type, [node], []));

        Assert.Equal(2, Regex.Count(dot, "<HR/>"));
        Assert.Contains("BALIGN=\"LEFT\">- int X<BR ALIGN=\"LEFT\"/>+ int Y<BR ALIGN=\"LEFT\"/></TD>", dot, StringComparison.Ordinal);
        Assert.Contains("BALIGN=\"LEFT\">+ void Run()<BR ALIGN=\"LEFT\"/></TD>", dot, StringComparison.Ordinal);
        Assert.Contains("<TABLE BORDER=\"1\" CELLBORDER=\"0\" CELLSPACING=\"0\" CELLPADDING=\"4\">", dot, StringComparison.Ordinal);
    }

    [Fact]
    public void Dot_WritesTheNameRowsOnlyForANodeWithoutMembers()
    {
        string dot = DotWriter.Write(Diagram(DiagramViews.Type, [Node("a")], []));

        Assert.DoesNotContain("<HR/>", dot, StringComparison.Ordinal);
        Assert.DoesNotContain("BALIGN", dot, StringComparison.Ordinal);
    }

    [Fact]
    public void Dot_ShowsHowManyMembersTheLimitCut()
    {
        DiagramNode node = Node("a", hidden: 3, members: [new DiagramMember("method", "void Run()", "+", false, false)]);

        string dot = DotWriter.Write(Diagram(DiagramViews.Type, [node], []));

        Assert.Contains("+ void Run()<BR ALIGN=\"LEFT\"/>… 3 more<BR ALIGN=\"LEFT\"/></TD>", dot, StringComparison.Ordinal);
    }

    [Fact]
    public void Dot_PutsTheCutLineUnderTheDataWhenThereAreNoOperations()
    {
        DiagramNode node = Node("a", hidden: 2, members: [new DiagramMember("field", "int X", "+", false, false)]);

        string dot = DotWriter.Write(Diagram(DiagramViews.Type, [node], []));

        Assert.Contains("+ int X<BR ALIGN=\"LEFT\"/>… 2 more<BR ALIGN=\"LEFT\"/>", dot, StringComparison.Ordinal);
    }

    [Fact]
    public void Dot_ClassesTheNodes()
    {
        DiagramNode[] nodes =
        [
            Node("a", isFocus: true),
            Node("b", kind: "interface", isTest: true),
            Node("c", kind: "external", label: "List<T>"),
            Node("d", isAbstract: true),
        ];

        string dot = DotWriter.Write(Diagram(DiagramViews.Type, nodes, []));

        Assert.Contains("n0 [class=\"node-class is-focus\"", dot, StringComparison.Ordinal);
        Assert.Contains("n1 [class=\"node-interface is-test\"", dot, StringComparison.Ordinal);
        Assert.Contains("n2 [class=\"node-external is-external\"", dot, StringComparison.Ordinal);
        Assert.Contains("n3 [class=\"node-class is-abstract\"", dot, StringComparison.Ordinal);
    }

    [Fact]
    public void Dot_WritesProjectsAndNamespacesAsRoundedBoxes()
    {
        DiagramNode project = new("p", "project", "Demo.Core.Tests", null, "Demo.Core.Tests", null, true, false, false, 12, "/code/diagram?source=1&view=namespaces&project=Demo.Core.Tests", "folder:tests", [], 0);
        DiagramNode single = new("n", "namespace", "Demo", "Demo", "Demo.Core", null, false, false, false, 1, "/code/diagram?source=1&view=namespace&namespace=Demo&project=Demo.Core", null, [], 0);

        string dot = DotWriter.Write(Diagram(DiagramViews.Projects, [project, single], [], [new DiagramCluster("folder:tests", "tests")]));

        Assert.Contains("class=\"node-project is-test\", shape=box, style=rounded, label=\"Demo.Core.Tests\\n12 types\\n[test]\"", dot, StringComparison.Ordinal);
        Assert.Contains("class=\"node-namespace\", shape=box, style=rounded, label=\"Demo\\n1 type\"", dot, StringComparison.Ordinal);
        Assert.Contains("URL=\"/code/diagram?source=1&view=namespaces&project=Demo.Core.Tests\"", dot, StringComparison.Ordinal);
        Assert.Contains("tooltip=\"Demo.Core.Tests\"", dot, StringComparison.Ordinal);
        Assert.Contains("tooltip=\"Demo\"", dot, StringComparison.Ordinal);
    }

    [Fact]
    public void Dot_WritesTheLinkAndTheFullNameOfATypeButNoLinkForAnExternalType()
    {
        DiagramNode type = Node("a", href: "/code/diagram?source=1&view=type&id=T%3ADemo.Shape", fullName: "Demo.Shape");
        DiagramNode external = Node("b", kind: "external", fullName: "System.Object");

        string dot = DotWriter.Write(Diagram(DiagramViews.Type, [type, external], []));

        Assert.Contains("URL=\"/code/diagram?source=1&view=type&id=T%3ADemo.Shape\", tooltip=\"Demo.Shape\"", dot, StringComparison.Ordinal);
        Assert.Equal(1, Regex.Count(dot, "URL="));
        Assert.Contains("tooltip=\"System.Object\"", dot, StringComparison.Ordinal);
    }

    [Fact]
    public void Dot_WritesTheStyleOfEachKindOfEdge()
    {
        DiagramNode[] nodes = [Node("base"), Node("derived"), Node("iface", kind: "interface"), Node("user"), Node("used")];
        DiagramEdge[] edges =
        [
            new("base", "derived", DiagramEdgeStyles.Inherit, [new UseCount("inherit", 1)], 1),
            new("iface", "derived", DiagramEdgeStyles.Implement, [new UseCount("implement", 1)], 1),
            new("user", "used", DiagramEdgeStyles.Dependency, [new UseCount("call", 3), new UseCount("create", 1)], 4),
            new("user", "base", DiagramEdgeStyles.Reference, [], 1),
        ];

        string dot = DotWriter.Write(Diagram(DiagramViews.Type, nodes, edges));

        Assert.Contains("n0 -> n1 [class=\"edge-inherit\", dir=back, arrowtail=empty, style=solid, tooltip=\"inherit 1\"];", dot, StringComparison.Ordinal);
        Assert.Contains("n2 -> n1 [class=\"edge-implement\", dir=back, arrowtail=empty, style=dashed, tooltip=\"implement 1\"];", dot, StringComparison.Ordinal);
        Assert.Contains("n3 -> n4 [class=\"edge-dependency\", style=dashed, arrowhead=vee, penwidth=", dot, StringComparison.Ordinal);
        Assert.Contains("label=\"4\", tooltip=\"call 3, create 1\"];", dot, StringComparison.Ordinal);
        Assert.Contains("n3 -> n0 [class=\"edge-reference\", style=solid, arrowhead=vee, tooltip=\"reference\"];", dot, StringComparison.Ordinal);
    }

    [Fact]
    public void Dot_ScalesThePenWidthFromOneToThreeByTheLogOfTheTotal()
    {
        DiagramNode[] nodes = [Node("a"), Node("b"), Node("c"), Node("d")];
        DiagramEdge[] edges =
        [
            new("a", "b", DiagramEdgeStyles.Dependency, [new UseCount("call", 1)], 1),
            new("a", "c", DiagramEdgeStyles.Dependency, [new UseCount("call", 100)], 100),
            new("a", "d", DiagramEdgeStyles.Dependency, [new UseCount("call", 10_000)], 10_000),
        ];

        string dot = DotWriter.Write(Diagram(DiagramViews.Type, nodes, edges));

        double[] widths = [.. Regex.Matches(dot, @"penwidth=([0-9.]+)").Select(static match => double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture))];
        Assert.Equal([1.0, 2.0, 3.0], widths);
    }

    [Theory]
    [InlineData(25, true)]
    [InlineData(26, false)]
    public void Dot_LabelsTheDependenciesOnlyInASmallDiagram(int edgeCount, bool labeled)
    {
        DiagramNode[] nodes = [.. Enumerable.Range(0, edgeCount + 1).Select(static i => Node("t" + i))];
        DiagramEdge[] edges = [.. Enumerable.Range(0, edgeCount).Select(static i => new DiagramEdge("t0", "t" + (i + 1), DiagramEdgeStyles.Dependency, [new UseCount("call", 2)], 2))];

        string dot = DotWriter.Write(Diagram(DiagramViews.Type, nodes, edges));

        Assert.Equal(labeled ? edgeCount : 0, Regex.Count(dot, "label=\"2\""));
    }

    [Fact]
    public void Dot_WritesTheClustersWithTheirNodes()
    {
        DiagramNode[] nodes = [Node("a", cluster: "src"), Node("b", cluster: "tests"), Node("c"), Node("d", cluster: "src")];
        DiagramCluster[] clusters = [new("src", "src"), new("tests", "tests \"x\"")];

        string dot = DotWriter.Write(Diagram(DiagramViews.Projects, nodes, [], clusters));

        Assert.Contains("subgraph cluster_0 {\n    label=\"src\";\n    class=\"cluster\";\n    n0 [", dot, StringComparison.Ordinal);
        Assert.Contains("    n3 [", dot, StringComparison.Ordinal);
        Assert.Contains("subgraph cluster_1 {\n    label=\"tests \\\"x\\\"\";", dot, StringComparison.Ordinal);
        Assert.True(dot.IndexOf("    n0 [", StringComparison.Ordinal) < dot.IndexOf("  }\n  subgraph cluster_1", StringComparison.Ordinal));
        Assert.True(dot.IndexOf("  n2 [", StringComparison.Ordinal) > dot.IndexOf("subgraph cluster_1", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("projects", "LR")]
    [InlineData("namespaces", "LR")]
    [InlineData("namespace", "TB")]
    [InlineData("type", "TB")]
    public void Dot_SetsTheDirectionAndTheDefaults(string view, string direction)
    {
        string dot = DotWriter.Write(Diagram(view, [Node("a")], []));

        Assert.StartsWith("digraph code {\n  graph [rankdir=" + direction + ", fontname=\"Helvetica\", fontsize=11, bgcolor=\"transparent\", nodesep=0.3, ranksep=0.5, splines=true, concentrate=false];", dot, StringComparison.Ordinal);
        Assert.Contains("  node [fontname=\"Helvetica\", fontsize=11];", dot, StringComparison.Ordinal);
        Assert.Contains("  edge [fontname=\"Helvetica\", fontsize=11];", dot, StringComparison.Ordinal);
        Assert.DoesNotContain("color", dot.Replace("bgcolor=\"transparent\"", string.Empty, StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Dot_IsDeterministic()
    {
        CodeDiagram first = RichDiagram();
        CodeDiagram second = RichDiagram();

        Assert.Equal(DotWriter.Write(first), DotWriter.Write(first));
        Assert.Equal(DotWriter.Write(first), DotWriter.Write(second));
        Assert.Equal(PlantUmlWriter.Write(first), PlantUmlWriter.Write(second));
        Assert.DoesNotContain('\r', DotWriter.Write(first));
    }

    [Fact]
    public void Dot_HasBalancedBracesAndOnlyKnownNodesInTheEdges()
    {
        string dot = DotWriter.Write(RichDiagram());

        Assert.Equal(dot.Count(static c => c == '{'), dot.Count(static c => c == '}'));
        HashSet<string> declared = [.. Regex.Matches(dot, @"^\s*(n\d+) \[class=", RegexOptions.Multiline).Select(static match => match.Groups[1].Value)];
        Assert.Equal(5, declared.Count);
        MatchCollection edges = Regex.Matches(dot, @"(n\d+) -> (n\d+)");
        Assert.Equal(4, edges.Count);
        foreach (Match edge in edges)
        {
            Assert.Contains(edge.Groups[1].Value, declared);
            Assert.Contains(edge.Groups[2].Value, declared);
        }

        Assert.Equal(Regex.Count(dot, "<TABLE"), Regex.Count(dot, "</TABLE>"));
        Assert.Equal(Regex.Count(dot, "<TR>"), Regex.Count(dot, "</TR>"));
    }

    [Fact]
    public void Dot_SkipsAnEdgeToAnUnknownNode()
    {
        string dot = DotWriter.Write(Diagram(DiagramViews.Type, [Node("a")], [new DiagramEdge("a", "missing", DiagramEdgeStyles.Dependency, [], 1)]));

        Assert.DoesNotContain("->", dot, StringComparison.Ordinal);
    }

    [Fact]
    public void Model_SerializesWithTheNamesOfTheContract()
    {
        CodeDiagram diagram = RichDiagram() with { Dot = "digraph code {}" };

        using JsonDocument json = JsonDocument.Parse(JsonSerializer.Serialize(diagram, JsonSerializerOptions.Web));

        JsonElement root = json.RootElement;
        Assert.Equal(["view", "title", "nodes", "edges", "clusters", "truncated", "notes", "dot"], root.EnumerateObject().Select(static property => property.Name));
        Assert.Equal(
            ["id", "kind", "label", "fullName", "project", "docId", "isTest", "isFocus", "isAbstract", "count", "href", "cluster", "members", "hiddenMembers"],
            root.GetProperty("nodes")[0].EnumerateObject().Select(static property => property.Name));
        Assert.Equal(["kind", "text", "visibility", "isStatic", "isAbstract"], root.GetProperty("nodes")[0].GetProperty("members")[0].EnumerateObject().Select(static property => property.Name));
        Assert.Equal(["from", "to", "style", "kinds", "total"], root.GetProperty("edges")[0].EnumerateObject().Select(static property => property.Name));
        Assert.Equal(["kind", "count"], root.GetProperty("edges")[2].GetProperty("kinds")[0].EnumerateObject().Select(static property => property.Name));
    }

    [Fact]
    public void Dot_PassesGraphvizWhenItIsInstalled()
    {
        string dot = DotWriter.Write(RichDiagram());
        (bool started, int exitCode, string error) = GraphvizRunner.Run(dot);

        Assert.SkipUnless(started, "Graphviz dot is not installed.");
        Assert.True(exitCode == 0, "Graphviz rejected the text: " + error);
    }

    [Fact]
    public void PlantUml_WritesTheSkeletonTypesMembersAndRelations()
    {
        DiagramNode[] nodes =
        [
            Node("shape", isAbstract: true, label: "Shape", members:
            [
                new DiagramMember("field", "int Count", "+", true, false),
                new DiagramMember("method", "void Draw()", "#", false, true),
            ]),
            Node("circle", label: "Circle"),
            Node("iface", kind: "interface", label: "IDrawable<T>"),
            Node("color", kind: "enum", label: "Color", members: [new DiagramMember("enum_member", "Red = 0", "+", false, false)]),
            Node("rec", kind: "record", label: "Point"),
            Node("pt", kind: "struct", label: "Vec"),
            Node("ext", kind: "external", label: "List<T>"),
        ];
        DiagramEdge[] edges =
        [
            new("shape", "circle", DiagramEdgeStyles.Inherit, [new UseCount("inherit", 1)], 1),
            new("iface", "circle", DiagramEdgeStyles.Implement, [new UseCount("implement", 1)], 1),
            new("circle", "ext", DiagramEdgeStyles.Dependency, [new UseCount("call", 5)], 5),
        ];

        string text = PlantUmlWriter.Write(Diagram(DiagramViews.Type, nodes, edges, title: "Neighborhood of Shape"));

        Assert.StartsWith("@startuml\nhide empty members\nskinparam classAttributeIconSize 0\ntitle Neighborhood of Shape\n", text, StringComparison.Ordinal);
        Assert.EndsWith("@enduml\n", text, StringComparison.Ordinal);
        Assert.Contains("abstract class \"Shape\" as n0 {\n  +{static} int Count\n  #{abstract} void Draw()\n}\n", text, StringComparison.Ordinal);
        Assert.Contains("class \"Circle\" as n1\n", text, StringComparison.Ordinal);
        Assert.Contains("interface \"IDrawable<T>\" as n2\n", text, StringComparison.Ordinal);
        Assert.Contains("enum \"Color\" as n3 {\n  Red = 0\n}\n", text, StringComparison.Ordinal);
        Assert.Contains("class \"Point\" as n4 <<record>>\n", text, StringComparison.Ordinal);
        Assert.Contains("class \"Vec\" as n5 <<struct>>\n", text, StringComparison.Ordinal);
        Assert.Contains("class \"List<T>\" as n6 <<external>>\n", text, StringComparison.Ordinal);
        Assert.Contains("n0 <|-- n1\n", text, StringComparison.Ordinal);
        Assert.Contains("n2 <|.. n1\n", text, StringComparison.Ordinal);
        Assert.Contains("n1 ..> n6 : 5\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void PlantUml_WritesProjectsAsComponentsInPackages()
    {
        DiagramNode[] nodes =
        [
            new("a", "project", "Demo.Core", null, "Demo.Core", null, false, false, false, 4, null, "folder:src", [], 0),
            new("b", "project", "Demo.Core.Tests", null, "Demo.Core.Tests", null, true, false, false, 1, null, "folder:tests", [], 0),
            new("c", "project", "Tool", null, "Tool", null, false, false, false, 2, null, null, [], 0),
        ];
        DiagramEdge[] edges = [new("b", "a", DiagramEdgeStyles.Reference, [], 1)];

        string text = PlantUmlWriter.Write(Diagram(DiagramViews.Projects, nodes, edges, [new DiagramCluster("folder:src", "src"), new DiagramCluster("folder:tests", "tests")]));

        Assert.Contains("package \"src\" {\n  component \"Demo.Core\\n4 types\" as n0\n}\n", text, StringComparison.Ordinal);
        Assert.Contains("package \"tests\" {\n  component \"Demo.Core.Tests\\n1 type\" as n1\n}\n", text, StringComparison.Ordinal);
        Assert.Contains("component \"Tool\\n2 types\" as n2\n", text, StringComparison.Ordinal);
        Assert.Contains("n1 --> n0\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void PlantUml_ShowsTheCutMembersAndHasBalancedBraces()
    {
        string text = PlantUmlWriter.Write(RichDiagram());

        Assert.Contains("… 3 more", text, StringComparison.Ordinal);
        Assert.Equal(text.Count(static c => c == '{'), text.Count(static c => c == '}'));
        Assert.Equal(1, Regex.Count(text, "@startuml"));
        Assert.Equal(1, Regex.Count(text, "@enduml"));
    }

    [Theory]
    [InlineData("Registry", "public sealed class Registry<T> where T : class", "Registry<T>")]
    [InlineData("Registry", "public sealed class Registry<in TKey, out TValue>", "Registry<TKey, TValue>")]
    [InlineData("Pair", "public readonly record struct Pair<TFirst, TSecond>(TFirst First, TSecond Second)", "Pair<TFirst, TSecond>")]
    [InlineData("Map", "public class Map<TKey, TValue> : Dictionary<TKey, List<TValue>>", "Map<TKey, TValue>")]
    [InlineData("Handler", "public delegate void Handler<T>(T value)", "Handler<T>")]
    [InlineData("Plain", "public class Plain : Base<Plain>", "Plain")]
    [InlineData("Plain", "public class Plain", "Plain")]
    [InlineData("Plain", "", "Plain")]
    public void TypeLabel_ShowsTheTypeParametersOfTheDeclaredName(string name, string signature, string label) =>
        Assert.Equal(label, CodeDiagramText.TypeLabel(name, signature));

    [Theory]
    [InlineData("method", "public void Update(double time)", "void Update(double time)")]
    [InlineData("method", "public static async Task<int> RunAsync()", "Task<int> RunAsync()")]
    [InlineData("method", "protected internal override string ToString()", "string ToString()")]
    [InlineData("property", "public int Count { get; set; }", "int Count")]
    [InlineData("property", "public required string Name { get; init; }", "string Name")]
    [InlineData("field", "private readonly GPUScene _scene", "GPUScene _scene")]
    [InlineData("constant", "public const int Max = 5", "int Max = 5")]
    [InlineData("constructor", "internal Registry(int capacity)", "Registry(int capacity)")]
    [InlineData("event", "public event EventHandler Changed", "event EventHandler Changed")]
    [InlineData("method", "void   Update(double   time)", "void Update(double time)")]
    [InlineData("enum_member", "Red = 0", "Red = 0")]
    public void MemberText_DropsTheLeadingAccessibilityAndModifiers(string kind, string signature, string text) =>
        Assert.Equal(text, CodeDiagramText.MemberText(kind, signature));

    [Theory]
    [InlineData("System.Collections.Generic.List<T>", "List<T>")]
    [InlineData("System.Object", "Object")]
    [InlineData("Plain", "Plain")]
    [InlineData("System.Collections.Generic.Dictionary<System.String, System.Int32>", "Dictionary<System.String, System.Int32>")]
    public void ExternalLabel_DropsTheNamespace(string target, string label) =>
        Assert.Equal(label, CodeDiagramText.ExternalLabel(target));

    [Theory]
    [InlineData("public", "+")]
    [InlineData("private", "-")]
    [InlineData("protected", "#")]
    [InlineData("protected internal", "#")]
    [InlineData("private protected", "#")]
    [InlineData("internal", "~")]
    public void Visibility_MapsTheAccessibilityToUml(string accessibility, string mark) =>
        Assert.Equal(mark, CodeDiagramText.Visibility(accessibility));

    private static CodeDiagram RichDiagram() => Diagram(
        DiagramViews.Type,
        [
            Node("base", label: "Shape", isAbstract: true, members: [new DiagramMember("method", "void Draw()", "#", false, true)]),
            Node("focus", label: "Registry<T>", isFocus: true, hidden: 3, members:
            [
                new DiagramMember("field", "Dictionary<string, List<int>> _map", "-", false, false),
                new DiagramMember("method", "static Registry<T> Create()", "+", true, false),
            ]),
            Node("iface", kind: "interface", label: "IRegistry<T>"),
            Node("test", label: "RegistryTests", isTest: true),
            Node("ext", kind: "external", label: "List<T>"),
        ],
        [
            new DiagramEdge("base", "focus", DiagramEdgeStyles.Inherit, [new UseCount("inherit", 1)], 1),
            new DiagramEdge("iface", "focus", DiagramEdgeStyles.Implement, [new UseCount("implement", 1)], 1),
            new DiagramEdge("test", "focus", DiagramEdgeStyles.Dependency, [new UseCount("call", 7), new UseCount("create", 2)], 9),
            new DiagramEdge("focus", "ext", DiagramEdgeStyles.Dependency, [new UseCount("type", 4)], 4),
        ]);

    private static CodeDiagram Diagram(string view, IReadOnlyList<DiagramNode> nodes, IReadOnlyList<DiagramEdge> edges, IReadOnlyList<DiagramCluster>? clusters = null, string title = "Test") =>
        new(view, title, nodes, edges, clusters ?? [], false, [], string.Empty);

    private static DiagramNode Node(
        string id,
        string kind = "class",
        string label = "Shape",
        string? fullName = null,
        bool isAbstract = false,
        bool isFocus = false,
        bool isTest = false,
        string? href = null,
        string? cluster = null,
        int hidden = 0,
        IReadOnlyList<DiagramMember>? members = null) =>
        new(id, kind, label, fullName, "Demo.Core", null, isTest, isFocus, isAbstract, null, href, cluster, members ?? [], hidden);
}
