using System.Text.Json;

using ProjectRag.Core.Code.Graph;

namespace ProjectRag.Code.Tests;

public sealed class CodeGraphExtractorTests(SampleFixture fixture) : IClassFixture<SampleFixture>
{
    private const string Circle = "T:Lib.Circle";
    private const string CircleDefaultConstructor = "M:Lib.Circle.#ctor";
    private const string CircleConstructor = "M:Lib.Circle.#ctor(System.Double)";
    private const string CircleArea = "M:Lib.Circle.Area";
    private const string ShapeBaseArea = "M:Lib.ShapeBase.Area";
    private const string ShapeArea = "M:Lib.IShape.Area";

    private CodeGraph Graph
    {
        get
        {
            fixture.RequireRestore();
            return fixture.Graph;
        }
    }

    [Fact]
    public void Projects_HaveReferencesTargetFrameworkAndCounts()
    {
        CodeGraph graph = Graph;

        Assert.Equal(["src/App/App.csproj", "src/Lib/Lib.csproj"], graph.Projects.Select(project => project.Path));
        Assert.Equal(["App", "Lib"], graph.Projects.Select(project => project.Name));
        Assert.All(graph.Projects, project => Assert.Equal("net10.0", project.TargetFramework));
        Assert.All(graph.Projects, project => Assert.False(project.IsTest));
        Assert.All(graph.Projects, project => Assert.Equal(0, project.ErrorCount));
        Assert.Equal([1], graph.Projects[0].ProjectReferences);
        Assert.Empty(graph.Projects[1].ProjectReferences);
        Assert.Equal(2, graph.Projects[0].DocumentCount);
        Assert.Equal(11, graph.Projects[1].DocumentCount);
        Assert.DoesNotContain(graph.Diagnostics, diagnostic => diagnostic.Severity == "error");
        Assert.False(string.IsNullOrEmpty(graph.SdkVersion));
    }

    [Fact]
    public void Solution_ListsItsProjects()
    {
        GraphSolution solution = Assert.Single(Graph.Solutions);

        Assert.Equal("Sample.slnx", solution.Path);
        Assert.Equal([0, 1], solution.Projects);
    }

    [Fact]
    public void LinkedFile_HasTheOwnerInTheProjectOfItsFolder()
    {
        List<GraphFileProject> rows = [.. Graph.FileProjects.Where(row => row.Path == "src/Lib/Shared.cs")];

        Assert.Equal(2, rows.Count);
        Assert.Single(rows, row => row.Project == 1 && row.IsOwner);
        Assert.Single(rows, row => row.Project == 0 && !row.IsOwner);
        Assert.Equal(1, Graph.FileProjects.Count(row => row.Path == "src/Lib/Circle.cs"));
    }

    [Fact]
    public void LinkedFile_GivesOneSymbolForBothCopies()
    {
        CodeGraph graph = Graph;

        GraphSymbol counter = Assert.Single(graph.Symbols, symbol => symbol.DocId == "T:Lib.Counter");
        Assert.Equal(1, counter.Project);
        int counterIndex = graph.Symbols.ToList().IndexOf(counter);
        Assert.Contains(graph.References, reference => reference.Symbol == counterIndex && reference.Path == "src/App/Consumer.cs" && reference.Kind == CodeUseKinds.Create);
        Assert.Equal(1, graph.Declarations.Count(declaration => declaration.Symbol == counterIndex));
    }

    [Fact]
    public void PartialClass_IsOneSymbolWithTwoDeclarations()
    {
        CodeGraph graph = Graph;
        int index = IndexOf(graph, "T:Lib.Scene");

        List<GraphDeclaration> declarations = [.. graph.Declarations.Where(declaration => declaration.Symbol == index)];
        Assert.Equal(["src/Lib/Scene.Extra.cs", "src/Lib/Scene.cs"], declarations.Select(declaration => declaration.Path));
        Assert.Equal(1, graph.Symbols.Count(symbol => symbol.DocId == "T:Lib.Scene"));

        GraphDeclaration main = declarations.Single(declaration => declaration.Path == "src/Lib/Scene.cs");
        Assert.Equal(SampleFixture.Find("src/Lib/Scene.cs", "/// <summary>").Line, main.StartLine);
        Assert.Equal(SampleFixture.Find("src/Lib/Scene.cs", "public partial class").Line, main.NameLine);
        Assert.Equal(SampleFixture.Find("src/Lib/Scene.cs", "public partial class Scene", offset: "public partial class ".Length).Column, main.NameColumn);
        Assert.Equal(SampleCodeBase.Files["src/Lib/Scene.cs"].ReplaceLineEndings("\n").Split('\n').Length, main.EndLine);
        Assert.Contains("partial", graph.Symbols[index].Modifiers);
    }

