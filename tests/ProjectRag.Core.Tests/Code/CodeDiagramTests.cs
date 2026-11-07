using Dapper;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Npgsql;

using ProjectRag.Core.Code;
using ProjectRag.Core.Code.Diagrams;
using ProjectRag.Core.Code.Graph;
using ProjectRag.Core.Embeddings;
using ProjectRag.Core.Indexing;
using ProjectRag.Core.Reranking;
using ProjectRag.Core.Sources;
using ProjectRag.Core.Storage;

namespace ProjectRag.Core.Tests.Code;

/// <summary>
/// Builds the diagrams from the graph that the fake extractor returns, against a real PostgreSQL.
/// </summary>
/// <remarks>
/// The tests use the variable PROJECTRAG_TEST_DATABASE with a database of their own with the suffix <c>_diagram</c>. Without the variable they skip themselves.
/// </remarks>
public sealed class CodeDiagramTests : IAsyncLifetime
{
    private const string VariableName = "PROJECTRAG_TEST_DATABASE";

    private readonly string _root = Directory.CreateTempSubdirectory("project-rag-diagram-").FullName;
    private readonly FakeCodeGraphExtractor _extractor = new();
    private ServiceProvider? _services;

    public async ValueTask InitializeAsync()
    {
        string? connectionString = Environment.GetEnvironmentVariable(VariableName);
        if (string.IsNullOrEmpty(connectionString))
            return;

        NpgsqlConnectionStringBuilder builder = new(connectionString);
        string database = (builder.Database ?? throw new InvalidOperationException($"{VariableName} names no database.")) + "_diagram";
        builder.Database = "postgres";
        await using (NpgsqlConnection admin = new(builder.ConnectionString))
        {
            await admin.ExecuteAsync($"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE)");
            await admin.ExecuteAsync($"CREATE DATABASE \"{database}\"");
        }

        builder.Database = database;
        string diagramConnection = builder.ConnectionString;
        NpgsqlConnection.ClearAllPools();
        DatabaseMigrator.MigrateUp(diagramConnection, NullLoggerFactory.Instance);
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:ProjectRag"] = diagramConnection,
            ["Embedding:Model"] = "fake",
            ["Rerank:Candidates"] = "10",
        }).Build();

        ServiceCollection services = new();
        services.AddLogging();
        services.AddKnowledgeBase(configuration);
        services.AddSingleton<IPageClient, NoPages>();
        services.AddSingleton<IEmbeddingClient, FakeEmbeddings>();
        services.AddSingleton<IRerankClient, FakeRerank>();
        services.AddSingleton<ICodeGraphExtractor>(_extractor);
        _services = services.BuildServiceProvider();
    }

    public async ValueTask DisposeAsync()
    {
        if (_services is not null)
            await _services.DisposeAsync();

        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Projects_ListTheProjectsTheirReferencesAndTheirTopFolders()
    {
        (CodeDiagramService diagrams, int sourceId) = await IndexAsync(SampleGraph.Create());

        CodeDiagram diagram = await diagrams.GetProjectsAsync(new ProjectsDiagramRequest(sourceId), TestContext.Current.CancellationToken);

        Assert.Equal("projects", diagram.View);
        Assert.Equal(["Demo.Core", "Demo.Core.Tests"], diagram.Nodes.Select(static node => node.Id));
        Assert.All(diagram.Nodes, static node => Assert.Equal("project", node.Kind));
        Assert.Equal([4, 1], diagram.Nodes.Select(static node => node.Count));
        Assert.Equal([false, true], diagram.Nodes.Select(static node => node.IsTest));
        Assert.Equal(["folder:src", "folder:tests"], diagram.Nodes.Select(static node => node.Cluster));
        Assert.Equal([("folder:src", "src"), ("folder:tests", "tests")], diagram.Clusters.Select(static cluster => (cluster.Id, cluster.Label)));
        Assert.Equal($"/code/diagram?source={sourceId}&view=namespaces&project=Demo.Core", diagram.Nodes[0].Href);
        DiagramEdge edge = Assert.Single(diagram.Edges);
        Assert.Equal(("Demo.Core.Tests", "Demo.Core", DiagramEdgeStyles.Reference, 1), (edge.From, edge.To, edge.Style, edge.Total));
        Assert.False(diagram.Truncated);
        Assert.Empty(diagram.Notes);
        Assert.Equal(DotWriter.Write(diagram), diagram.Dot);
        Assert.Contains("rankdir=LR", diagram.Dot, StringComparison.Ordinal);

        CodeDiagram withoutTests = await diagrams.GetProjectsAsync(new ProjectsDiagramRequest(sourceId, false), TestContext.Current.CancellationToken);
        Assert.Equal(["Demo.Core"], withoutTests.Nodes.Select(static node => node.Id));
        Assert.Empty(withoutTests.Edges);
        Assert.Equal(["src"], withoutTests.Clusters.Select(static cluster => cluster.Label));
    }

    [Fact]
    public async Task Namespaces_SumTheTypeEdgesBetweenNamespacesAndGroupThemByProject()
    {
        (CodeDiagramService diagrams, int sourceId) = await IndexAsync(SampleGraph.Create());
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        CodeDiagram all = await diagrams.GetNamespacesAsync(new NamespacesDiagramRequest(sourceId), cancellationToken);

        Assert.Equal("namespaces", all.View);
        Assert.Equal(["Demo.Core::Demo", "Demo.Core.Tests::Demo"], all.Nodes.Select(static node => node.Id));
        Assert.All(all.Nodes, static node => Assert.Equal("namespace", node.Kind));
        Assert.Equal([4, 1], all.Nodes.Select(static node => node.Count));
        Assert.Equal(["project:Demo.Core", "project:Demo.Core.Tests"], all.Nodes.Select(static node => node.Cluster));
        Assert.Equal(["Demo.Core", "Demo.Core.Tests"], all.Clusters.Select(static cluster => cluster.Label));
        Assert.Equal($"/code/diagram?source={sourceId}&view=namespace&namespace=Demo&project=Demo.Core", all.Nodes[0].Href);
        DiagramEdge edge = Assert.Single(all.Edges);
        Assert.Equal(("Demo.Core.Tests::Demo", "Demo.Core::Demo", DiagramEdgeStyles.Dependency, 2), (edge.From, edge.To, edge.Style, edge.Total));
        Assert.Equal([("create", 1), ("call", 1)], edge.Kinds.Select(static kind => (kind.Kind, kind.Count)));
        Assert.Equal(all.Dot, DotWriter.Write(all));

        CodeDiagram inProject = await diagrams.GetNamespacesAsync(new NamespacesDiagramRequest(sourceId, "Demo.Core"), cancellationToken);
        Assert.Equal(["Demo"], inProject.Nodes.Select(static node => node.Id));
        Assert.Empty(inProject.Edges);
        Assert.Empty(inProject.Clusters);
        Assert.Null(inProject.Nodes[0].Cluster);

        Assert.Empty((await diagrams.GetNamespacesAsync(new NamespacesDiagramRequest(sourceId, MinWeight: 3), cancellationToken)).Edges);
        CodeDiagram withoutTests = await diagrams.GetNamespacesAsync(new NamespacesDiagramRequest(sourceId, IncludeTests: false), cancellationToken);
        Assert.Equal(["Demo.Core::Demo"], withoutTests.Nodes.Select(static node => node.Id));
        Assert.Empty(withoutTests.Edges);
    }

    [Fact]
    public async Task Namespaces_KeepTheNodesWithTheStrongestEdgesWhenThereAreTooMany()
    {
        (CodeDiagramService diagrams, int sourceId) = await IndexAsync(HubGraph.Create());
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        CodeDiagram all = await diagrams.GetNamespacesAsync(new NamespacesDiagramRequest(sourceId), cancellationToken);
        Assert.Equal(12, all.Nodes.Count);
        Assert.Equal(10, all.Edges.Count);
        Assert.False(all.Truncated);

        CodeDiagram cut = await diagrams.GetNamespacesAsync(new NamespacesDiagramRequest(sourceId, MaxNodes: 5), cancellationToken);
        Assert.True(cut.Truncated);
        Assert.Equal(["Demo.Core::Hub", "Demo.Core::N4", "Demo.Core::N5", "Demo.Core::N6", "Demo.Core::N7"], cut.Nodes.Select(static node => node.Id));
        Assert.Equal(["Demo.Core::N4", "Demo.Core::N5", "Demo.Core::N6", "Demo.Core::N7"], cut.Edges.Select(static edge => edge.From));
        Assert.All(cut.Edges, static edge => Assert.Equal("Demo.Core::Hub", edge.To));
        Assert.Equal(["Demo.Core"], cut.Clusters.Select(static cluster => cluster.Label));
        Assert.Contains("7 namespaces", Assert.Single(cut.Notes), StringComparison.Ordinal);

        CodeDiagram heavy = await diagrams.GetNamespacesAsync(new NamespacesDiagramRequest(sourceId, MinWeight: 6), cancellationToken);
        Assert.Equal(12, heavy.Nodes.Count);
        Assert.Equal([6, 7, 8], heavy.Edges.Select(static edge => edge.Total));

        CodeDiagram withoutTests = await diagrams.GetNamespacesAsync(new NamespacesDiagramRequest(sourceId, IncludeTests: false), cancellationToken);
        Assert.Equal(11, withoutTests.Nodes.Count);
        Assert.Equal(9, withoutTests.Edges.Count);

        CodeDiagram inProject = await diagrams.GetNamespacesAsync(new NamespacesDiagramRequest(sourceId, "Demo.Core"), cancellationToken);
        Assert.Equal(11, inProject.Nodes.Count);
        Assert.Equal("Hub", inProject.Nodes.Single(static node => node.Label == "Hub").Id);
        Assert.Empty(inProject.Clusters);
        Assert.Equal(9, inProject.Edges.Count);
    }

    [Fact]
    public async Task Namespace_DrawsTheTypesOfANamespaceWithTheirMembersAndEdgeStyles()
    {
        (CodeDiagramService diagrams, int sourceId) = await IndexAsync(SampleGraph.Create());
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        CodeDiagram diagram = await diagrams.GetNamespaceAsync(new NamespaceDiagramRequest(sourceId, "Demo"), cancellationToken);

        Assert.Equal("namespace", diagram.View);
        Assert.Equal(["IScene", "Scene", "SceneHost", "SceneSynchronizer", "SceneTests"], diagram.Nodes.Select(static node => node.Label));
        Assert.Equal(["interface", "class", "class", "class", "class"], diagram.Nodes.Select(static node => node.Kind));
        Assert.Equal([false, true, false, false, false], diagram.Nodes.Select(static node => node.IsAbstract));
        Assert.Equal([false, false, false, false, true], diagram.Nodes.Select(static node => node.IsTest));
        Assert.All(diagram.Nodes, static node => Assert.Empty(node.Members));
        Assert.Empty(diagram.Clusters);
        Assert.Equal(
            [
                ("Demo.Core|T:Demo.IScene", "Demo.Core|T:Demo.Scene", DiagramEdgeStyles.Implement, 1),
                ("Demo.Core|T:Demo.Scene", "Demo.Core|T:Demo.SceneHost", DiagramEdgeStyles.Inherit, 1),
                ("Demo.Core|T:Demo.SceneHost", "Demo.Core|T:Demo.SceneSynchronizer", DiagramEdgeStyles.Dependency, 1),
                ("Demo.Core|T:Demo.SceneSynchronizer", "Demo.Core|T:Demo.SceneHost", DiagramEdgeStyles.Dependency, 4),
                ("Demo.Core.Tests|T:Demo.SceneTests", "Demo.Core|T:Demo.SceneHost", DiagramEdgeStyles.Dependency, 2),
            ],
            diagram.Edges.Select(static edge => (edge.From, edge.To, edge.Style, edge.Total)));
        Assert.Equal([("signature", 1), ("create", 1), ("call", 2)], diagram.Edges[3].Kinds.Select(static kind => (kind.Kind, kind.Count)));
        Assert.Equal($"/code/diagram?source={sourceId}&view=type&id=T%3ADemo.SceneHost&project=Demo.Core", diagram.Nodes[2].Href);
        Assert.Equal("T:Demo.SceneHost", diagram.Nodes[2].DocId);
        Assert.Equal("Demo.SceneHost", diagram.Nodes[2].FullName);
        Assert.Equal(DotWriter.Write(diagram), diagram.Dot);
        Assert.Contains("rankdir=TB", diagram.Dot, StringComparison.Ordinal);

        CodeDiagram withMembers = await diagrams.GetNamespaceAsync(new NamespaceDiagramRequest(sourceId, "Demo", Members: "all"), cancellationToken);
        Assert.Equal(["void Sync()", "void Update(double time)", "void Reset()", "void Reset(bool all)"], withMembers.Nodes[2].Members.Select(static member => member.Text));
        Assert.All(withMembers.Nodes[2].Members, static member => Assert.Equal(("method", "+"), (member.Kind, member.Visibility)));
        Assert.Equal(["void Update(double time)"], withMembers.Nodes[0].Members.Select(static member => member.Text));

        CodeDiagram tests = await diagrams.GetNamespaceAsync(new NamespaceDiagramRequest(sourceId, "Demo", "Demo.Core.Tests"), cancellationToken);
        Assert.Equal(["SceneTests"], tests.Nodes.Select(static node => node.Label));
        Assert.Empty(tests.Edges);
    }

    [Fact]
    public async Task Namespace_AddsOnlyTheExternalBasesAndKeepsTheMostEdgesWhenThereAreTooMany()
    {
        (CodeDiagramService diagrams, int sourceId) = await IndexAsync(HubGraph.Create());
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        CodeDiagram plain = await diagrams.GetNamespaceAsync(new NamespaceDiagramRequest(sourceId, "Hub"), cancellationToken);
        Assert.Equal(["Base", "Child", "Hub", "Registry<T>", "TestUser"], plain.Nodes.Select(static node => node.Label));
        Assert.DoesNotContain(plain.Nodes, static node => node.Kind == "external");
        Assert.Equal(
            [("Demo.Core|T:Hub.Base", "Demo.Core|T:Hub.Hub", "inherit"), ("Demo.Core|T:Hub.Hub", "Demo.Core|T:Hub.Child", "inherit"), ("Demo.Core.Tests|T:Hub.TestUser", "Demo.Core|T:Hub.Hub", "dependency")],
            plain.Edges.Select(static edge => (edge.From, edge.To, edge.Style)));

        CodeDiagram external = await diagrams.GetNamespaceAsync(new NamespaceDiagramRequest(sourceId, "Hub", IncludeExternal: true), cancellationToken);
        DiagramNode disposable = Assert.Single(external.Nodes, static node => node.Kind == "external");
        Assert.Equal(("IDisposable", "System.IDisposable", "System.Runtime", null), (disposable.Label, disposable.FullName, disposable.Project, disposable.Href));
        Assert.Equal(6, external.Nodes.Count);
        DiagramEdge implement = Assert.Single(external.Edges, static edge => edge.Style == DiagramEdgeStyles.Implement);
        Assert.Equal((disposable.Id, "Demo.Core|T:Hub.Hub"), (implement.From, implement.To));
        Assert.Contains("is-external", external.Dot, StringComparison.Ordinal);

        CodeDiagram cut = await diagrams.GetNamespaceAsync(new NamespaceDiagramRequest(sourceId, "Many", MaxNodes: 5), cancellationToken);
        Assert.True(cut.Truncated);
        Assert.Equal(["U0", "U4", "U5", "U6", "U7"], cut.Nodes.Select(static node => node.Label));
        Assert.Equal(4, cut.Edges.Count);
        Assert.Contains("3 types", Assert.Single(cut.Notes), StringComparison.Ordinal);

        CodeDiagram inProject = await diagrams.GetNamespaceAsync(new NamespaceDiagramRequest(sourceId, "Hub", "Demo.Core"), cancellationToken);
        Assert.Equal(["Base", "Child", "Hub", "Registry<T>"], inProject.Nodes.Select(static node => node.Label));
    }

    [Fact]
    public async Task Type_ListsTheBasesFirstThenTheNeighborsByTheSumOfTheirEdges()
    {
        (CodeDiagramService diagrams, int sourceId) = await IndexAsync(SampleGraph.Create());
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        CodeDiagram diagram = await diagrams.GetTypeAsync(new TypeDiagramRequest(sourceId, "T:Demo.SceneHost"), cancellationToken);

        Assert.Equal("type", diagram.View);
        Assert.Equal(["SceneHost", "Scene", "IScene", "SceneSynchronizer", "SceneTests"], diagram.Nodes.Select(static node => node.Label));
        Assert.Equal([true, false, false, false, false], diagram.Nodes.Select(static node => node.IsFocus));
        Assert.Equal(["Demo.Core|T:Demo.SceneHost", "Demo.Core|T:Demo.Scene", "Demo.Core|T:Demo.IScene", "Demo.Core|T:Demo.SceneSynchronizer", "Demo.Core.Tests|T:Demo.SceneTests"], diagram.Nodes.Select(static node => node.Id));
        Assert.Equal(["inherit", "implement", "dependency", "dependency", "dependency"], diagram.Edges.OrderBy(static edge => edge.Style == "inherit" ? 0 : edge.Style == "implement" ? 1 : 2).Select(static edge => edge.Style));
        Assert.Equal(5, diagram.Edges.Count);
        Assert.False(diagram.Truncated);
        Assert.Empty(diagram.Notes);
        Assert.Equal(["void Sync()", "void Update(double time)", "void Reset()", "void Reset(bool all)"], diagram.Nodes[0].Members.Select(static member => member.Text));
        Assert.All(diagram.Nodes.Skip(1), static node => Assert.Empty(node.Members));
        Assert.Equal($"/code/diagram?source={sourceId}&view=type&id=T%3ADemo.SceneHost&project=Demo.Core", diagram.Nodes[0].Href);
        Assert.Equal(DotWriter.Write(diagram), diagram.Dot);
        Assert.Contains("is-focus", diagram.Dot, StringComparison.Ordinal);

        CodeDiagram withoutTests = await diagrams.GetTypeAsync(new TypeDiagramRequest(sourceId, "T:Demo.SceneHost", IncludeTests: false), cancellationToken);
        Assert.DoesNotContain(withoutTests.Nodes, static node => node.IsTest);
        Assert.Equal(4, withoutTests.Nodes.Count);

        CodeDiagram uses = await diagrams.GetTypeAsync(new TypeDiagramRequest(sourceId, "T:Demo.SceneHost", Direction: "uses"), cancellationToken);
        Assert.Equal(["SceneHost", "Scene", "IScene", "SceneSynchronizer"], uses.Nodes.Select(static node => node.Label));

        CodeDiagram usedBy = await diagrams.GetTypeAsync(new TypeDiagramRequest(sourceId, "T:Demo.SceneHost", Direction: "usedBy"), cancellationToken);
        Assert.Equal(["SceneHost", "SceneSynchronizer", "SceneTests"], usedBy.Nodes.Select(static node => node.Label));
    }

    [Fact]
    public async Task Type_ResolvesAMemberToItsTypeAndTellsSo()
    {
        (CodeDiagramService diagrams, int sourceId) = await IndexAsync(SampleGraph.Create());

        CodeDiagram diagram = await diagrams.GetTypeAsync(new TypeDiagramRequest(sourceId, "M:Demo.SceneHost.Update(System.Double)"), TestContext.Current.CancellationToken);

        DiagramNode focus = Assert.Single(diagram.Nodes, static node => node.IsFocus);
        Assert.Equal("T:Demo.SceneHost", focus.DocId);
        Assert.Contains("M:Demo.SceneHost.Update(System.Double) is a member", Assert.Single(diagram.Notes), StringComparison.Ordinal);
        Assert.False(diagram.Truncated);
    }

    [Fact]
    public async Task Type_FollowsTheLimitsOfDepthDirectionKindsAndNodes()
    {
        (CodeDiagramService diagrams, int sourceId) = await IndexAsync(HubGraph.Create());
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        string[] spokes = ["S7", "S6", "S5", "TestUser", "S4", "S3", "S2", "S1", "S0"];

        CodeDiagram all = await diagrams.GetTypeAsync(new TypeDiagramRequest(sourceId, "T:Hub.Hub"), cancellationToken);
        Assert.Equal(["Hub", "Base", "Child", .. spokes], all.Nodes.Select(static node => node.Label));
        Assert.Equal(11, all.Edges.Count);
        Assert.Equal(["inherit", "inherit"], all.Edges.Where(static edge => edge.Style == "inherit").Select(static edge => edge.Style));
        Assert.True(all.Nodes.Single(static node => node.Label == "TestUser").IsTest);
        Assert.False(all.Truncated);

        CodeDiagram cut = await diagrams.GetTypeAsync(new TypeDiagramRequest(sourceId, "T:Hub.Hub", MaxNodes: 5), cancellationToken);
        Assert.Equal(["Hub", "Base", "Child", "S7", "S6"], cut.Nodes.Select(static node => node.Label));
        Assert.True(cut.Truncated);
        Assert.Equal("7 more neighbors are not shown. The limit is 5 nodes.", Assert.Single(cut.Notes));

        CodeDiagram withoutTests = await diagrams.GetTypeAsync(new TypeDiagramRequest(sourceId, "T:Hub.Hub", IncludeTests: false), cancellationToken);
        Assert.DoesNotContain(withoutTests.Nodes, static node => node.Label == "TestUser");
        Assert.Equal(11, withoutTests.Nodes.Count);

        CodeDiagram uses = await diagrams.GetTypeAsync(new TypeDiagramRequest(sourceId, "T:Hub.Hub", Direction: "uses"), cancellationToken);
        Assert.Equal(["Hub", "Base"], uses.Nodes.Select(static node => node.Label));
        CodeDiagram usedBy = await diagrams.GetTypeAsync(new TypeDiagramRequest(sourceId, "T:Hub.Hub", Direction: "usedBy"), cancellationToken);
        Assert.Equal(["Hub", "Child", .. spokes], usedBy.Nodes.Select(static node => node.Label));

        CodeDiagram calls = await diagrams.GetTypeAsync(new TypeDiagramRequest(sourceId, "T:Hub.Hub", Kinds: "call"), cancellationToken);
        Assert.Equal(["Hub", .. spokes], calls.Nodes.Select(static node => node.Label));
        Assert.All(calls.Edges, static edge => Assert.Equal(("dependency", "call"), (edge.Style, Assert.Single(edge.Kinds).Kind)));

        CodeDiagram deeper = await diagrams.GetTypeAsync(new TypeDiagramRequest(sourceId, "T:Hub.Hub", Depth: 2), cancellationToken);
        Assert.Equal(["Hub", "Base", "Child", .. spokes, "Far"], deeper.Nodes.Select(static node => node.Label));
        Assert.Contains(deeper.Edges, static edge => edge.From == "Demo.Core|T:Far.Far" && edge.To == "Demo.Core|T:N0.S0");
        CodeDiagram withNeighborEdges = await diagrams.GetTypeAsync(new TypeDiagramRequest(sourceId, "T:Hub.Hub", Depth: 2, NeighborEdges: true), cancellationToken);
        Assert.Contains(withNeighborEdges.Edges, static edge => edge.From == "Demo.Core|T:Far.Far" && edge.To == "Demo.Core|T:N0.S0");
        Assert.Equal(13, (await diagrams.GetTypeAsync(new TypeDiagramRequest(sourceId, "T:Hub.Hub", Depth: 3), cancellationToken)).Nodes.Count);
    }

    [Fact]
    public async Task Type_KeepsTheEdgesBetweenNeighborsOnlyWhenAsked()
    {
        (CodeDiagramService diagrams, int sourceId) = await IndexAsync(HubGraph.Create());
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        CodeDiagram plain = await diagrams.GetTypeAsync(new TypeDiagramRequest(sourceId, "T:Hub.Hub", Depth: 2), cancellationToken);
        CodeDiagram full = await diagrams.GetTypeAsync(new TypeDiagramRequest(sourceId, "T:Hub.Hub", Depth: 2, NeighborEdges: true), cancellationToken);

        // Without neighbor edges, a dependency touches the focus or links a node of the second level to the node that found it.
        string focus = "Demo.Core|T:Hub.Hub";
        Assert.All(plain.Edges, edge => Assert.True(edge.Style != DiagramEdgeStyles.Dependency || edge.From == focus || edge.To == focus || edge.From == "Demo.Core|T:Far.Far"));
        Assert.Single(plain.Edges, static edge => edge.From == "Demo.Core|T:Far.Far");
        Assert.True(full.Edges.Count >= plain.Edges.Count);
        Assert.Equal(DotWriter.Write(plain), plain.Dot);
        Assert.DoesNotContain("neighborEdges", plain.Nodes[0].Href, StringComparison.Ordinal);
        Assert.EndsWith("&neighborEdges=1", full.Nodes[0].Href, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Namespaces_KeepTheHeaviestEdgesUpToMaxEdgesAndSayHowManyAreHidden()
    {
        (CodeDiagramService diagrams, int sourceId) = await IndexAsync(HubGraph.Create());
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        CodeDiagram all = await diagrams.GetNamespacesAsync(new NamespacesDiagramRequest(sourceId), cancellationToken);
        CodeDiagram cut = await diagrams.GetNamespacesAsync(new NamespacesDiagramRequest(sourceId, MaxEdges: 5), cancellationToken);

        Assert.Equal(12, cut.Nodes.Count);
        Assert.Equal(5, cut.Edges.Count);
        Assert.True(cut.Truncated);
        Assert.Equal("5 lighter edges are hidden. Raise maxEdges or minWeight.", Assert.Single(cut.Notes));
        Assert.Equal(all.Edges.OrderByDescending(static edge => edge.Total).Take(5).Sum(static edge => edge.Total), cut.Edges.Sum(static edge => edge.Total));
        Assert.False(all.Truncated);
        Assert.Empty(all.Notes);
    }

    [Fact]
    public async Task Namespace_CutsTheDependencyEdgesButKeepsTheInheritEdges()
    {
        (CodeDiagramService diagrams, int sourceId) = await IndexAsync(HubGraph.Create());
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        CodeDiagram all = await diagrams.GetNamespaceAsync(new NamespaceDiagramRequest(sourceId, "Many"), cancellationToken);
        int dependencies = all.Edges.Count(static edge => edge.Style == DiagramEdgeStyles.Dependency);
        CodeDiagram cut = await diagrams.GetNamespaceAsync(new NamespaceDiagramRequest(sourceId, "Many", MaxEdges: 5), cancellationToken);

        Assert.Equal(Math.Min(dependencies, 5), cut.Edges.Count(static edge => edge.Style == DiagramEdgeStyles.Dependency));
        Assert.Equal(all.Edges.Count - dependencies, cut.Edges.Count(static edge => edge.Style != DiagramEdgeStyles.Dependency));
        Assert.Equal(dependencies > 5, cut.Truncated);

        CodeDiagram hub = await diagrams.GetNamespaceAsync(new NamespaceDiagramRequest(sourceId, "Hub", MaxEdges: 5), cancellationToken);
        Assert.Equal(2, hub.Edges.Count(static edge => edge.Style == DiagramEdgeStyles.Inherit));
    }

    [Fact]
    public async Task Type_AddsTheExternalTypesThatTheFocusUses()
    {
        (CodeDiagramService diagrams, int sourceId) = await IndexAsync(HubGraph.Create());
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        CodeDiagram diagram = await diagrams.GetTypeAsync(new TypeDiagramRequest(sourceId, "T:Hub.Hub", IncludeExternal: true), cancellationToken);

        Assert.Equal(["List<T>", "IDisposable"], diagram.Nodes.Where(static node => node.Kind == "external").Select(static node => node.Label));
        Assert.All(diagram.Nodes.Where(static node => node.Kind == "external"), static node => Assert.Null(node.Href));
        DiagramEdge list = Assert.Single(diagram.Edges, static edge => edge.To == "external:System.Collections.Generic.List<T>");
        Assert.Equal(("Demo.Core|T:Hub.Hub", DiagramEdgeStyles.Dependency, 6), (list.From, list.Style, list.Total));
        DiagramEdge disposable = Assert.Single(diagram.Edges, static edge => edge.From == "external:System.IDisposable");
        Assert.Equal(("Demo.Core|T:Hub.Hub", DiagramEdgeStyles.Implement), (disposable.To, disposable.Style));

        CodeDiagram without = await diagrams.GetTypeAsync(new TypeDiagramRequest(sourceId, "T:Hub.Hub"), cancellationToken);
        Assert.DoesNotContain(without.Nodes, static node => node.Kind == "external");

        CodeDiagram incomingOnly = await diagrams.GetTypeAsync(new TypeDiagramRequest(sourceId, "T:Hub.Hub", Direction: "usedBy", IncludeExternal: true), cancellationToken);
        Assert.DoesNotContain(incomingOnly.Nodes, static node => node.Kind == "external");
    }

    [Fact]
    public async Task Type_ShowsThePublicOrAllMembersInTheOrderOfTheLinesAndCutsThemAtTwelve()
    {
        (CodeDiagramService diagrams, int sourceId) = await IndexAsync(HubGraph.Create());
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        DiagramNode hub = (await diagrams.GetTypeAsync(new TypeDiagramRequest(sourceId, "T:Hub.Hub"), cancellationToken)).Nodes[0];
        Assert.Equal(16, hub.Members.Count);
        Assert.Equal(3, hub.HiddenMembers);
        Assert.Equal(["int Count", .. Enumerable.Range(0, 11).Select(static i => "int F" + i)], hub.Members.Take(12).Select(static member => member.Text));
        DiagramMember[] operations = [.. hub.Members.Skip(12)];
        Assert.Equal(["Hub()", "void Run()", "void Hook()", "Hub Create()"], operations.Select(static member => member.Text));
        Assert.Equal(["constructor", "method", "method", "method"], operations.Select(static member => member.Kind));
        Assert.Equal(["+", "+", "#", "+"], operations.Select(static member => member.Visibility));
        Assert.Equal([false, false, false, true], operations.Select(static member => member.IsStatic));
        Assert.Equal(("property", "+"), (hub.Members[0].Kind, hub.Members[0].Visibility));

        DiagramNode everything = (await diagrams.GetTypeAsync(new TypeDiagramRequest(sourceId, "T:Hub.Hub", Members: "all"), cancellationToken)).Nodes[0];
        Assert.Equal(17, everything.Members.Count);
        Assert.Equal(4, everything.HiddenMembers);
        Assert.Equal(("GPUScene _scene", "-"), (everything.Members[1].Text, everything.Members[1].Visibility));
        Assert.Contains(everything.Members, static member => member.Text == "void Helper()" && member.Visibility == "-");

        CodeDiagram none = await diagrams.GetTypeAsync(new TypeDiagramRequest(sourceId, "T:Hub.Hub", Members: "none"), cancellationToken);
        Assert.All(none.Nodes, static node => Assert.Equal((0, 0), (node.Members.Count, node.HiddenMembers)));

        DiagramNode plainBase = (await diagrams.GetTypeAsync(new TypeDiagramRequest(sourceId, "T:Hub.Hub"), cancellationToken)).Nodes[1];
        Assert.Empty(plainBase.Members);
        DiagramNode baseWithMembers = (await diagrams.GetTypeAsync(new TypeDiagramRequest(sourceId, "T:Hub.Hub", NeighborMembers: true), cancellationToken)).Nodes[1];
        DiagramMember draw = Assert.Single(baseWithMembers.Members);
        Assert.Equal(("void Draw()", "+", false, true), (draw.Text, draw.Visibility, draw.IsStatic, draw.IsAbstract));
        Assert.True(baseWithMembers.IsAbstract);
        Assert.Equal("Base", baseWithMembers.Label);
    }

    [Fact]
    public async Task Type_ShowsTheTypeParametersInTheLabel()
    {
        (CodeDiagramService diagrams, int sourceId) = await IndexAsync(HubGraph.Create());

        CodeDiagram diagram = await diagrams.GetTypeAsync(new TypeDiagramRequest(sourceId, "T:Hub.Registry`1"), TestContext.Current.CancellationToken);

        Assert.Equal("Registry<T>", diagram.Nodes[0].Label);
        Assert.Equal("Neighborhood of Registry<T>", diagram.Title);
        Assert.Contains("<B>Registry&lt;T&gt;</B>", diagram.Dot, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Diagrams_WriteTheTextFormats()
    {
        (CodeDiagramService diagrams, int sourceId) = await IndexAsync(SampleGraph.Create());

        CodeDiagram diagram = await diagrams.GetTypeAsync(new TypeDiagramRequest(sourceId, "T:Demo.SceneHost"), TestContext.Current.CancellationToken);
        string plantUml = PlantUmlWriter.Write(diagram);

        Assert.StartsWith("digraph code {", diagram.Dot, StringComparison.Ordinal);
        Assert.StartsWith("@startuml", plantUml, StringComparison.Ordinal);
        Assert.Contains("class \"SceneHost\" as n0 {", plantUml, StringComparison.Ordinal);
        Assert.Contains("abstract class \"Scene\" as n1", plantUml, StringComparison.Ordinal);
        Assert.Contains("n1 <|-- n0", plantUml, StringComparison.Ordinal);
        Assert.Contains("n2 <|.. n1", plantUml, StringComparison.Ordinal);
        Assert.Contains("n3 ..> n0 : 4", plantUml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Diagrams_RejectInvalidFieldsWithTheNameOfTheField()
    {
        (CodeDiagramService diagrams, int sourceId) = await IndexAsync(SampleGraph.Create());
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        await AssertFieldAsync("sourceId", diagrams.GetProjectsAsync(new ProjectsDiagramRequest(null), cancellationToken));
        await AssertFieldAsync("minWeight", diagrams.GetNamespacesAsync(new NamespacesDiagramRequest(sourceId, MinWeight: 0), cancellationToken));
        await AssertFieldAsync("maxNodes", diagrams.GetNamespacesAsync(new NamespacesDiagramRequest(sourceId, MaxNodes: 4), cancellationToken));
        await AssertFieldAsync("maxNodes", diagrams.GetNamespacesAsync(new NamespacesDiagramRequest(sourceId, MaxNodes: 151), cancellationToken));
        await AssertFieldAsync("maxEdges", diagrams.GetNamespacesAsync(new NamespacesDiagramRequest(sourceId, MaxEdges: 4), cancellationToken));
        await AssertFieldAsync("maxEdges", diagrams.GetNamespacesAsync(new NamespacesDiagramRequest(sourceId, MaxEdges: 501), cancellationToken));
        await AssertFieldAsync("maxEdges", diagrams.GetNamespaceAsync(new NamespaceDiagramRequest(sourceId, "Demo", MaxEdges: 4), cancellationToken));
        await AssertFieldAsync("maxEdges", diagrams.GetNamespaceAsync(new NamespaceDiagramRequest(sourceId, "Demo", MaxEdges: 501), cancellationToken));
        await AssertFieldAsync("namespace", diagrams.GetNamespaceAsync(new NamespaceDiagramRequest(sourceId, null), cancellationToken));
        await AssertFieldAsync("members", diagrams.GetNamespaceAsync(new NamespaceDiagramRequest(sourceId, "Demo", Members: "most"), cancellationToken));
        await AssertFieldAsync("maxNodes", diagrams.GetNamespaceAsync(new NamespaceDiagramRequest(sourceId, "Demo", MaxNodes: 1000), cancellationToken));
        await AssertFieldAsync("docId", diagrams.GetTypeAsync(new TypeDiagramRequest(sourceId, " "), cancellationToken));
        await AssertFieldAsync("depth", diagrams.GetTypeAsync(new TypeDiagramRequest(sourceId, "T:Demo.Scene", Depth: 0), cancellationToken));
        await AssertFieldAsync("depth", diagrams.GetTypeAsync(new TypeDiagramRequest(sourceId, "T:Demo.Scene", Depth: 4), cancellationToken));
        await AssertFieldAsync("direction", diagrams.GetTypeAsync(new TypeDiagramRequest(sourceId, "T:Demo.Scene", Direction: "sideways"), cancellationToken));
        await AssertFieldAsync("kinds", diagrams.GetTypeAsync(new TypeDiagramRequest(sourceId, "T:Demo.Scene", Kinds: "call,fly"), cancellationToken));
        await AssertFieldAsync("members", diagrams.GetTypeAsync(new TypeDiagramRequest(sourceId, "T:Demo.Scene", Members: "some"), cancellationToken));
        await AssertFieldAsync("maxNodes", diagrams.GetTypeAsync(new TypeDiagramRequest(sourceId, "T:Demo.Scene", MaxNodes: 4), cancellationToken));

        Assert.NotNull(await diagrams.GetTypeAsync(new TypeDiagramRequest(sourceId, "T:Demo.Scene", Depth: 3, Direction: "UsedBy", Kinds: "call, create", Members: "ALL", MaxNodes: 150), cancellationToken));
    }

    [Fact]
    public async Task Diagrams_ReportAMissingSourceGraphProjectNamespaceOrSymbol()
    {
        (CodeDiagramService diagrams, int sourceId) = await IndexAsync(SampleGraph.Create());
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SourceStore sources = _services!.GetRequiredService<SourceStore>();
        string other = Directory.CreateTempSubdirectory("project-rag-diagram-other-").FullName;
        try
        {
            int docs = await sources.AddAsync(new SourceInput { Name = "docs", RootPath = other }, cancellationToken);
            int bare = await sources.AddAsync(new SourceInput { Name = "bare", Kind = SourceKind.Code, RootPath = other }, cancellationToken);

            await AssertFieldAsync("sourceId", diagrams.GetProjectsAsync(new ProjectsDiagramRequest(docs), cancellationToken));
            Assert.Contains("no source", (await Assert.ThrowsAsync<CodeDiagramNotFoundException>(() => diagrams.GetProjectsAsync(new ProjectsDiagramRequest(sourceId + 1000), cancellationToken))).Message, StringComparison.Ordinal);
            CodeDiagramNotFoundException noGraph = await Assert.ThrowsAsync<CodeDiagramNotFoundException>(() => diagrams.GetTypeAsync(new TypeDiagramRequest(bare, "T:Demo.Scene"), cancellationToken));
            Assert.Contains("bare has no code graph", noGraph.Message, StringComparison.Ordinal);
            Assert.Contains("no project Nope", (await Assert.ThrowsAsync<CodeDiagramNotFoundException>(() => diagrams.GetNamespacesAsync(new NamespacesDiagramRequest(sourceId, "Nope"), cancellationToken))).Message, StringComparison.Ordinal);
            Assert.Contains("no types in the namespace Nope", (await Assert.ThrowsAsync<CodeDiagramNotFoundException>(() => diagrams.GetNamespaceAsync(new NamespaceDiagramRequest(sourceId, "Nope"), cancellationToken))).Message, StringComparison.Ordinal);
            Assert.Contains("no symbol T:Demo.Nothing", (await Assert.ThrowsAsync<CodeDiagramNotFoundException>(() => diagrams.GetTypeAsync(new TypeDiagramRequest(sourceId, "T:Demo.Nothing"), cancellationToken))).Message, StringComparison.Ordinal);
            await Assert.ThrowsAsync<CodeDiagramNotFoundException>(() => diagrams.GetTypeAsync(new TypeDiagramRequest(sourceId, "T:Demo.Scene", Project: "Demo.Nothing"), cancellationToken));
        }
        finally
        {
            Directory.Delete(other, recursive: true);
        }
    }

    [Fact]
    public async Task Diagrams_ValidateWithGraphvizWhenItIsInstalled()
    {
        (CodeDiagramService diagrams, int sourceId) = await IndexAsync(HubGraph.Create());
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        CodeDiagram[] all =
        [
            await diagrams.GetProjectsAsync(new ProjectsDiagramRequest(sourceId), cancellationToken),
            await diagrams.GetNamespacesAsync(new NamespacesDiagramRequest(sourceId), cancellationToken),
            await diagrams.GetNamespaceAsync(new NamespaceDiagramRequest(sourceId, "Hub", Members: "all", IncludeExternal: true), cancellationToken),
            await diagrams.GetTypeAsync(new TypeDiagramRequest(sourceId, "T:Hub.Hub", Depth: 2, Members: "all", NeighborMembers: true, IncludeExternal: true), cancellationToken),
            await diagrams.GetTypeAsync(new TypeDiagramRequest(sourceId, "T:Hub.Registry`1"), cancellationToken),
        ];

        foreach (CodeDiagram diagram in all)
        {
            (bool started, int exitCode, string error) = GraphvizRunner.Run(diagram.Dot);
            Assert.SkipUnless(started, "Graphviz dot is not installed.");
            Assert.True(exitCode == 0, $"Graphviz rejected the {diagram.View} diagram: {error}");
        }
    }

    private static async Task AssertFieldAsync(string field, Task<CodeDiagram> call)
    {
        CodeSearchException exception = await Assert.ThrowsAsync<CodeSearchException>(() => call);
        Assert.Equal(field, exception.Field);
    }

    [Fact]
    public async Task Diagrams_SumTheEdgesOfAPairThatSeveralFilesUse()
    {
        (CodeDiagramService diagrams, int whole) = await IndexAsync(HubGraph.Create());
        (_, int split) = await IndexAsync(HubGraph.Create(splitEdges: true), "split");
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        Func<int, Task<CodeDiagram>>[] views =
        [
            id => diagrams.GetNamespacesAsync(new NamespacesDiagramRequest(id), cancellationToken),
            id => diagrams.GetNamespaceAsync(new NamespaceDiagramRequest(id, "Hub", Members: "all", IncludeExternal: true), cancellationToken),
            id => diagrams.GetTypeAsync(new TypeDiagramRequest(id, "T:Hub.Hub", Depth: 2, Members: "all", NeighborMembers: true, IncludeExternal: true), cancellationToken),
            id => diagrams.GetTypeAsync(new TypeDiagramRequest(id, "T:Hub.Hub", Depth: 1, Direction: "usedBy"), cancellationToken),
        ];

        foreach (Func<int, Task<CodeDiagram>> view in views)
        {
            CodeDiagram expected = await view(whole);
            CodeDiagram actual = await view(split);
            Assert.NotEmpty(expected.Edges);
            Assert.Equal(System.Text.Json.JsonSerializer.Serialize(expected.Edges), System.Text.Json.JsonSerializer.Serialize(actual.Edges));
            Assert.Equal(expected.Nodes.Select(static node => (node.Id, node.Label, node.Count)), actual.Nodes.Select(static node => (node.Id, node.Label, node.Count)));
        }

        DiagramEdge spoke = (await diagrams.GetTypeAsync(new TypeDiagramRequest(split, "T:Hub.Hub", Direction: "usedBy"), cancellationToken)).Edges.Single(static edge => edge.From == "Demo.Core|T:N7.S7");
        Assert.Equal(8, spoke.Total);
        Assert.Equal([("call", 8)], spoke.Kinds.Select(static kind => (kind.Kind, kind.Count)));
    }

    private async Task<(CodeDiagramService Diagrams, int SourceId)> IndexAsync(CodeGraph graph, string name = "demo")
    {
        Assert.SkipWhen(_services is null, $"Set {VariableName} to run the tests of the database.");
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        _extractor.Graph = graph;
        foreach ((string path, string text) in SampleGraph.Files)
        {
            string full = Path.Combine(_root, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, text);
        }

        int sourceId = await _services!.GetRequiredService<SourceStore>().AddAsync(new SourceInput { Name = name, Kind = SourceKind.Code, RootPath = _root }, cancellationToken);
        IndexRun run = (await _services!.GetRequiredService<Indexer>().RunAsync(sourceId, null, cancellationToken))!;
        Assert.Equal(IndexRun.Succeeded, run.Status);
        return (_services!.GetRequiredService<CodeDiagramService>(), sourceId);
    }

    private sealed class NoPages : IPageClient
    {
        public Task<WebPage> GetAsync(Uri address, long maxBytes, CancellationToken cancellationToken) => throw new HttpRequestException("No pages.", null, System.Net.HttpStatusCode.NotFound);
    }

    private sealed class FakeEmbeddings : IEmbeddingClient
    {
        public Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<float[]>>([.. texts.Select(static _ => new float[] { 1, 0, 0, 0 })]);

        public Task<bool> IsHealthyAsync(CancellationToken cancellationToken) => Task.FromResult(true);
    }

    private sealed class FakeRerank : IRerankClient
    {
        public Task<IReadOnlyList<double>> RerankAsync(string query, IReadOnlyList<string> documents, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<double>>([.. documents.Select(static _ => 0.0)]);

        public Task<bool> IsHealthyAsync(CancellationToken cancellationToken) => Task.FromResult(true);
    }
}

// A hub type with a base, a derived type, eight spokes in eight namespaces, a test user, a far neighbor, members and external types, and a group of types in one namespace.
internal static class HubGraph
{
    private const string DeclarationFile = "src/Core/Scene.cs";
    private const string OtherFile = "src/Core/IScene.cs";

    // The edges of one pair of types can sit in one file or in two. The readers must sum them, so both graphs give the same diagrams.
    public static CodeGraph Create(bool splitEdges = false)
    {
        CodeGraph sample = SampleGraph.Create();
        List<GraphSymbol> symbols = [];
        List<GraphDeclaration> declarations = [];

        int Add(GraphSymbol symbol)
        {
            symbols.Add(symbol);
            int line = 100 + symbols.Count;
            declarations.Add(new GraphDeclaration(symbols.Count - 1, DeclarationFile, line, line, line, 1));
            return symbols.Count - 1;
        }

        int hub = Add(Type(0, "Hub", "Hub", "public class Hub : Base, IDisposable"));
        int baseType = Add(Type(0, "Hub", "Base", "public abstract class Base", "abstract"));
        int child = Add(Type(0, "Hub", "Child", "public class Child : Hub"));
        int[] spokes = [.. Enumerable.Range(0, 8).Select(i => Add(Type(0, "N" + i, "S" + i, "public class S" + i)))];
        int far = Add(Type(0, "Far", "Far", "public class Far"));
        int[] many = [.. Enumerable.Range(0, 8).Select(i => Add(Type(0, "Many", "U" + i, "public class U" + i)))];
        int testUser = Add(Type(1, "Hub", "TestUser", "public class TestUser"));
        Add(Type(0, "Hub", "Registry", "public sealed class Registry<in T> where T : class", "sealed") with { DocId = "T:Hub.Registry`1" });

        Add(Member(hub, "Hub.Hub", "property", "Count", "public int Count { get; set; }"));
        Add(Member(hub, "Hub.Hub", "field", "_scene", "private GPUScene _scene", "private"));
        foreach (int i in Enumerable.Range(0, 14))
            Add(Member(hub, "Hub.Hub", "field", "F" + i, "public int F" + i));

        Add(Member(hub, "Hub.Hub", "constructor", "Hub", "public Hub()"));
        Add(Member(hub, "Hub.Hub", "method", "Run", "public void Run()"));
        Add(Member(hub, "Hub.Hub", "method", "Hook", "protected void Hook()", "protected"));
        Add(Member(hub, "Hub.Hub", "method", "Helper", "private void Helper()", "private"));
        Add(Member(hub, "Hub.Hub", "method", "Create", "public static Hub Create()", "public", "static"));
        Add(Member(baseType, "Hub.Base", "method", "Draw", "public abstract void Draw()", "public", "abstract"));

        List<GraphTypeEdge> edges = [];
        void AddEdge(int from, int to, string kind, int count)
        {
            if (!splitEdges || count < 2)
            {
                edges.Add(new GraphTypeEdge(from, to, kind, count, DeclarationFile));
                return;
            }

            edges.Add(new GraphTypeEdge(from, to, kind, (count + 1) / 2, DeclarationFile));
            edges.Add(new GraphTypeEdge(from, to, kind, count / 2, OtherFile));
        }

        AddEdge(hub, baseType, CodeUseKinds.Inherit, 1);
        AddEdge(child, hub, CodeUseKinds.Inherit, 1);
        AddEdge(testUser, hub, CodeUseKinds.Call, 5);
        AddEdge(far, spokes[0], CodeUseKinds.Call, 1);
        for (int i = 0; i < spokes.Length; i++)
            AddEdge(spokes[i], hub, CodeUseKinds.Call, i + 1);

        for (int i = 1; i < many.Length; i++)
            AddEdge(many[i], many[0], CodeUseKinds.Type, i);

        List<GraphExternalEdge> external = [new(hub, "System.IDisposable", "System.Runtime", CodeUseKinds.Implement, 1, DeclarationFile)];
        if (splitEdges)
        {
            external.Add(new GraphExternalEdge(hub, "System.Collections.Generic.List<T>", "System.Collections", CodeUseKinds.Type, 4, DeclarationFile));
            external.Add(new GraphExternalEdge(hub, "System.Collections.Generic.List<T>", "System.Collections", CodeUseKinds.Type, 2, OtherFile));
        }
        else
        {
            external.Add(new GraphExternalEdge(hub, "System.Collections.Generic.List<T>", "System.Collections", CodeUseKinds.Type, 6, DeclarationFile));
        }

        return sample with
        {
            Symbols = symbols,
            Declarations = declarations,
            References = [],
            TypeEdges = edges,
            ExternalEdges = external,
            MemberLinks = [],
        };
    }

    private static GraphSymbol Type(int project, string ns, string name, string signature, params string[] modifiers) =>
        new(project, null, "class", name, ns + "." + name, "T:" + ns + "." + name, signature, ns, "public", modifiers, null);

    private static GraphSymbol Member(int parent, string owner, string kind, string name, string signature, string accessibility = "public", params string[] modifiers) =>
        new(0, parent, kind, name, owner + "." + name, "M:" + owner + "." + name, signature, owner[..owner.LastIndexOf('.')], accessibility, modifiers, null);
}
