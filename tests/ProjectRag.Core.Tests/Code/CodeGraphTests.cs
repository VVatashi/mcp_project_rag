using System.Diagnostics;

using Dapper;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Npgsql;

using ProjectRag.Core.Code;
using ProjectRag.Core.Code.Graph;
using ProjectRag.Core.Embeddings;
using ProjectRag.Core.Indexing;
using ProjectRag.Core.Reranking;
using ProjectRag.Core.Sources;
using ProjectRag.Core.Storage;

namespace ProjectRag.Core.Tests.Code;

/// <summary>
/// Runs the code graph against a real PostgreSQL. The extractor is a fake that returns a hand-built graph.
/// </summary>
/// <remarks>
/// The tests use the variable PROJECTRAG_TEST_DATABASE like the other tests of the database, but a database of their own with the suffix <c>_graph</c>,
/// so they can run next to those tests. Without the variable they skip themselves.
/// </remarks>
public sealed class CodeGraphTests : IAsyncLifetime
{
    private const string VariableName = "PROJECTRAG_TEST_DATABASE";

    private readonly string _root = Directory.CreateTempSubdirectory("project-rag-graph-").FullName;
    private readonly FakeCodeGraphExtractor _extractor = new();
    private ServiceProvider? _services;

    public async ValueTask InitializeAsync()
    {
        string? connectionString = Environment.GetEnvironmentVariable(VariableName);
        if (string.IsNullOrEmpty(connectionString))
            return;

        NpgsqlConnectionStringBuilder builder = new(connectionString);
        string database = (builder.Database ?? throw new InvalidOperationException($"{VariableName} names no database.")) + "_graph";
        builder.Database = "postgres";
        await using (NpgsqlConnection admin = new(builder.ConnectionString))
        {
            await admin.ExecuteAsync($"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE)");
            await admin.ExecuteAsync($"CREATE DATABASE \"{database}\"");
        }

        builder.Database = database;
        string graphConnection = builder.ConnectionString;
        NpgsqlConnection.ClearAllPools();
        DatabaseMigrator.MigrateUp(graphConnection, NullLoggerFactory.Instance);
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:ProjectRag"] = graphConnection,
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
    public async Task Indexer_StoresTheGraphAndDropsTheRowsOfUnknownPaths()
    {
        (ServiceProvider services, int sourceId, IndexRun run) = await IndexSampleAsync();

        Assert.Equal(IndexRun.Succeeded, run.Status);
        Assert.Null(run.Warning);
        Assert.Equal(1, _extractor.Calls);
        CodeGraphInput input = _extractor.LastInput!;
        Assert.Equal(_root, input.RootPath);
        Assert.Equal(["src/Core/Core.csproj", "tests/Core.Tests/Core.Tests.csproj"], input.ProjectPaths);
        Assert.Equal(["Demo.slnx"], input.SolutionPaths);
        Assert.Contains("README.md", input.KnownFiles);
        Assert.Contains("src/Core/Long.cs", input.KnownFiles);
        Assert.Equal("Release", input.Properties["Configuration"]);

        CodeGraphStatus status = (await services.GetRequiredService<CodeGraphService>().GetStatusAsync(sourceId, TestContext.Current.CancellationToken))!;
        Assert.NotNull(status.BuiltAt);
        Assert.Equal("10.0.300", status.SdkVersion);
        Assert.Null(status.Error);
        Assert.Equal((2, 13, 8), (status.ProjectCount, status.SymbolCount, status.ReferenceCount));
        Assert.Equal(["Demo.Core", "Demo.Core.Tests"], status.Projects.Select(static project => project.Name));
        CodeProject core = status.Projects[0];
        CodeProject tests = status.Projects[1];
        Assert.Equal(("net10.0", false, 11), (core.TargetFramework, core.IsTest, core.SymbolCount));
        Assert.Equal((true, 2), (tests.IsTest, tests.SymbolCount));
        Assert.Equal([core.ProjectId], tests.References);
        CodeSolution solution = Assert.Single(status.Solutions);
        Assert.Equal("Demo.slnx", solution.Path);
        Assert.Equal([core.ProjectId, tests.ProjectId], solution.ProjectIds);
        Assert.Equal(["error", "warning"], status.Diagnostics.Select(static diagnostic => diagnostic.Severity));
        Assert.Equal("Demo.Core.Tests", status.Diagnostics[1].Project);
        Assert.Null(status.Diagnostics[0].Project);

        Database database = services.GetRequiredService<Database>();
        await using NpgsqlConnection connection = await database.OpenAsync(TestContext.Current.CancellationToken);
        Assert.Equal(8, await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM code_file_project"));
        Assert.Equal(2, await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM code_file_project fp JOIN document d USING (document_id) WHERE d.path = 'src/Core/SceneHost.cs'"));
        Assert.Equal(14, await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM code_declaration"));
        Assert.Equal(0, await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM code_symbol WHERE name = 'Ghost'"));
        Assert.Equal(8, await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM code_type_edge"));
        Assert.Equal(3, await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM code_external_edge"));
        Assert.Equal(2, await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM code_member_link"));
    }

    [Fact]
    public async Task Indexer_SkipsAnUnchangedInputAndBuildsAgainAfterAChange()
    {
        (ServiceProvider services, int sourceId, _) = await IndexSampleAsync();
        Indexer indexer = services.GetRequiredService<Indexer>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        await indexer.RunAsync(sourceId, null, cancellationToken);
        Assert.Equal(1, _extractor.Calls);

        Write("README.md", "# Demo\n\nAnother text.\n");
        await indexer.RunAsync(sourceId, null, cancellationToken);
        Assert.Equal(1, _extractor.Calls);

        Write("src/Core/Scene.cs", SampleGraph.Files["src/Core/Scene.cs"] + "// changed\n");
        await indexer.RunAsync(sourceId, null, cancellationToken);
        Assert.Equal(2, _extractor.Calls);

        _extractor.Version = 2;
        await indexer.RunAsync(sourceId, null, cancellationToken);
        Assert.Equal(3, _extractor.Calls);

        SourceStore sources = services.GetRequiredService<SourceStore>();
        await sources.UpdateAsync(sourceId, new SourceInput { Name = "demo", Kind = SourceKind.Code, RootPath = _root, BuildProperties = new Dictionary<string, string> { ["Configuration"] = "Debug" } }, cancellationToken);
        await indexer.RunAsync(sourceId, null, cancellationToken);
        Assert.Equal(4, _extractor.Calls);
        Assert.Equal("Debug", _extractor.LastInput!.Properties["Configuration"]);

        await sources.UpdateAsync(sourceId, new SourceInput { Name = "demo", Kind = SourceKind.Code, RootPath = _root }, cancellationToken);
        Assert.Equal("Debug", (await sources.GetAsync(sourceId, null, cancellationToken))!.BuildProperties["Configuration"]);
        await sources.UpdateAsync(sourceId, new SourceInput { Name = "demo", Kind = SourceKind.Code, RootPath = _root, BuildProperties = new Dictionary<string, string>() }, cancellationToken);
        Assert.Empty((await sources.GetAsync(sourceId, null, cancellationToken))!.BuildProperties);
    }

    [Fact]
    public async Task Indexer_KeepsTheOldGraphWhenTheExtractorFails()
    {
        (ServiceProvider services, int sourceId, _) = await IndexSampleAsync();
        Indexer indexer = services.GetRequiredService<Indexer>();
        CodeGraphService graph = services.GetRequiredService<CodeGraphService>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Write("src/Core/Scene.cs", SampleGraph.Files["src/Core/Scene.cs"] + "// changed\n");
        _extractor.Failure = new CodeGraphException("The SDK is missing.");
        IndexRun failed = (await indexer.RunAsync(sourceId, null, cancellationToken))!;

        Assert.Equal(IndexRun.Succeeded, failed.Status);
        Assert.Contains("The SDK is missing.", failed.Warning, StringComparison.Ordinal);
        CodeGraphStatus status = (await graph.GetStatusAsync(sourceId, cancellationToken))!;
        Assert.Equal("The SDK is missing.", status.Error);
        Assert.NotNull(status.BuiltAt);
        Assert.Equal(13, status.SymbolCount);
        Assert.Single(await graph.SearchSymbolsAsync("SceneHost", sourceId, null, null, 5, cancellationToken), static match => match.Match == CodeMatchKinds.Exact);

        // The same input does not build again soon after an error, so the periodic scan does not repeat a slow failure.
        _extractor.Failure = null;
        IndexRun same = (await indexer.RunAsync(sourceId, null, cancellationToken))!;
        Assert.Null(same.Warning);
        Assert.Equal(2, _extractor.Calls);
        Assert.Equal("The SDK is missing.", (await graph.GetStatusAsync(sourceId, cancellationToken))!.Error);

        // A change of the input builds again at once.
        Write("src/Core/Scene.cs", SampleGraph.Files["src/Core/Scene.cs"] + "// fixed\n");
        IndexRun fixedRun = (await indexer.RunAsync(sourceId, null, cancellationToken))!;
        Assert.Null(fixedRun.Warning);
        Assert.Equal(3, _extractor.Calls);
        Assert.Null((await graph.GetStatusAsync(sourceId, cancellationToken))!.Error);

        Write("src/Core/Scene.cs", SampleGraph.Files["src/Core/Scene.cs"] + "// changed again\n");
        _extractor.Failure = new OperationCanceledException();
        IndexRun timeout = (await indexer.RunAsync(sourceId, null, cancellationToken))!;
        Assert.Equal(IndexRun.Succeeded, timeout.Status);
        Assert.Contains("longer than", timeout.Warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Indexer_ExplainsAGraphThatNeverSucceeded()
    {
        ServiceProvider services = RequireDatabase();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        WriteSample();
        _extractor.Failure = new CodeGraphException("No project could load.");
        int sourceId = await services.GetRequiredService<SourceStore>().AddAsync(new SourceInput { Name = "demo", Kind = SourceKind.Code, RootPath = _root }, cancellationToken);
        IndexRun run = (await services.GetRequiredService<Indexer>().RunAsync(sourceId, null, cancellationToken))!;

        Assert.Equal(IndexRun.Succeeded, run.Status);
        CodeGraphService graph = services.GetRequiredService<CodeGraphService>();
        CodeGraphStatus status = (await graph.GetStatusAsync(sourceId, cancellationToken))!;
        Assert.Null(status.BuiltAt);
        Assert.Equal("No project could load.", status.Error);
        CodeSearchException missing = await Assert.ThrowsAsync<CodeSearchException>(() => graph.RequireGraphAsync("demo", cancellationToken));
        Assert.Contains("No project could load.", missing.Message, StringComparison.Ordinal);
        Assert.Contains("index_source", missing.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Indexer_RemovesTheGraphOfASourceWithoutProjectFiles()
    {
        (ServiceProvider services, int sourceId, _) = await IndexSampleAsync();
        File.Delete(Path.Combine(_root, "src/Core/Core.csproj"));
        File.Delete(Path.Combine(_root, "tests/Core.Tests/Core.Tests.csproj"));

        await services.GetRequiredService<Indexer>().RunAsync(sourceId, null, TestContext.Current.CancellationToken);

        CodeGraphStatus status = (await services.GetRequiredService<CodeGraphService>().GetStatusAsync(sourceId, TestContext.Current.CancellationToken))!;
        Assert.Null(status.BuiltAt);
        Assert.Empty(status.Projects);
        Assert.Equal(1, _extractor.Calls);
    }

    [Fact]
    public async Task Indexer_LeavesASourceOfDocumentationAlone()
    {
        ServiceProvider services = RequireDatabase();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        Write("note.md", "# Note\n\nA text.\n");
        int sourceId = await services.GetRequiredService<SourceStore>().AddAsync(new SourceInput { Name = "docs", RootPath = _root }, cancellationToken);

        IndexRun run = (await services.GetRequiredService<Indexer>().RunAsync(sourceId, null, cancellationToken))!;

        Assert.Equal(IndexRun.Succeeded, run.Status);
        Assert.Equal(0, _extractor.Calls);
        Assert.Null(await services.GetRequiredService<CodeGraphStore>().GetStateAsync(sourceId, cancellationToken));
        CodeSearchException notCode = await Assert.ThrowsAsync<CodeSearchException>(() => services.GetRequiredService<CodeGraphService>().GetStatusAsync(sourceId, cancellationToken));
        Assert.Equal("sourceId", notCode.Field);
        Assert.Null(await services.GetRequiredService<CodeGraphService>().GetStatusAsync(sourceId + 100, cancellationToken));
    }

    [Fact]
    public async Task Search_OrdersTheMatchesByTheirClass()
    {
        (ServiceProvider services, int sourceId, _) = await IndexSampleAsync();
        CodeGraphService graph = services.GetRequiredService<CodeGraphService>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        IReadOnlyList<SymbolMatch> scene = await graph.SearchSymbolsAsync("Scene", sourceId, "type", null, 10, cancellationToken);
        Assert.Equal(["Scene", "SceneHost", "SceneTests", "SceneSynchronizer"], scene.Take(4).Select(static match => match.Name));
        Assert.Equal([CodeMatchKinds.Exact, CodeMatchKinds.Prefix, CodeMatchKinds.Prefix, CodeMatchKinds.Prefix], scene.Take(4).Select(static match => match.Match));
        Assert.All(scene, static match => Assert.True(CodeSymbolKinds.IsType(match.Kind)));
        Assert.Equal(scene.Count, scene.Select(static match => match.SymbolId).Distinct().Count());

        SymbolMatch ignoreCase = (await graph.SearchSymbolsAsync("scenehost", sourceId, null, null, 5, cancellationToken))[0];
        Assert.Equal((CodeMatchKinds.ExactIgnoreCase, "Demo.SceneHost"), (ignoreCase.Match, ignoreCase.FullName));

        SymbolMatch humps = (await graph.SearchSymbolsAsync("SSync", sourceId, null, null, 5, cancellationToken))[0];
        Assert.Equal((CodeMatchKinds.Humps, "SceneSynchronizer"), (humps.Match, humps.Name));

        SymbolMatch qualified = (await graph.SearchSymbolsAsync("Scene.Update", sourceId, null, null, 5, cancellationToken))[0];
        Assert.Equal((CodeMatchKinds.Qualified, "M:Demo.Scene.Update(System.Double)"), (qualified.Match, qualified.DocId));

        SymbolMatch fuzzy = (await graph.SearchSymbolsAsync("SceneHots", sourceId, null, null, 5, cancellationToken))[0];
        Assert.Equal((CodeMatchKinds.Fuzzy, "SceneHost"), (fuzzy.Match, fuzzy.Name));

        IReadOnlyList<SymbolMatch> substring = await graph.SearchSymbolsAsync("ynchron", sourceId, null, null, 5, cancellationToken);
        Assert.Equal((CodeMatchKinds.Substring, "SceneSynchronizer"), (substring[0].Match, substring[0].Name));

        IReadOnlyList<SymbolMatch> updates = await graph.SearchSymbolsAsync("Update", sourceId, "method", null, 10, cancellationToken);
        Assert.Equal(["Demo.IScene.Update", "Demo.Scene.Update", "Demo.SceneHost.Update"], updates.Where(static match => match.Match == CodeMatchKinds.Exact).Select(static match => match.FullName));

        IReadOnlyList<SymbolMatch> inTests = await graph.SearchSymbolsAsync("Scene", sourceId, null, "Demo.Core.Tests", 10, cancellationToken);
        Assert.Equal("SceneTests", inTests[0].Name);
        Assert.All(inTests, static match => Assert.Equal("Demo.Core.Tests", match.Project));
        Assert.Single(await graph.SearchSymbolsAsync("Scene", sourceId, "type", null, 1, cancellationToken));
        Assert.Empty(await graph.SearchSymbolsAsync("Zzzzzz", sourceId, null, null, 5, cancellationToken));
        Assert.Equal("q", (await Assert.ThrowsAsync<CodeSearchException>(() => graph.SearchSymbolsAsync(" ", sourceId, null, null, 5, cancellationToken))).Field);
    }

    [Fact]
    public async Task Detail_GivesTheFieldsOfATypeAndOfAMember()
    {
        (ServiceProvider services, int sourceId, _) = await IndexSampleAsync();
        CodeGraphService graph = services.GetRequiredService<CodeGraphService>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        SymbolDetail host = (await graph.GetDetailAsync(sourceId, "T:Demo.SceneHost", null, cancellationToken))!;
        Assert.Equal(("class", "Demo.SceneHost", "Demo.Core", "src/Core/SceneHost.Sync.cs", 3), (host.Symbol.Kind, host.Symbol.FullName, host.Symbol.Project, host.Symbol.Path, host.Symbol.Line));
        Assert.Equal("Runs a scene. It has several overloads.", host.Symbol.Summary);
        Assert.Equal(["sealed", "partial"], host.Modifiers);
        Assert.Null(host.Parent);
        Assert.Equal(["src/Core/SceneHost.Sync.cs", "src/Core/SceneHost.cs"], host.Declarations.Select(static declaration => declaration.Path));
        Assert.Equal((4, 29), (host.Declarations[1].NameLine, host.Declarations[1].NameColumn));
        Assert.Equal(["Sync", "Update", "Reset", "Reset"], host.Members.Select(static member => member.Name));
        Assert.Equal(["Demo.Scene"], host.Bases.Select(static type => type.FullName));
        Assert.Empty(host.Derived);
        Assert.Equal(4, host.ReferenceCount);
        Assert.Equal(["Demo.Scene", "Demo.SceneSynchronizer"], host.Uses.Select(static edge => edge.Symbol.FullName).Order(StringComparer.Ordinal));
        Assert.Equal(["Demo.SceneSynchronizer", "Demo.SceneTests"], host.UsedBy.Select(static edge => edge.Symbol.FullName));
        EdgeSummary synchronizer = host.UsedBy[0];
        Assert.Equal(4, synchronizer.Total);
        Assert.Equal([("signature", 1), ("create", 1), ("call", 2)], synchronizer.Kinds.Select(static kind => (kind.Kind, kind.Count)));
        Assert.Empty(host.ExternalUses);

        SymbolDetail scene = (await graph.GetDetailAsync(host.Bases[0].SymbolId, cancellationToken))!;
        Assert.Equal(["Demo.SceneHost"], scene.Derived.Select(static type => type.FullName));
        Assert.Equal(["Demo.IScene"], scene.Bases.Select(static type => type.FullName));

        SymbolDetail iScene = (await graph.GetDetailAsync(sourceId, "T:Demo.IScene", null, cancellationToken))!;
        Assert.Equal(["Demo.Scene"], iScene.Derived.Select(static type => type.FullName));

        SymbolDetail run = (await graph.GetDetailAsync(sourceId, "M:Demo.SceneSynchronizer.Run(Demo.SceneHost)", null, cancellationToken))!;
        Assert.Equal("Demo.SceneSynchronizer", run.Parent!.FullName);
        Assert.Empty(run.Members);
        Assert.Empty(run.UsedBy);
        Assert.Equal(3, run.Uses.Count);
        EdgeSummary hostUse = run.Uses.Single(static edge => edge.Symbol.FullName == "Demo.SceneHost");
        Assert.Equal([("signature", 1), ("create", 1)], hostUse.Kinds.Select(static kind => (kind.Kind, kind.Count)));
        Assert.Equal(["Demo.SceneHost", "Demo.SceneHost.Sync", "Demo.SceneHost.Update"], run.Uses.Select(static edge => edge.Symbol.FullName).Order(StringComparer.Ordinal));

        SymbolDetail update = (await graph.GetDetailAsync(sourceId, "M:Demo.SceneHost.Update(System.Double)", null, cancellationToken))!;
        Assert.Equal(["override"], update.Modifiers);
        Assert.Equal(["Demo.Scene.Update"], update.Overrides.Select(static member => member.FullName));
        Assert.Empty(update.OverriddenBy);
        Assert.Equal(["Demo.SceneSynchronizer.Run", "Demo.SceneTests.Updates"], update.UsedBy.Select(static edge => edge.Symbol.FullName).Order(StringComparer.Ordinal));

        SymbolDetail synchronizerType = (await graph.GetDetailAsync(sourceId, "T:Demo.SceneSynchronizer", null, cancellationToken))!;
        ExternalUse list = synchronizerType.ExternalUses[0];
        Assert.Equal(("System.Collections.Generic.List<T>", "System.Collections", 6), (list.Target, list.Assembly, list.Total));
        Assert.Equal([("signature", 2), ("type", 4)], list.Kinds.Select(static kind => (kind.Kind, kind.Count)));
        Assert.Equal(2, synchronizerType.ExternalUses.Count);

        Assert.Null(await graph.GetDetailAsync(sourceId, "T:Demo.SceneHost", "Demo.Core.Tests", cancellationToken));
        Assert.NotNull(await graph.GetDetailAsync(sourceId, "T:Demo.SceneHost", "Demo.Core", cancellationToken));
        Assert.Null(await graph.GetDetailAsync(sourceId, "T:Demo.Nothing", null, cancellationToken));
        Assert.Null(await graph.GetDetailAsync(host.Symbol.SymbolId + 10_000, cancellationToken));
    }

    [Fact]
    public async Task References_GiveTheTextOfTheLineAndTheCaller()
    {
        (ServiceProvider services, int sourceId, _) = await IndexSampleAsync();
        CodeGraphService graph = services.GetRequiredService<CodeGraphService>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        int host = (await graph.GetDetailAsync(sourceId, "T:Demo.SceneHost", null, cancellationToken))!.Symbol.SymbolId;

        CodeReferencePage page = (await graph.GetReferencesAsync(host, null, 0, 100, cancellationToken))!;

        Assert.Equal(4, page.Total);
        Assert.Equal(["src/Core/Long.cs", "src/Core/SceneSynchronizer.cs", "src/Core/SceneSynchronizer.cs", "tests/Core.Tests/SceneTests.cs"], page.Items.Select(static item => item.Path));
        CodeReference signature = page.Items[1];
        Assert.Equal((5, 21, 9, "signature", "    public void Run(SceneHost host)", 0), (signature.Line, signature.Column, signature.Length, signature.Kind, signature.Text, signature.TextStart));
        Assert.Equal("Demo.SceneSynchronizer.Run", signature.From!.FullName);
        Assert.Equal("        var copy = new SceneHost();", page.Items[2].Text);
        CodeReference longLine = page.Items[0];
        Assert.True(FakeCodeGraphExtractor.LongLine.Length > 450);
        Assert.Equal(FakeCodeGraphExtractor.LongLine.Length - CodeLineMatcher.MaxLineText, longLine.TextStart);
        Assert.Equal(CodeLineMatcher.MaxLineText, longLine.Text.Length);
        Assert.EndsWith("SceneHost", longLine.Text, StringComparison.Ordinal);
        Assert.Null(longLine.From);

        CodeReferencePage creates = (await graph.GetReferencesAsync(host, "create", 0, 100, cancellationToken))!;
        Assert.Equal(2, creates.Total);
        CodeReferencePage second = (await graph.GetReferencesAsync(host, null, 1, 2, cancellationToken))!;
        Assert.Equal((4, 2), (second.Total, second.Items.Count));
        Assert.Equal(5, second.Items[0].Line);
        Assert.Null(await graph.GetReferencesAsync(host + 10_000, null, 0, 10, cancellationToken));

        CodeReferencePage inFolder = await graph.GetReferencesAsync([host], null, "tests/", 0, 10, cancellationToken);
        Assert.Equal(["tests/Core.Tests/SceneTests.cs"], inFolder.Items.Select(static item => item.Path));
        CodeReferencePage inFile = await graph.GetReferencesAsync([host], null, "src/Core/Long.cs", 0, 10, cancellationToken);
        Assert.Single(inFile.Items);
    }

    [Fact]
    public async Task Outline_ListsTheSymbolsOfAFileAsATree()
    {
        (ServiceProvider services, _, _) = await IndexSampleAsync();
        CodeGraphService graph = services.GetRequiredService<CodeGraphService>();
        DocumentStoreHelper documents = new(services);
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        OutlineNode host = Assert.Single(await graph.GetOutlineAsync(await documents.IdAsync("src/Core/SceneHost.cs", cancellationToken), cancellationToken));
        Assert.Equal(("SceneHost", 4), (host.Symbol.Name, host.Symbol.Line));
        Assert.Equal([("Update", 6), ("Reset", 10), ("Reset", 14)], host.Children.Select(static child => (child.Symbol.Name, child.Symbol.Line)));

        OutlineNode part = Assert.Single(await graph.GetOutlineAsync(await documents.IdAsync("src/Core/SceneHost.Sync.cs", cancellationToken), cancellationToken));
        Assert.Equal(("SceneHost", 3), (part.Symbol.Name, part.Symbol.Line));
        Assert.Equal([("Sync", 5)], part.Children.Select(static child => (child.Symbol.Name, child.Symbol.Line)));
        Assert.Equal("src/Core/SceneHost.Sync.cs", part.Symbol.Path);

        Assert.Empty(await graph.GetOutlineAsync(await documents.IdAsync("README.md", cancellationToken), cancellationToken));

        string text = CodeGraphFormatter.OutlineFile("src/Core/SceneHost.cs", await graph.GetOutlineAsync(await documents.IdAsync("src/Core/SceneHost.cs", cancellationToken), cancellationToken));
        Assert.Equal(
            "src/Core/SceneHost.cs\n  4: public sealed partial class SceneHost : Scene\n    6: public override void Update(double time)\n    10: public void Reset()\n    14: public void Reset(bool all)\n",
            text.Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Resolve_GroupsOverloadsAndReportsAmbiguity()
    {
        (ServiceProvider services, _, _) = await IndexSampleAsync();
        CodeGraphService graph = services.GetRequiredService<CodeGraphService>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        CodeResolution updates = await graph.ResolveAsync("demo", "Update", cancellationToken);
        Assert.Equal(["Demo.IScene.Update", "Demo.Scene.Update", "Demo.SceneHost.Update"], updates.Groups.Select(static group => group.Primary.FullName));

        CodeSymbolGroup reset = Assert.Single((await graph.ResolveAsync("demo", "SceneHost.Reset", cancellationToken)).Groups);
        Assert.Equal(2, reset.Symbols.Count);
        Assert.False(reset.IsType);
        Assert.Equal(2, Assert.Single((await graph.ResolveAsync("demo", "M:Demo.SceneHost.Reset", cancellationToken)).Groups).Symbols.Count);
        Assert.Single(Assert.Single((await graph.ResolveAsync("demo", "M:Demo.SceneHost.Reset(System.Boolean)", cancellationToken)).Groups).Symbols);
        Assert.Single(Assert.Single((await graph.ResolveAsync("demo", "SceneHost.Reset(bool)", cancellationToken)).Groups).Symbols.Take(1));

        CodeSymbolGroup type = Assert.Single((await graph.ResolveAsync(null, "SceneHost", cancellationToken)).Groups);
        Assert.True(type.IsType);
        Assert.Equal("Demo.SceneHost", Assert.Single((await graph.ResolveAsync("demo", "T:Demo.SceneHost", cancellationToken)).Groups).Primary.FullName);
        Assert.Equal("Demo.Scene.Update", Assert.Single((await graph.ResolveAsync("demo", "Scene.Update", cancellationToken)).Groups).Primary.FullName);
        Assert.Equal("Demo.SceneHost", Assert.Single((await graph.ResolveAsync("demo", "scenehost", cancellationToken)).Groups).Primary.FullName);
        Assert.Empty((await graph.ResolveAsync("demo", "Nothing", cancellationToken)).Groups);

        CodeSearchException ambiguous = await Assert.ThrowsAsync<CodeSearchException>(() => graph.ResolveOneAsync("demo", "Update", cancellationToken));
        Assert.Contains("3 symbols match", ambiguous.Message, StringComparison.Ordinal);
        Assert.Contains("M:Demo.Scene.Update(System.Double)", ambiguous.Message, StringComparison.Ordinal);
        Assert.Contains("src/Core/Scene.cs:6", ambiguous.Message, StringComparison.Ordinal);
        CodeSearchException none = await Assert.ThrowsAsync<CodeSearchException>(() => graph.ResolveOneAsync("demo", "Nothing", cancellationToken));
        Assert.Contains("has no symbol Nothing", none.Message, StringComparison.Ordinal);
        Assert.Equal("codebase", (await Assert.ThrowsAsync<CodeSearchException>(() => graph.ResolveAsync("missing", "Update", cancellationToken))).Field);
    }

    [Fact]
    public async Task Codebase_IsOptionalOnlyWhenThereIsOneCodeSource()
    {
        (ServiceProvider services, _, _) = await IndexSampleAsync();
        CodeGraphService graph = services.GetRequiredService<CodeGraphService>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SourceStore sources = services.GetRequiredService<SourceStore>();
        string other = Directory.CreateTempSubdirectory("project-rag-other-").FullName;
        try
        {
            Assert.Equal("demo", (await graph.FindCodebaseAsync(null, cancellationToken)).Name);
            await sources.AddAsync(new SourceInput { Name = "docs", RootPath = other }, cancellationToken);
            Assert.Equal("demo", (await graph.FindCodebaseAsync(null, cancellationToken)).Name);
            Assert.Equal("codebase", (await Assert.ThrowsAsync<CodeSearchException>(() => graph.FindCodebaseAsync("docs", cancellationToken))).Field);

            await sources.AddAsync(new SourceInput { Name = "second", Kind = SourceKind.Code, RootPath = other }, cancellationToken);
            CodeSearchException several = await Assert.ThrowsAsync<CodeSearchException>(() => graph.FindCodebaseAsync(null, cancellationToken));
            Assert.Contains("demo, second", several.Message, StringComparison.Ordinal);
            CodeSearchException noGraph = await Assert.ThrowsAsync<CodeSearchException>(() => graph.RequireGraphAsync("second", cancellationToken));
            Assert.Contains("no code graph", noGraph.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(other, recursive: true);
        }
    }

    [Fact]
    public async Task Relations_WalkTheEdgesBetweenTypes()
    {
        (ServiceProvider services, _, _) = await IndexSampleAsync();
        CodeGraphService graph = services.GetRequiredService<CodeGraphService>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        (_, CodeSymbolGroup host) = await graph.ResolveOneAsync("demo", "SceneHost", cancellationToken);

        CodeRelations both = await graph.GetRelationsAsync(host, new CodeRelationOptions(), cancellationToken);
        Assert.Equal(["Demo.Scene"], both.Bases.Select(static type => type.FullName));
        Assert.Equal(["Demo.SceneSynchronizer", "Demo.SceneTests"], both.UsedBy.Select(static node => node.Symbol.FullName));
        Assert.Equal(["Demo.Scene", "Demo.SceneSynchronizer"], both.Uses.Select(static node => node.Symbol.FullName).Order(StringComparer.Ordinal));
        Assert.All(both.Uses.Concat(both.UsedBy), static node => Assert.Equal(1, node.Depth));

        CodeRelations withoutTests = await graph.GetRelationsAsync(host, new CodeRelationOptions(IncludeTests: false), cancellationToken);
        Assert.Equal(["Demo.SceneSynchronizer"], withoutTests.UsedBy.Select(static node => node.Symbol.FullName));

        CodeRelations creates = await graph.GetRelationsAsync(host, new CodeRelationOptions(Kinds: new HashSet<string> { "create" }), cancellationToken);
        Assert.Empty(creates.Uses);
        Assert.Equal([("create", 1)], creates.UsedBy[0].Kinds.Select(static kind => (kind.Kind, kind.Count)));

        CodeRelations onlyUses = await graph.GetRelationsAsync(host, new CodeRelationOptions(CodeRelationDirection.Uses, Depth: 2), cancellationToken);
        Assert.Empty(onlyUses.UsedBy);
        RelationNode deep = onlyUses.Uses.Single(static node => node.Depth == 2);
        Assert.Equal(("Demo.IScene", "Demo.Scene"), (deep.Symbol.FullName, deep.Via!.FullName));
        Assert.Equal(3, onlyUses.Uses.Count);

        (_, CodeSymbolGroup synchronizer) = await graph.ResolveOneAsync("demo", "SceneSynchronizer", cancellationToken);
        CodeRelations external = await graph.GetRelationsAsync(synchronizer, new CodeRelationOptions(IncludeExternal: true), cancellationToken);
        Assert.Equal("System.Collections.Generic.List<T>", external.External[0].Target);
        Assert.Empty((await graph.GetRelationsAsync(synchronizer, new CodeRelationOptions(), cancellationToken)).External);

        (_, CodeSymbolGroup update) = await graph.ResolveOneAsync("demo", "SceneHost.Update", cancellationToken);
        CodeRelations member = await graph.GetRelationsAsync(update, new CodeRelationOptions(Depth: 3), cancellationToken);
        Assert.Equal(["Demo.SceneSynchronizer.Run", "Demo.SceneTests.Updates"], member.UsedBy.Select(static node => node.Symbol.FullName).Order(StringComparer.Ordinal));
        Assert.Equal(["Demo.Scene.Update"], member.Overrides.Select(static symbol => symbol.FullName));
        Assert.Equal(1, member.Depth);

        string text = CodeGraphFormatter.Relations(both);
        Assert.Contains("Used by (types):", text, StringComparison.Ordinal);
        Assert.Contains("class Demo.SceneSynchronizer — signature 1, create 1, call 2 — Demo.Core — src/Core/SceneSynchronizer.cs:3", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Map_GivesTheProjectsTheNamespacesAndTheTypes()
    {
        (ServiceProvider services, int sourceId, _) = await IndexSampleAsync();
        CodeGraphService graph = services.GetRequiredService<CodeGraphService>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        CodeMapOverview overview = (await graph.GetMapOverviewAsync(sourceId, cancellationToken))!;
        Assert.Equal([4, 1], overview.Status.Projects.Select(project => overview.TypeCounts.GetValueOrDefault(project.ProjectId)));
        string overviewText = CodeGraphFormatter.MapOverview(overview);
        Assert.Contains("Demo.Core.Tests [test] net10.0", overviewText, StringComparison.Ordinal);
        Assert.Contains("-> Demo.Core", overviewText, StringComparison.Ordinal);
        Assert.Contains("Demo.slnx: Demo.Core, Demo.Core.Tests", overviewText, StringComparison.Ordinal);
        Assert.Contains("SDK 10.0.300", overviewText, StringComparison.Ordinal);

        CodeProjectMap project = (await graph.GetProjectMapAsync(sourceId, "Demo.Core", cancellationToken))!;
        Assert.Equal([("Demo", 4)], project.Namespaces.Select(static item => (item.Namespace, item.Types)));
        Assert.Empty(project.Dependencies);
        Assert.Null(await graph.GetProjectMapAsync(sourceId, "Nothing", cancellationToken));

        CodeNamespaceMap map = await graph.GetNamespaceMapAsync(sourceId, "Demo", null, cancellationToken);
        Assert.Equal(["Demo.IScene", "Demo.Scene", "Demo.SceneHost", "Demo.SceneSynchronizer", "Demo.SceneTests"], map.Types.Select(static type => type.Symbol.FullName));
        NamespaceType host = map.Types.Single(static type => type.Symbol.Name == "SceneHost");
        Assert.Equal("Demo.SceneSynchronizer", host.TopUsedBy[0].Symbol.FullName);
        Assert.Equal(2, host.TopUsedBy.Count);
        Assert.Equal(["Demo.SceneTests"], (await graph.GetNamespaceMapAsync(sourceId, "Demo", "Demo.Core.Tests", cancellationToken)).Types.Select(static type => type.Symbol.FullName));
        CodeNamespaceMap unknown = await graph.GetNamespaceMapAsync(sourceId, "Dem", null, cancellationToken);
        Assert.Empty(unknown.Types);
        Assert.Equal(["Demo"], unknown.Similar);
        Assert.Contains("Similar namespaces: Demo", CodeGraphFormatter.NamespaceMap(unknown), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Store_ClearsTheGraphWhenAFileOrASourceGoesAway()
    {
        (ServiceProvider services, int sourceId, _) = await IndexSampleAsync();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using NpgsqlConnection connection = await services.GetRequiredService<Database>().OpenAsync(cancellationToken);

        File.Delete(Path.Combine(_root, "src/Core/SceneSynchronizer.cs"));
        await services.GetRequiredService<Indexer>().RunAsync(sourceId, null, cancellationToken);

        // The fake still returns the whole graph. The writer drops the rows of the deleted file and the symbols that lose their only declaration.
        Assert.Equal(2, _extractor.Calls);
        Assert.Equal(11, await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM code_symbol"));
        Assert.Equal(4, await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM code_reference"));
        Assert.Equal(0, await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM code_symbol WHERE name = 'SceneSynchronizer'"));
        Assert.Equal(4, await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM code_type_edge"));
        Assert.Equal(0, await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM code_external_edge"));
        await services.GetRequiredService<SourceStore>().DeleteAsync(sourceId, cancellationToken);
        foreach (string table in new[] { "code_project", "code_symbol", "code_declaration", "code_reference", "code_type_edge", "code_external_edge", "code_member_link", "code_diagnostic", "code_file_project", "code_solution", "code_graph_state" })
            Assert.Equal(0, await connection.ExecuteScalarAsync<long>($"SELECT count(*) FROM {table}"));
    }

    [Fact]
    public async Task Store_WritesALargeGraphQuickly()
    {
        ServiceProvider services = RequireDatabase();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        int sourceId = await services.GetRequiredService<SourceStore>().AddAsync(new SourceInput { Name = "big", Kind = SourceKind.Code, RootPath = _root }, cancellationToken);
        const int fileCount = 400;
        const int typeCount = 3000;
        const int membersPerType = 10;
        const int referenceTotal = 515_000;
        await using (NpgsqlConnection connection = await services.GetRequiredService<Database>().OpenAsync(cancellationToken))
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO document (source_id, path, title, content, content_hash, parser_version, size_bytes, modified_at, category, language)
                SELECT @SourceId, 'f' || n || '.cs', 'f' || n || '.cs', 'class C {}', 'h' || n, 'code:1', 10, now(), 'big', 'csharp' FROM generate_series(1, @fileCount) n
                """,
                new { SourceId = sourceId, fileCount });
        }

        List<GraphSymbol> symbols = [];
        List<GraphDeclaration> declarations = [];
        for (int t = 0; t < typeCount; t++)
        {
            int typeIndex = symbols.Count;
            string name = "Type" + t.ToString(System.Globalization.CultureInfo.InvariantCulture);
            string path = $"f{(t % fileCount) + 1}.cs";
            symbols.Add(new GraphSymbol(0, null, "class", name, "Big." + name, "T:Big." + name, "public class " + name, "Big", "public", [], "The type " + name + "."));
            declarations.Add(new GraphDeclaration(typeIndex, path, 1, 100, 1, 14));
            for (int m = 0; m < membersPerType; m++)
            {
                int member = symbols.Count;
                string memberName = "Member" + m.ToString(System.Globalization.CultureInfo.InvariantCulture);
                symbols.Add(new GraphSymbol(0, typeIndex, "method", memberName, $"Big.{name}.{memberName}", $"M:Big.{name}.{memberName}", $"public void {memberName}()", "Big", "public", ["static"], null));
                declarations.Add(new GraphDeclaration(member, path, 2 + m, 2 + m, 2 + m, 17));
            }
        }

        Random random = new(7);
        List<GraphReference> references = new(referenceTotal);
        string[] kinds = [.. CodeUseKinds.All];
        for (int r = 0; r < referenceTotal; r++)
            references.Add(new GraphReference(random.Next(symbols.Count), $"f{random.Next(fileCount) + 1}.cs", 1 + random.Next(300), 1 + random.Next(80), 8, kinds[random.Next(kinds.Length)], random.Next(symbols.Count)));

        List<GraphTypeEdge> edges = [.. Enumerable.Range(0, 23_000).Select(i => new GraphTypeEdge(random.Next(typeCount) * (membersPerType + 1), random.Next(typeCount) * (membersPerType + 1), kinds[i % kinds.Length], 1 + (i % 7), $"f{(i % fileCount) + 1}.cs"))];
        CodeGraph graph = new("10.0.300", [new GraphProject("big.csproj", "Big", "net10.0", false, [], fileCount, 0)], [], [], symbols, declarations, references, edges, [], [], []);

        Stopwatch watch = Stopwatch.StartNew();
        CodeGraphCounts counts = await services.GetRequiredService<CodeGraphStore>().ReplaceAsync(sourceId, graph, "hash", 1, TimeSpan.FromSeconds(1), cancellationToken);
        TimeSpan first = watch.Elapsed;
        watch.Restart();
        await services.GetRequiredService<CodeGraphStore>().ReplaceAsync(sourceId, graph, "hash", 1, TimeSpan.FromSeconds(1), cancellationToken);
        TimeSpan second = watch.Elapsed;

        Assert.Equal((1, typeCount * (membersPerType + 1), referenceTotal), (counts.Projects, counts.Symbols, counts.References));
        Assert.True(first < TimeSpan.FromSeconds(15) && second < TimeSpan.FromSeconds(15), $"The first write took {first.TotalSeconds:F1} s and the replacement {second.TotalSeconds:F1} s.");
        TestContext.Current.SendDiagnosticMessage($"The first write of {symbols.Count} symbols and {referenceTotal} references took {first.TotalSeconds:F1} s, the replacement {second.TotalSeconds:F1} s.");
    }

    private async Task<(ServiceProvider Services, int SourceId, IndexRun Run)> IndexSampleAsync()
    {
        ServiceProvider services = RequireDatabase();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        WriteSample();
        int sourceId = await services.GetRequiredService<SourceStore>().AddAsync(
            new SourceInput { Name = "demo", Kind = SourceKind.Code, RootPath = _root, BuildProperties = new Dictionary<string, string> { ["Configuration"] = "Release" } }, cancellationToken);
        IndexRun run = (await services.GetRequiredService<Indexer>().RunAsync(sourceId, null, cancellationToken))!;
        return (services, sourceId, run);
    }

    private void WriteSample()
    {
        foreach ((string path, string text) in SampleGraph.Files)
            Write(path, text);
    }

    private ServiceProvider RequireDatabase()
    {
        Assert.SkipWhen(_services is null, $"Set {VariableName} to run the tests of the database.");
        return _services!;
    }

    private void Write(string path, string text)
    {
        string full = Path.Combine(_root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
    }

    private sealed class DocumentStoreHelper(ServiceProvider services)
    {
        public async Task<long> IdAsync(string path, CancellationToken cancellationToken)
        {
            await using NpgsqlConnection connection = await services.GetRequiredService<Database>().OpenAsync(cancellationToken);
            return await connection.ExecuteScalarAsync<long>(new CommandDefinition("SELECT document_id FROM document WHERE path = @Path", new { Path = path }, cancellationToken: cancellationToken));
        }
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