    [Fact]
    public void Symbols_HaveTheirKinds()
    {
        CodeGraph graph = Graph;
        Dictionary<string, string> expected = new()
        {
            ["T:Lib.Circle"] = CodeSymbolKinds.Class,
            ["T:Lib.IShape"] = CodeSymbolKinds.Interface,
            ["T:Lib.Color"] = CodeSymbolKinds.Enum,
            ["F:Lib.Color.Red"] = CodeSymbolKinds.EnumMember,
            ["T:Lib.Tag"] = CodeSymbolKinds.Record,
            ["T:Lib.Id"] = CodeSymbolKinds.RecordStruct,
            ["T:Lib.Pair"] = CodeSymbolKinds.Struct,
            ["T:Lib.ShapeHandler"] = CodeSymbolKinds.Delegate,
            ["M:Lib.Circle.Area"] = CodeSymbolKinds.Method,
            [CircleDefaultConstructor] = CodeSymbolKinds.Constructor,
            ["M:Lib.Counter.#cctor"] = CodeSymbolKinds.Constructor,
            ["M:Lib.Counter.Finalize"] = CodeSymbolKinds.Destructor,
            ["M:Lib.Circle.op_Addition(Lib.Circle,Lib.Circle)"] = CodeSymbolKinds.Operator,
            ["M:Lib.Pair.op_Implicit(Lib.Pair)~System.Int32"] = CodeSymbolKinds.Conversion,
            ["P:Lib.Circle.Radius"] = CodeSymbolKinds.Property,
            ["P:Lib.Scene.Item(System.Int32)"] = CodeSymbolKinds.Indexer,
            ["F:Lib.Pair.A"] = CodeSymbolKinds.Field,
            ["F:Lib.Circle.Sides"] = CodeSymbolKinds.Constant,
            ["E:Lib.Circle.Changed"] = CodeSymbolKinds.Event,
        };

        foreach ((string docId, string kind) in expected)
            Assert.Equal(kind, graph.Symbols[IndexOf(graph, docId)].Kind);

        Assert.DoesNotContain(graph.Symbols, symbol => symbol.DocId.Contains("get_", StringComparison.Ordinal));
        Assert.DoesNotContain(graph.Symbols, symbol => symbol.DocId.Contains("OnlyWithFlag", StringComparison.Ordinal));
    }

    [Fact]
    public void Symbols_HaveNamesParentsAndNamespaces()
    {
        CodeGraph graph = Graph;
        GraphSymbol constructor = graph.Symbols[IndexOf(graph, CircleConstructor)];
        GraphSymbol registryAdd = graph.Symbols[IndexOf(graph, "M:Lib.Registry`1.Add(`0)")];
        GraphSymbol op = graph.Symbols[IndexOf(graph, "M:Lib.Circle.op_Addition(Lib.Circle,Lib.Circle)")];

        Assert.Equal("Circle", constructor.Name);
        Assert.Equal("Lib.Circle.Circle", constructor.FullName);
        Assert.Equal("Lib", constructor.Namespace);
        Assert.Equal(IndexOf(graph, Circle), constructor.Parent);
        Assert.Equal("Lib.Registry.Add", registryAdd.FullName);
        Assert.Equal("operator +", op.Name);
        Assert.Null(graph.Symbols[IndexOf(graph, Circle)].Parent);
        Assert.Equal(1, constructor.Project);
        Assert.Equal(0, graph.Symbols[IndexOf(graph, "T:App.Consumer")].Project);
    }

    [Fact]
    public void Signature_ShowsAccessibilityModifiersTypesAndParameters()
    {
        CodeGraph graph = Graph;

        Assert.Equal("public override double Area()", graph.Symbols[IndexOf(graph, CircleArea)].Signature);
        Assert.Equal("public abstract double Area()", graph.Symbols[IndexOf(graph, ShapeBaseArea)].Signature);
        Assert.Equal("public Circle(double radius)", graph.Symbols[IndexOf(graph, CircleConstructor)].Signature);
        Assert.Equal("public sealed class Circle", graph.Symbols[IndexOf(graph, Circle)].Signature);
        Assert.Equal("public class Registry<T>", graph.Symbols[IndexOf(graph, "T:Lib.Registry`1")].Signature);
        Assert.Equal("public static double Doubled(this IShape shape)", graph.Symbols[IndexOf(graph, "M:Lib.ShapeExtensions.Doubled(Lib.IShape)")].Signature);
        Assert.Equal("public Circle Make(double radius)", graph.Symbols[IndexOf(graph, "M:Lib.Scene.Make(System.Double)")].Signature);
        Assert.Equal("public const int Sides = 0", graph.Symbols[IndexOf(graph, "F:Lib.Circle.Sides")].Signature);
        Assert.Equal("private readonly List<Circle> _items", graph.Symbols[IndexOf(graph, "F:Lib.Scene._items")].Signature);
        Assert.Equal("public interface IShape", graph.Symbols[IndexOf(graph, "T:Lib.IShape")].Signature);
    }

    [Fact]
    public void Symbols_HaveAccessibilityAndModifiers()
    {
        CodeGraph graph = Graph;

        GraphSymbol area = graph.Symbols[IndexOf(graph, CircleArea)];
        Assert.Equal("public", area.Accessibility);
        Assert.Equal(["override"], area.Modifiers);

        GraphSymbol items = graph.Symbols[IndexOf(graph, "F:Lib.Scene._items")];
        Assert.Equal("private", items.Accessibility);
        Assert.Equal(["readonly"], items.Modifiers);

        Assert.Equal(["sealed"], graph.Symbols[IndexOf(graph, Circle)].Modifiers);
        Assert.Equal(["static"], graph.Symbols[IndexOf(graph, "T:Lib.ShapeExtensions")].Modifiers);
        Assert.Equal(["abstract"], graph.Symbols[IndexOf(graph, ShapeBaseArea)].Modifiers);
        Assert.Equal(["const"], graph.Symbols[IndexOf(graph, "F:Lib.Circle.Sides")].Modifiers);
    }

    [Fact]
    public void Summary_ReplacesTagsAndCollapsesWhitespace()
    {
        CodeGraph graph = Graph;

        Assert.Equal("Holds the Circle items, or null. Use Make.", graph.Symbols[IndexOf(graph, "T:Lib.Scene")].Summary);
        Assert.Equal("A circle with a radius.", graph.Symbols[IndexOf(graph, Circle)].Summary);
        Assert.Null(graph.Symbols[IndexOf(graph, CircleArea)].Summary);
    }

    [Fact]
    public void References_HaveKindsLinesAndColumns()
    {
        CodeGraph graph = Graph;

        AssertReference(graph, "T:Lib.IShape", "src/Lib/ShapeBase.cs", SampleFixture.Find("src/Lib/ShapeBase.cs", "IShape"), 6, CodeUseKinds.Implement, "T:Lib.ShapeBase");
        AssertReference(graph, "T:Lib.ShapeBase", "src/Lib/Circle.cs", SampleFixture.Find("src/Lib/Circle.cs", "ShapeBase"), 9, CodeUseKinds.Inherit, Circle);
        AssertReference(graph, "T:Lib.MarkerAttribute", "src/Lib/Circle.cs", SampleFixture.Find("src/Lib/Circle.cs", "Marker"), 6, CodeUseKinds.Attribute, Circle);
        AssertReference(graph, Circle, "src/Lib/Scene.cs", SampleFixture.Find("src/Lib/Scene.cs", "new Circle()", offset: 4), 6, CodeUseKinds.Create, "F:Lib.Scene._default");
        AssertReference(graph, CircleDefaultConstructor, "src/Lib/Scene.cs", SampleFixture.Find("src/Lib/Scene.cs", "new Circle()", offset: 4), 6, CodeUseKinds.Call, "F:Lib.Scene._default");
        AssertReference(graph, Circle, "src/Lib/Scene.cs", SampleFixture.Find("src/Lib/Scene.cs", "new Circle(radius)", offset: 4), 6, CodeUseKinds.Create, "M:Lib.Scene.Make(System.Double)");
        AssertReference(graph, CircleConstructor, "src/Lib/Scene.cs", SampleFixture.Find("src/Lib/Scene.cs", "new Circle(radius)", offset: 4), 6, CodeUseKinds.Call, "M:Lib.Scene.Make(System.Double)");
        AssertReference(graph, CircleDefaultConstructor, "src/Lib/Circle.cs", SampleFixture.Find("src/Lib/Circle.cs", ": this()", offset: 2), 4, CodeUseKinds.Call, CircleConstructor);
        AssertReference(graph, "M:Lib.Counter.Increment", "src/Lib/Scene.cs", SampleFixture.Find("src/Lib/Scene.cs", "Increment"), 9, CodeUseKinds.Call, "M:Lib.Scene.Make(System.Double)");
        AssertReference(graph, "F:Lib.Scene._counter", "src/Lib/Scene.cs", SampleFixture.Find("src/Lib/Scene.cs", "_counter.Increment"), 8, CodeUseKinds.Member, "M:Lib.Scene.Make(System.Double)");
        AssertReference(graph, Circle, "src/Lib/Scene.cs", SampleFixture.Find("src/Lib/Scene.cs", "public Circle Make", offset: 7), 6, CodeUseKinds.Signature, "M:Lib.Scene.Make(System.Double)");
        AssertReference(graph, Circle, "src/Lib/Scene.cs", SampleFixture.Find("src/Lib/Scene.cs", "Circle circle ="), 6, CodeUseKinds.Type, "M:Lib.Scene.Make(System.Double)");
        AssertReference(graph, Circle, "src/Lib/Scene.cs", SampleFixture.Find("src/Lib/Scene.cs", "List<Circle>", offset: 5), 6, CodeUseKinds.Signature, "F:Lib.Scene._items");
        AssertReference(graph, CircleArea, "src/Lib/Scene.Extra.cs", SampleFixture.Find("src/Lib/Scene.Extra.cs", "item.Area", offset: 5), 4, CodeUseKinds.Call, "M:Lib.Scene.Total");
        AssertReference(graph, "T:Lib.Registry`1", "src/Lib/Scene.Extra.cs", SampleFixture.Find("src/Lib/Scene.Extra.cs", "= new()", offset: 2), 3, CodeUseKinds.Create, "P:Lib.Scene.Registry");
        AssertReference(graph, "M:Lib.ShapeExtensions.Doubled(Lib.IShape)", "src/App/Consumer.cs", SampleFixture.Find("src/App/Consumer.cs", "Doubled"), 7, CodeUseKinds.Call, "M:App.Consumer.Run");
        AssertReference(graph, "P:Lib.Circle.Radius", "src/Lib/Circle.cs", SampleFixture.Find("src/Lib/Circle.cs", "Radius = radius"), 6, CodeUseKinds.Member, CircleConstructor);
    }

    [Fact]
    public void References_KeepOnlyGraphSymbols()
    {
        CodeGraph graph = Graph;

        Assert.DoesNotContain(graph.References, reference => reference.Length == 0);
        Assert.All(graph.References, reference => Assert.InRange(reference.Symbol, 0, graph.Symbols.Count - 1));
        Assert.All(graph.References, reference => Assert.True(fixture.KnownFiles.Contains(reference.Path)));
        Assert.DoesNotContain(graph.References, reference => reference.Line < 1 || reference.Column < 1);
    }

    [Fact]
    public void TypeEdges_AreSummedByKind()
    {
        CodeGraph graph = Graph;
        int scene = IndexOf(graph, "T:Lib.Scene");
        int circle = IndexOf(graph, Circle);

        Assert.Equal(2, Edge(graph, scene, circle, CodeUseKinds.Create));
        Assert.Equal(3, Edge(graph, scene, circle, CodeUseKinds.Call));
        Assert.Equal(5, Edge(graph, scene, circle, CodeUseKinds.Signature));
        Assert.Equal(2, Edge(graph, scene, circle, CodeUseKinds.Type));
        Assert.Equal(1, Edge(graph, circle, IndexOf(graph, "T:Lib.ShapeBase"), CodeUseKinds.Inherit));
        Assert.Equal(1, Edge(graph, IndexOf(graph, "T:Lib.ShapeBase"), IndexOf(graph, "T:Lib.IShape"), CodeUseKinds.Implement));
        Assert.Equal(1, Edge(graph, circle, IndexOf(graph, "T:Lib.MarkerAttribute"), CodeUseKinds.Attribute));
        Assert.Equal(1, Edge(graph, IndexOf(graph, "T:App.Consumer"), IndexOf(graph, "T:Lib.Counter"), CodeUseKinds.Create));
        Assert.DoesNotContain(graph.TypeEdges, edge => edge.From == edge.To);
        Assert.Equal(graph.TypeEdges.OrderBy(edge => edge.From).ThenBy(edge => edge.To).ThenBy(edge => edge.Kind, StringComparer.Ordinal), graph.TypeEdges);
    }

    [Fact]
    public void Edges_AreSummedForEachFile()
    {
        CodeGraph graph = Graph;
        int scene = IndexOf(graph, "T:Lib.Scene");
        int circle = IndexOf(graph, Circle);

        List<GraphTypeEdge> calls = [.. graph.TypeEdges.Where(edge => edge.From == scene && edge.To == circle && edge.Kind == CodeUseKinds.Call)];

        Assert.Equal(["src/Lib/Scene.Extra.cs", "src/Lib/Scene.cs"], calls.Select(edge => edge.Path));
        Assert.Equal([1, 2], calls.Select(edge => edge.Count));
        Assert.All(graph.TypeEdges, edge => Assert.Contains(edge.Path, fixture.KnownFiles));
        Assert.All(graph.ExternalEdges, edge => Assert.Contains(edge.Path, fixture.KnownFiles));
        Assert.All(graph.MemberLinks, link => Assert.Contains(graph.Declarations, declaration => declaration.Symbol == link.Member && declaration.Path == link.Path));
    }

    [Fact]
    public void ExternalEdges_NameTheExternalTypes()
    {
        CodeGraph graph = Graph;
        int registry = IndexOf(graph, "T:Lib.Registry`1");

        List<GraphExternalEdge> list = [.. graph.ExternalEdges.Where(edge => edge.From == registry && edge.Target == "System.Collections.Generic.List<T>")];
        Assert.Equal([CodeUseKinds.Call, CodeUseKinds.Create, CodeUseKinds.Signature], list.Select(edge => edge.Kind));
        Assert.All(list, edge => Assert.Equal("System.Collections", edge.Assembly));
        Assert.All(list, edge => Assert.Equal(1, edge.Count));

        Assert.Contains(graph.ExternalEdges, edge => edge.Target == "System.Math" && edge.Kind == CodeUseKinds.Member);
        Assert.Contains(graph.ExternalEdges, edge => edge.Target == "System.EventHandler" && edge.Kind == CodeUseKinds.Signature);
        Assert.DoesNotContain(graph.ExternalEdges, edge => edge.Target is "System.Double" or "System.Int32" or "System.String" or "System.Object" or "System.Void");
        Assert.DoesNotContain(graph.ExternalEdges, edge => edge.Target.StartsWith("global::", StringComparison.Ordinal));
    }

    [Fact]
    public void MemberLinks_HoldTheOverrideAndTheImplementation()
    {
        CodeGraph graph = Graph;

        Assert.Equal(
            [
                new GraphMemberLink(IndexOf(graph, CircleArea), IndexOf(graph, ShapeBaseArea), "override", "src/Lib/Circle.cs"),
                new GraphMemberLink(IndexOf(graph, ShapeBaseArea), IndexOf(graph, ShapeArea), "implement", "src/Lib/ShapeBase.cs"),
            ],
            graph.MemberLinks.OrderBy(link => link.Member));
    }

    [Fact]
    public async Task KnownFiles_LeaveOutTheFilesThatAreNotListed()
    {
        fixture.RequireRestore();
        HashSet<string> known = [.. fixture.KnownFiles.Where(path => path != "src/Lib/Circle.cs")];

        CodeGraph graph = await SampleFixture.ExtractAsync(fixture.Input(new Dictionary<string, string>(), known));

        Assert.DoesNotContain(graph.Symbols, symbol => symbol.FullName.StartsWith("Lib.Circle", StringComparison.Ordinal));
        Assert.DoesNotContain(graph.Declarations, declaration => declaration.Path == "src/Lib/Circle.cs");
        Assert.DoesNotContain(graph.FileProjects, row => row.Path == "src/Lib/Circle.cs");
        Assert.DoesNotContain(graph.References, reference => reference.Path == "src/Lib/Circle.cs");
        Assert.Contains(graph.Symbols, symbol => symbol.DocId == "T:Lib.Scene");
        Assert.DoesNotContain(graph.Symbols, symbol => symbol.DocId == "T:Lib.Circle");
        Assert.DoesNotContain(graph.References, reference => graph.Symbols[reference.Symbol].DocId == "T:Lib.Circle");
    }

    [Fact]
    public void Properties_ChangeTheViewOfTheBuild()
    {
        fixture.RequireRestore();

        Assert.DoesNotContain(fixture.Graph.Symbols, symbol => symbol.DocId == "M:Lib.Flagged.OnlyWithFlag");
        GraphSymbol flagged = Assert.Single(fixture.FlaggedGraph.Symbols, symbol => symbol.DocId == "M:Lib.Flagged.OnlyWithFlag");
        Assert.Equal("public Circle OnlyWithFlag()", flagged.Signature);

        int circle = IndexOf(fixture.FlaggedGraph, Circle);
        int method = IndexOf(fixture.FlaggedGraph, "M:Lib.Flagged.OnlyWithFlag");
        Assert.Contains(fixture.FlaggedGraph.References, reference => reference.Symbol == circle && reference.Path == "src/Lib/Conditional.cs" && reference.Kind == CodeUseKinds.Create && reference.From == method);
    }

    [Fact]
    public async Task BrokenProjectPath_GivesADiagnosticAndTheOtherProjectLoads()
    {
        fixture.RequireRestore();
        CodeGraphInput input = fixture.Input(new Dictionary<string, string>(), projects: ["src/Missing/Missing.csproj", SampleCodeBase.LibProject]);

        CodeGraph graph = await SampleFixture.ExtractAsync(input);

        GraphProject project = Assert.Single(graph.Projects);
        Assert.Equal(SampleCodeBase.LibProject, project.Path);
        GraphDiagnostic diagnostic = Assert.Single(graph.Diagnostics, item => item.Path == "src/Missing/Missing.csproj");
        Assert.Equal("error", diagnostic.Severity);
        Assert.Contains(graph.Symbols, symbol => symbol.DocId == "T:Lib.Circle");
    }

    [Fact]
    public async Task NoLoadedProject_Throws()
    {
        fixture.RequireRestore();
        CodeGraphInput input = fixture.Input(new Dictionary<string, string>(), projects: ["src/Missing/Missing.csproj"]);

        await Assert.ThrowsAsync<CodeGraphException>(() => SampleFixture.ExtractAsync(input));
    }

    [Fact]
    public async Task BrokenSolution_GivesAWarning()
    {
        fixture.RequireRestore();
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "Broken.slnx"), "<Solution><Project", TestContext.Current.CancellationToken);
        CodeGraphInput input = fixture.Input(new Dictionary<string, string>(), projects: [SampleCodeBase.LibProject], solutions: ["Broken.slnx", SampleCodeBase.Solution]);

        CodeGraph graph = await SampleFixture.ExtractAsync(input);

        Assert.Contains(graph.Diagnostics, diagnostic => diagnostic.Severity == "warning" && diagnostic.Path == "Broken.slnx");
        Assert.Equal([0], graph.Solutions.Single(solution => solution.Path == SampleCodeBase.Solution).Projects);
    }

    [Fact]
    public async Task Output_IsTheSameForAnotherDegreeOfParallelism()
    {
        fixture.RequireRestore();

        CodeGraph again = await SampleFixture.ExtractAsync(fixture.Input(new Dictionary<string, string>()), maxDegreeOfParallelism: 1);

        Assert.Equal(JsonSerializer.Serialize(fixture.Graph), JsonSerializer.Serialize(again));
    }

    [Fact]
    public void Version_IsOne() => Assert.Equal(1, new RoslynCodeGraphExtractor(Microsoft.Extensions.Options.Options.Create(new CodeGraphOptions())).Version);

    private static int IndexOf(CodeGraph graph, string docId)
    {
        int index = -1;
        for (int i = 0; i < graph.Symbols.Count; i++)
        {
            if (graph.Symbols[i].DocId != docId)
                continue;

            Assert.Equal(-1, index);
            index = i;
        }

        Assert.True(index >= 0, $"The graph has no symbol {docId}.");
        return index;
    }

    private static int Edge(CodeGraph graph, int from, int to, string kind) =>
        graph.TypeEdges.Where(edge => edge.From == from && edge.To == to && edge.Kind == kind).Sum(edge => edge.Count);

    private static void AssertReference(CodeGraph graph, string targetDocId, string path, (int Line, int Column) position, int length, string kind, string fromDocId)
    {
        int target = IndexOf(graph, targetDocId);
        int from = IndexOf(graph, fromDocId);
        GraphReference reference = Assert.Single(graph.References, item => item.Symbol == target && item.Path == path && item.Line == position.Line && item.Column == position.Column && item.Kind == kind);
        Assert.Equal(length, reference.Length);
        Assert.Equal(from, reference.From);
    }
}
