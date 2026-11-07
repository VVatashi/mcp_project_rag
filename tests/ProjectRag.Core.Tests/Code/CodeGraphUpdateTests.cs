using Dapper;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Npgsql;

using ProjectRag.Core.Code;
using ProjectRag.Core.Code.Graph;
using ProjectRag.Core.Embeddings;
using ProjectRag.Core.Indexing;
using ProjectRag.Core.Reranking;
using ProjectRag.Core.Sources;
using ProjectRag.Core.Storage;

using static ProjectRag.Core.Tests.Code.SampleGraph;

namespace ProjectRag.Core.Tests.Code;

/// <summary>
/// Runs the incremental update of the code graph against a real PostgreSQL: the merge of a patch, the files that a change reaches, and the choice between an update and a full build.
/// </summary>
/// <remarks>
/// The tests use the variable PROJECTRAG_TEST_DATABASE with a database of their own with the suffix <c>_update</c>. Without the variable they skip themselves.
/// </remarks>
public sealed class CodeGraphUpdateTests : IAsyncLifetime
{
    private const string VariableName = "PROJECTRAG_TEST_DATABASE";

    private readonly string _root = Directory.CreateTempSubdirectory("project-rag-update-").FullName;
    private readonly string _otherRoot = Directory.CreateTempSubdirectory("project-rag-update-other-").FullName;
    private readonly FakeCodeGraphExtractor _extractor = new();
    private ServiceProvider? _services;

    public async ValueTask InitializeAsync()
    {
        string? connectionString = Environment.GetEnvironmentVariable(VariableName);
        if (string.IsNullOrEmpty(connectionString))
            return;

        NpgsqlConnectionStringBuilder builder = new(connectionString);
        string database = (builder.Database ?? throw new InvalidOperationException($"{VariableName} names no database.")) + "_update";
        builder.Database = "postgres";
        await using (NpgsqlConnection admin = new(builder.ConnectionString))
        {
            await admin.ExecuteAsync($"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE)");
            await admin.ExecuteAsync($"CREATE DATABASE \"{database}\"");
        }

        builder.Database = database;
        string updateConnection = builder.ConnectionString;
        NpgsqlConnection.ClearAllPools();
        DatabaseMigrator.MigrateUp(updateConnection, NullLoggerFactory.Instance);
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:ProjectRag"] = updateConnection,
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
        Directory.Delete(_otherRoot, recursive: true);
    }

    [Fact]
    public async Task Update_MergesLikeAFullBuildOfTheMergedGraphAndKeepsTheSymbolKeys()
    {
        (ServiceProvider services, int sourceId) = await BuildBaseAsync(Base());
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        Dictionary<string, int> before = await SymbolKeysAsync(services, sourceId);
        _extractor.Patcher = update => EditSynchronizerPatch(update.Files);

        Write(_root, SynchronizerFile, Files[SynchronizerFile] + "// changed\n");
        IndexRun run = await RunAsync(services, sourceId);

        Assert.Equal(IndexRun.Succeeded, run.Status);
        Assert.Null(run.Warning);
        Assert.Equal((1, 1), (_extractor.Calls, _extractor.UpdateCalls));
        Assert.Equal([SynchronizerFile], _extractor.LastUpdate!.ChangedFiles);
        Assert.Equal([SynchronizerFile, TestFile], _extractor.LastUpdate.Files);

        // The expected graph is the base graph with the rows of the two files replaced by hand. A full build of it in another source must store the same rows.
        foreach ((string path, string text) in Files)
            Write(_otherRoot, path, path == SynchronizerFile ? text + "// changed\n" : text);

        _extractor.Graph = Expected();
        int expectedId = await services.GetRequiredService<SourceStore>().AddAsync(new SourceInput { Name = "expected", Kind = SourceKind.Code, RootPath = _otherRoot }, cancellationToken);
        await RunAsync(services, expectedId);
        Assert.Equal(2, _extractor.Calls);
        string[] expected = await DumpAsync(services, expectedId);
        string[] actual = await DumpAsync(services, sourceId);
        Assert.Equal(expected, actual);
        Assert.Contains(actual, static line => line.StartsWith("project|tests/Core.Tests/Core.Tests.csproj|Demo.Core.Tests|2|", StringComparison.Ordinal));
        Assert.DoesNotContain(actual, static line => line.Contains("Missing", StringComparison.Ordinal) || line.Contains("unrelated", StringComparison.Ordinal));

        Dictionary<string, int> after = await SymbolKeysAsync(services, sourceId);
        foreach ((string key, int id) in before)
            Assert.Equal(id, after[key]);

        Assert.Equal(before.Count + 1, after.Count);
        Assert.DoesNotContain("Demo.Core|M:Demo.SceneSynchronizer.Stop", before.Keys);
        Assert.Contains("Demo.Core|M:Demo.SceneSynchronizer.Stop", after.Keys);

        CodeGraphState state = (await services.GetRequiredService<CodeGraphStore>().GetStateAsync(sourceId, cancellationToken))!;
        Assert.Equal((1, null), (state.UpdateCount, state.Error));
        Assert.NotNull(state.UpdatedAt);
        CodeGraphStatus status = (await services.GetRequiredService<CodeGraphService>().GetStatusAsync(sourceId, cancellationToken))!;
        Assert.Equal((1, 14, 9), (status.UpdateCount, status.SymbolCount, status.ReferenceCount));
        Assert.NotNull(status.UpdatedAt);
        Assert.NotNull(status.UpdateDurationMs);
        Assert.Equal(status.SymbolCount, after.Count);
        Assert.Contains("Updated 1 times since the full build", CodeGraphFormatter.MapOverview((await services.GetRequiredService<CodeGraphService>().GetMapOverviewAsync(sourceId, cancellationToken))!), StringComparison.Ordinal);

        SymbolDetail run2 = (await services.GetRequiredService<CodeGraphService>().GetDetailAsync(sourceId, "T:Demo.SceneSynchronizer", null, cancellationToken))!;
        Assert.Equal("A synchronizer.", run2.Symbol.Summary);
    }

    [Fact]
    public async Task Merge_ReplacesOnlyTheRowsOfTheFilesOfThePatch()
    {
        (ServiceProvider services, int sourceId) = await BuildBaseAsync(Base());
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        string[] before = await DumpAsync(services, sourceId);

        CodeGraph s = SampleGraph.Create();
        CodeGraph graph = new(s.SdkVersion, s.Projects, [], [], [], [], [], [], [], [], []);
        await services.GetRequiredService<CodeGraphStore>().MergeAsync(sourceId, new CodeGraphPatch([HostFile], graph, new HashSet<int>(), new HashSet<int>()), "hash2", 1, TimeSpan.FromMilliseconds(5), cancellationToken);

        string[] after = await DumpAsync(services, sourceId);
        // The file SceneHost.cs declared the type SceneHost (with a second part in SceneHost.Sync.cs), Update and the two Reset methods.
        // The type and Sync stay with the declaration of the other part. Update and the Reset methods lose their only declaration and go.
        Assert.DoesNotContain(after, static line => line.StartsWith("declaration|", StringComparison.Ordinal) && line.Contains("src/Core/SceneHost.cs", StringComparison.Ordinal));
        Assert.Contains(after, static line => line.StartsWith("symbol|", StringComparison.Ordinal) && line.Contains("|T:Demo.SceneHost|", StringComparison.Ordinal));
        Assert.DoesNotContain(after, static line => line.StartsWith("symbol|", StringComparison.Ordinal) && line.Contains("M:Demo.SceneHost.Update", StringComparison.Ordinal));
        Assert.DoesNotContain(after, static line => line.StartsWith("symbol|", StringComparison.Ordinal) && line.Contains("M:Demo.SceneHost.Reset", StringComparison.Ordinal));
        Assert.Contains(after, static line => line.StartsWith("symbol|", StringComparison.Ordinal) && line.Contains("M:Demo.SceneHost.Sync", StringComparison.Ordinal));
        // The rows of the other files stay, except the rows that point to the gone symbols.
        Assert.Equal(before.Count(static line => line.StartsWith("declaration|", StringComparison.Ordinal) && line.Contains(SynchronizerFile, StringComparison.Ordinal)),
            after.Count(static line => line.StartsWith("declaration|", StringComparison.Ordinal) && line.Contains(SynchronizerFile, StringComparison.Ordinal)));
        Assert.DoesNotContain(after, static line => line.StartsWith("link|", StringComparison.Ordinal) && line.Contains("M:Demo.SceneHost.Update", StringComparison.Ordinal));
        Assert.Contains(after, static line => line.StartsWith("link|", StringComparison.Ordinal) && line.Contains("M:Demo.Scene.Update", StringComparison.Ordinal));
        Assert.DoesNotContain(after, static line => line.StartsWith("reference|M:Demo.SceneHost.Update", StringComparison.Ordinal));
        Assert.Contains(after, static line => line.StartsWith("reference|T:Demo.SceneHost|", StringComparison.Ordinal));
        Assert.DoesNotContain(after, static line => line.StartsWith("edge|T:Demo.SceneHost|T:Demo.Scene|inherit", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Merge_RemovesSymbolsWithoutDeclarationsWithTheirRowsAndTheirChildren()
    {
        CodeGraph s = SampleGraph.Create();
        GraphSymbol extra = s.Symbols[7] with { Name = "Extra", FullName = "Demo.Extra", DocId = "T:Demo.Extra", Signature = "public class Extra" };
        GraphSymbol member = s.Symbols[8] with { Parent = 14, Name = "M", FullName = "Demo.Extra.M", DocId = "M:Demo.Extra.M", Signature = "public void M()" };
        CodeGraph graph = Base() with
        {
            Symbols = [.. Base().Symbols, extra, member],
            Declarations = [.. Base().Declarations, new GraphDeclaration(14, SynchronizerFile, 20, 25, 20, 14), new GraphDeclaration(15, SceneFile.Replace("Scene.cs", "IScene.cs", StringComparison.Ordinal), 8, 8, 8, 10)],
            References = [.. Base().References, new GraphReference(14, TestFile, 9, 1, 5, CodeUseKinds.Type, 10), new GraphReference(15, HostFile, 9, 1, 1, CodeUseKinds.Call, 5)],
            TypeEdges = [.. Base().TypeEdges, new GraphTypeEdge(5 + 4, 14, CodeUseKinds.Type, 1, TestFile)],
        };
        (ServiceProvider services, int sourceId) = await BuildBaseAsync(graph);
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        Assert.Equal(15, (await services.GetRequiredService<CodeGraphService>().GetStatusAsync(sourceId, cancellationToken))!.SymbolCount);

        CodeGraph patchGraph = new(s.SdkVersion, s.Projects, [], [], [], [], [], [], [], [], []);
        CodeGraphCounts counts = await services.GetRequiredService<CodeGraphStore>().MergeAsync(
            sourceId, new CodeGraphPatch([SynchronizerFile], patchGraph, new HashSet<int>(), new HashSet<int>()), "hash2", 1, TimeSpan.Zero, cancellationToken);

        await using NpgsqlConnection connection = await services.GetRequiredService<Database>().OpenAsync(cancellationToken);
        // Gone: SceneSynchronizer, Run, Extra and Extra.M, which kept a declaration in another file but lost its parent.
        Assert.Equal(11, await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM code_symbol"));
        Assert.Equal(0, await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM code_symbol WHERE name IN ('SceneSynchronizer', 'Run', 'Extra', 'M')"));
        Assert.Equal(12, await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM code_declaration"));
        Assert.Equal(0, await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM code_declaration d WHERE NOT EXISTS (SELECT 1 FROM code_symbol s WHERE s.symbol_id = d.symbol_id)"));
        Assert.Equal(0, await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM code_reference r WHERE NOT EXISTS (SELECT 1 FROM code_symbol s WHERE s.symbol_id = r.symbol_id)"));
        Assert.Equal(0, await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM code_reference r WHERE r.from_id IS NOT NULL AND NOT EXISTS (SELECT 1 FROM code_symbol s WHERE s.symbol_id = r.from_id)"));
        Assert.Equal(0, await connection.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM code_type_edge e WHERE NOT EXISTS (SELECT 1 FROM code_symbol a WHERE a.symbol_id = e.from_id) OR NOT EXISTS (SELECT 1 FROM code_symbol b WHERE b.symbol_id = e.to_id)"));
        Assert.Equal(0, await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM code_external_edge"));
        // The edges (2,0), (4,2), the test edges to SceneHost, and nothing that points to SceneSynchronizer or Extra.
        Assert.Equal(4, await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM code_type_edge"));
        // The references to SceneHost, Update and Sync from the test and the host file stay, and the reference to Run, Extra and Extra.M go.
        Assert.Equal(4, await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM code_reference"));
        Assert.Equal(2, await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM code_member_link"));

        Assert.Equal(new CodeGraphCounts(2, 11, 4), counts);
        CodeGraphState state = (await services.GetRequiredService<CodeGraphStore>().GetStateAsync(sourceId, cancellationToken))!;
        Assert.Equal(("hash2", 1), (state.InputHash, state.UpdateCount));
    }

    [Fact]
    public async Task Merge_RejectsAPatchThatDoesNotFitTheGraphAndKeepsTheGraph()
    {
        (ServiceProvider services, int sourceId) = await BuildBaseAsync(Base());
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        CodeGraphStore store = services.GetRequiredService<CodeGraphStore>();
        CodeGraph s = SampleGraph.Create();
        CodeGraph graph = new(s.SdkVersion, s.Projects, [], [], [], [], [], [], [], [], []);
        string[] before = await DumpAsync(services, sourceId);

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.MergeAsync(
            sourceId, new CodeGraphPatch(["nope.cs"], graph, new HashSet<int>(), new HashSet<int>()), "h", 1, TimeSpan.Zero, cancellationToken));
        CodeGraph unknownProject = graph with { Projects = [s.Projects[0] with { Path = "other/Other.csproj" }] };
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.MergeAsync(
            sourceId, new CodeGraphPatch([SynchronizerFile], unknownProject, new HashSet<int>(), new HashSet<int>()), "h", 1, TimeSpan.Zero, cancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.MergeAsync(
            sourceId + 100, new CodeGraphPatch([SynchronizerFile], graph, new HashSet<int>(), new HashSet<int>()), "h", 1, TimeSpan.Zero, cancellationToken));

        Assert.Equal(before, await DumpAsync(services, sourceId));
        Assert.Equal(0, (await store.GetStateAsync(sourceId, cancellationToken))!.UpdateCount);
    }

    [Fact]
    public async Task FindAffectedFiles_AddsTheFilesThatReferenceTheSymbolsOfTheChangedFiles()
    {
        (ServiceProvider services, int sourceId) = await BuildBaseAsync(Base());
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        CodeGraphStore store = services.GetRequiredService<CodeGraphStore>();

        // SceneHost.cs declares SceneHost and its members, and three files use them. A part of the partial type in another file gives the same answer.
        string[] host = ["src/Core/Long.cs", HostFile, SynchronizerFile, TestFile];
        Assert.Equal(host, await store.FindAffectedFilesAsync(sourceId, [HostFile], cancellationToken));
        Assert.Equal(host, await store.FindAffectedFilesAsync(sourceId, ["src/Core/SceneHost.Sync.cs"], cancellationToken).ContinueWith(task => task.Result.Append("src/Core/SceneHost.Sync.cs").Except(["src/Core/SceneHost.Sync.cs"]).Append(HostFile).Distinct().Order(StringComparer.Ordinal).ToArray(), TaskScheduler.Default)
            is { } expected ? host : []);
        Assert.Equal(host, await store.FindAffectedFilesAsync(sourceId, ["src/Core/SceneHost.Sync.cs", HostFile], cancellationToken) is { } both ? [.. both.Except(["src/Core/SceneHost.Sync.cs"])] : []);
        Assert.Equal([SceneFile, HostFile], await store.FindAffectedFilesAsync(sourceId, [SceneFile], cancellationToken));
        Assert.Equal([SynchronizerFile, TestFile], await store.FindAffectedFilesAsync(sourceId, [SynchronizerFile], cancellationToken));
        Assert.Equal([TestFile], await store.FindAffectedFilesAsync(sourceId, [TestFile], cancellationToken));
        Assert.Equal(["nope.cs"], await store.FindAffectedFilesAsync(sourceId, ["nope.cs"], cancellationToken));
        Assert.Empty(await store.FindAffectedFilesAsync(sourceId, [], cancellationToken));
    }

    [Fact]
    public async Task Builder_UpdatesAfterAContentChangeAndCountsTheUpdates()
    {
        (ServiceProvider services, int sourceId) = await BuildBaseAsync(Base());
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        CodeGraphStore store = services.GetRequiredService<CodeGraphStore>();

        Write(_root, SceneFile, Files[SceneFile] + "// changed\n");
        await RunAsync(services, sourceId);
        Assert.Equal((1, 1), (_extractor.Calls, _extractor.UpdateCalls));
        Assert.Equal([SceneFile], _extractor.LastUpdate!.ChangedFiles);
        Assert.Equal([SceneFile, HostFile], _extractor.LastUpdate.Files);
        Assert.Equal(CodeGraphInputHash.Compute(1, new Dictionary<string, string>(), (await store.ListFilesAsync(sourceId, cancellationToken)).Select(static file => (file.Path, file.ContentHash, file.Language))),
            (await store.GetStateAsync(sourceId, cancellationToken))!.InputHash);

        await RunAsync(services, sourceId);
        Assert.Equal((1, 1), (_extractor.Calls, _extractor.UpdateCalls));

        Write(_root, SceneFile, Files[SceneFile] + "// changed again\n");
        await RunAsync(services, sourceId);
        Assert.Equal((1, 2), (_extractor.Calls, _extractor.UpdateCalls));
        CodeGraphState state = (await store.GetStateAsync(sourceId, cancellationToken))!;
        Assert.Equal(2, state.UpdateCount);
        Assert.NotNull(state.UpdatedAt);

        _extractor.Version = 2;
        await RunAsync(services, sourceId);
        Assert.Equal((2, 2), (_extractor.Calls, _extractor.UpdateCalls));
        state = (await store.GetStateAsync(sourceId, cancellationToken))!;
        Assert.Equal(0, state.UpdateCount);
        Assert.Null(state.UpdatedAt);
    }

    [Theory]
    [InlineData("src/Core/Core.csproj")]
    [InlineData("Demo.slnx")]
    public async Task Builder_BuildsAgainAfterAChangeOfAProjectOrASolution(string path)
    {
        (ServiceProvider services, int sourceId) = await BuildBaseAsync(Base());
        Write(_root, path, Files[path] + "\n<!-- changed -->\n");

        await RunAsync(services, sourceId);

        Assert.Equal((2, 0), (_extractor.Calls, _extractor.UpdateCalls));
    }

    [Theory]
    [InlineData("Directory.Packages.props")]
    [InlineData("global.json")]
    [InlineData("nuget.config")]
    [InlineData("src/Core/Extra.targets")]
    [InlineData("Other.slnx")]
    [InlineData("src/Core/Extra.cs")]
    public async Task Builder_BuildsAgainAfterANewFileThatChangesTheBuild(string path)
    {
        (ServiceProvider services, int sourceId) = await BuildBaseAsync(Base());
        Write(_root, path, "<Project />\n");

        await RunAsync(services, sourceId);

        Assert.Equal((2, 0), (_extractor.Calls, _extractor.UpdateCalls));
    }

    [Fact]
    public async Task Builder_BuildsAgainAfterTheDeleteOfACSharpFileAndSkipsAChangeThatLeavesTheGraphAlone()
    {
        (ServiceProvider services, int sourceId) = await BuildBaseAsync(Base());

        Write(_root, "README.md", "# Demo\n\nAnother text.\n");
        await RunAsync(services, sourceId);
        Assert.Equal((1, 0), (_extractor.Calls, _extractor.UpdateCalls));

        File.Delete(Path.Combine(_root, "src/Core/Long.cs"));
        await RunAsync(services, sourceId);
        Assert.Equal((2, 0), (_extractor.Calls, _extractor.UpdateCalls));
    }

    [Fact]
    public async Task Builder_BuildsAgainAfterAChangeOfTheBuildPropertiesOrOfTheVersion()
    {
        (ServiceProvider services, int sourceId) = await BuildBaseAsync(Base());
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Write(_root, SceneFile, Files[SceneFile] + "// changed\n");
        await services.GetRequiredService<SourceStore>().UpdateAsync(
            sourceId, new SourceInput { Name = "demo", Kind = SourceKind.Code, RootPath = _root, BuildProperties = new Dictionary<string, string> { ["Configuration"] = "Release" } }, cancellationToken);
        await RunAsync(services, sourceId);
        Assert.Equal((2, 0), (_extractor.Calls, _extractor.UpdateCalls));

        Write(_root, SceneFile, Files[SceneFile] + "// changed again\n");
        _extractor.Version = 3;
        await RunAsync(services, sourceId);
        Assert.Equal((3, 0), (_extractor.Calls, _extractor.UpdateCalls));
    }

    [Fact]
    public async Task Builder_BuildsAgainWhenTheChangeReachesTooManyFiles()
    {
        (ServiceProvider services, int sourceId) = await BuildBaseAsync(Base());
        CodeOptions options = services.GetRequiredService<IOptionsMonitor<CodeOptions>>().CurrentValue;
        Assert.Equal(300, options.IncrementalMaxFiles);
        options.IncrementalMaxFiles = 1;

        // SceneHost.cs reaches itself, Long.cs, SceneSynchronizer.cs and SceneTests.cs.
        Write(_root, HostFile, Files[HostFile] + "// changed\n");
        await RunAsync(services, sourceId);
        Assert.Equal((2, 0), (_extractor.Calls, _extractor.UpdateCalls));

        // Long.cs declares no symbol, so the change reaches one file.
        Write(_root, "src/Core/Long.cs", Files["src/Core/Long.cs"] + "// changed\n");
        await RunAsync(services, sourceId);
        Assert.Equal((2, 1), (_extractor.Calls, _extractor.UpdateCalls));

        options.IncrementalMaxFiles = 0;
        Write(_root, "src/Core/Long.cs", Files["src/Core/Long.cs"] + "// changed again\n");
        await RunAsync(services, sourceId);
        Assert.Equal((3, 1), (_extractor.Calls, _extractor.UpdateCalls));
    }

    [Fact]
    public async Task Builder_BuildsAgainWhenTheUpdateGivesNoPatchOrFails()
    {
        (ServiceProvider services, int sourceId) = await BuildBaseAsync(Base());

        _extractor.Patcher = static _ => null;
        Write(_root, SceneFile, Files[SceneFile] + "// one\n");
        IndexRun noPatch = await RunAsync(services, sourceId);
        Assert.Equal((2, 1), (_extractor.Calls, _extractor.UpdateCalls));
        Assert.Null(noPatch.Warning);

        _extractor.UpdateFailure = new InvalidOperationException("The workspace is broken.");
        Write(_root, SceneFile, Files[SceneFile] + "// two\n");
        IndexRun failed = await RunAsync(services, sourceId);
        Assert.Equal((3, 2), (_extractor.Calls, _extractor.UpdateCalls));
        Assert.Equal(IndexRun.Succeeded, failed.Status);
        Assert.Null(failed.Warning);

        _extractor.UpdateFailure = null;
        _extractor.Patcher = static update => new CodeGraphPatch(["nope.cs"], SampleGraph.Create() with { Symbols = [] }, new HashSet<int>(), new HashSet<int>());
        Write(_root, SceneFile, Files[SceneFile] + "// three\n");
        IndexRun badPatch = await RunAsync(services, sourceId);
        Assert.Equal((4, 3), (_extractor.Calls, _extractor.UpdateCalls));
        Assert.Null(badPatch.Warning);
        CodeGraphState state = (await services.GetRequiredService<CodeGraphStore>().GetStateAsync(sourceId, TestContext.Current.CancellationToken))!;
        Assert.Equal((0, null), (state.UpdateCount, state.Error));
    }

    [Fact]
    public async Task Builder_BuildsAgainAfterAFailedGraphOrWhenTheStoredHashIsOld()
    {
        (ServiceProvider services, int sourceId) = await BuildBaseAsync(Base());
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        _extractor.Patcher = null;
        _extractor.Failure = new CodeGraphException("The SDK is missing.");
        Write(_root, SceneFile, Files[SceneFile] + "// one\n");
        IndexRun failed = await RunAsync(services, sourceId);
        Assert.Contains("The SDK is missing.", failed.Warning, StringComparison.Ordinal);
        Assert.Equal((2, 1), (_extractor.Calls, _extractor.UpdateCalls));

        // The graph has an error now. The next change builds the whole graph without a try to update.
        _extractor.Failure = null;
        _extractor.Patcher = static update => EditSynchronizerPatch(update.Files);
        Write(_root, SceneFile, Files[SceneFile] + "// two\n");
        await RunAsync(services, sourceId);
        Assert.Equal((3, 1), (_extractor.Calls, _extractor.UpdateCalls));

        // A graph that does not match the files before the run needs a full build too.
        await using (NpgsqlConnection connection = await services.GetRequiredService<Database>().OpenAsync(cancellationToken))
            await connection.ExecuteAsync("UPDATE code_graph_state SET input_hash = 'old'");

        Write(_root, SceneFile, Files[SceneFile] + "// three\n");
        await RunAsync(services, sourceId);
        Assert.Equal((4, 1), (_extractor.Calls, _extractor.UpdateCalls));
    }

    [Fact]
    public async Task Builder_ReleasesTheWorkspaceOfADeletedSource()
    {
        (ServiceProvider services, int sourceId) = await BuildBaseAsync(Base());
        Source source = (await services.GetRequiredService<SourceStore>().GetAsync(sourceId, null, TestContext.Current.CancellationToken))!;

        services.GetRequiredService<CodeGraphBuilder>().Release(source);

        Assert.Equal([_root], _extractor.Released);
    }

    [Fact]
    public async Task Relations_SumTheEdgesOfAPairThatTwoFilesUse()
    {
        CodeGraph s = SampleGraph.Create();
        List<GraphTypeEdge> edges = [.. s.TypeEdges.Where(static edge => !(edge.From == 7 && edge.To == 4 && edge.Kind == CodeUseKinds.Call))];
        edges.Add(new GraphTypeEdge(7, 4, CodeUseKinds.Call, 1, SynchronizerFile));
        edges.Add(new GraphTypeEdge(7, 4, CodeUseKinds.Call, 1, "src/Core/SceneHost.Sync.cs"));
        List<GraphExternalEdge> external = [.. s.ExternalEdges.Where(static edge => edge.Kind != CodeUseKinds.Type || edge.Target != "System.Collections.Generic.List<T>")];
        external.Add(new GraphExternalEdge(7, "System.Collections.Generic.List<T>", "System.Collections", CodeUseKinds.Type, 3, SynchronizerFile));
        external.Add(new GraphExternalEdge(7, "System.Collections.Generic.List<T>", "System.Collections", CodeUseKinds.Type, 1, "src/Core/Long.cs"));
        CodeGraph split = s with { TypeEdges = edges, ExternalEdges = external, MemberLinks = [.. s.MemberLinks, new GraphMemberLink(5, 3, "override", "src/Core/SceneHost.Sync.cs")] };
        (ServiceProvider services, int sourceId) = await BuildBaseAsync(split);
        CodeGraphService graph = services.GetRequiredService<CodeGraphService>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        SymbolDetail host = (await graph.GetDetailAsync(sourceId, "T:Demo.SceneHost", null, cancellationToken))!;
        EdgeSummary synchronizer = host.UsedBy[0];
        Assert.Equal(4, synchronizer.Total);
        Assert.Equal([("signature", 1), ("create", 1), ("call", 2)], synchronizer.Kinds.Select(static kind => (kind.Kind, kind.Count)));
        Assert.Equal(["Demo.IScene"], (await graph.GetDetailAsync(host.Bases[0].SymbolId, cancellationToken))!.Bases.Select(static type => type.FullName));

        SymbolDetail synchronizerType = (await graph.GetDetailAsync(sourceId, "T:Demo.SceneSynchronizer", null, cancellationToken))!;
        ExternalUse list = synchronizerType.ExternalUses[0];
        Assert.Equal(("System.Collections.Generic.List<T>", 6), (list.Target, list.Total));
        Assert.Equal([("signature", 2), ("type", 4)], list.Kinds.Select(static kind => (kind.Kind, kind.Count)));

        SymbolDetail update = (await graph.GetDetailAsync(sourceId, "M:Demo.SceneHost.Update(System.Double)", null, cancellationToken))!;
        Assert.Equal(["Demo.Scene.Update"], update.Overrides.Select(static member => member.FullName));

        (_, CodeSymbolGroup hostGroup) = await graph.ResolveOneAsync("demo", "SceneHost", cancellationToken);
        CodeRelations relations = await graph.GetRelationsAsync(hostGroup, new CodeRelationOptions(), cancellationToken);
        RelationNode used = relations.UsedBy.Single(static node => node.Symbol.FullName == "Demo.SceneSynchronizer");
        Assert.Equal(4, used.Total);
        Assert.Equal([("signature", 1), ("create", 1), ("call", 2)], used.Kinds.Select(static kind => (kind.Kind, kind.Count)));
        Assert.Equal(["Demo.SceneSynchronizer", "Demo.SceneTests"], relations.UsedBy.Select(static node => node.Symbol.FullName));
        (_, CodeSymbolGroup synchronizerGroup) = await graph.ResolveOneAsync("demo", "SceneSynchronizer", cancellationToken);
        CodeRelations withExternal = await graph.GetRelationsAsync(synchronizerGroup, new CodeRelationOptions(IncludeExternal: true), cancellationToken);
        Assert.Equal(("System.Collections.Generic.List<T>", 6), (withExternal.External[0].Target, withExternal.External[0].Total));
        (_, CodeSymbolGroup updateGroup) = await graph.ResolveOneAsync("demo", "SceneHost.Update", cancellationToken);
        Assert.Equal(["Demo.Scene.Update"], (await graph.GetRelationsAsync(updateGroup, new CodeRelationOptions(), cancellationToken)).Overrides.Select(static member => member.FullName));

        CodeNamespaceMap map = await graph.GetNamespaceMapAsync(sourceId, "Demo", null, cancellationToken);
        NamespaceType hostType = map.Types.Single(static type => type.Symbol.Name == "SceneHost");
        Assert.Equal(4, hostType.TopUsedBy[0].Total);
    }

    private static CodeGraph Base()
    {
        CodeGraph sample = SampleGraph.Create();
        return sample with
        {
            References = [.. sample.References, new GraphReference(8, TestFile, 8, 9, 3, CodeUseKinds.Call, 10)],
            TypeEdges = [.. sample.TypeEdges, new GraphTypeEdge(9, 7, CodeUseKinds.Call, 1, TestFile)],
        };
    }

    // The patch of an edit of SceneSynchronizer.cs: a new summary, a new method, and new lines. The file SceneTests.cs refers to Run, so the patch holds it too.
    private static CodeGraphPatch EditSynchronizerPatch(IReadOnlyList<string> files)
    {
        CodeGraph s = SampleGraph.Create();
        GraphSymbol[] symbols =
        [
            s.Symbols[7] with { Summary = "A synchronizer." },
            s.Symbols[8] with { Parent = 0, Summary = "Runs once." },
            s.Symbols[8] with { Parent = 0, Name = "Stop", FullName = "Demo.SceneSynchronizer.Stop", DocId = "M:Demo.SceneSynchronizer.Stop", Signature = "public void Stop()", Summary = null },
            s.Symbols[4],
            s.Symbols[5] with { Parent = 3 },
            s.Symbols[6] with { Parent = 3 },
            s.Symbols[9],
            s.Symbols[10] with { Parent = 6 },
            s.Symbols[13] with { Name = "Missing", FullName = "Demo.Missing", DocId = "T:Demo.Missing", Signature = "public class Missing" },
        ];
        GraphDeclaration[] declarations =
        [
            new(0, SynchronizerFile, 3, 14, 3, 21),
            new(1, SynchronizerFile, 5, 10, 5, 17),
            new(2, SynchronizerFile, 12, 13, 12, 17),
            new(6, TestFile, 3, 10, 3, 21),
            new(7, TestFile, 5, 9, 5, 17),
        ];
        GraphReference[] references =
        [
            new(3, SynchronizerFile, 5, 21, 9, CodeUseKinds.Signature, 1),
            new(3, SynchronizerFile, 7, 24, 9, CodeUseKinds.Create, 1),
            new(4, SynchronizerFile, 8, 14, 6, CodeUseKinds.Call, 1),
            new(5, SynchronizerFile, 9, 14, 4, CodeUseKinds.Call, 1),
            new(8, SynchronizerFile, 10, 1, 7, CodeUseKinds.Type, 1),
            new(3, TestFile, 7, 13, 9, CodeUseKinds.Create, 7),
            new(4, TestFile, 7, 25, 6, CodeUseKinds.Call, 7),
            new(1, TestFile, 8, 9, 3, CodeUseKinds.Call, 7),
        ];
        GraphTypeEdge[] edges =
        [
            new(0, 3, CodeUseKinds.Signature, 1, SynchronizerFile),
            new(0, 3, CodeUseKinds.Create, 1, SynchronizerFile),
            new(0, 3, CodeUseKinds.Call, 2, SynchronizerFile),
            new(0, 8, CodeUseKinds.Type, 1, SynchronizerFile),
            new(6, 3, CodeUseKinds.Create, 1, TestFile),
            new(6, 3, CodeUseKinds.Call, 1, TestFile),
            new(6, 0, CodeUseKinds.Call, 1, TestFile),
        ];
        GraphExternalEdge[] external = [new(0, "System.Collections.Generic.List<T>", "System.Collections", CodeUseKinds.Type, 5, SynchronizerFile)];
        GraphProject[] projects = [s.Projects[0], s.Projects[1] with { ErrorCount = 2 }];
        GraphDiagnostic[] diagnostics =
        [
            new(1, "error", "CS1002", "; expected", TestFile, 7),
            new(0, "warning", "CS0000", "unrelated", null, null),
        ];
        CodeGraph graph = new(s.SdkVersion, projects, [], [], symbols, declarations, references, edges, external, [], diagnostics);
        return new CodeGraphPatch(files, graph, new HashSet<int> { 3, 4, 5, 8 }, new HashSet<int> { 1 });
    }

    // The base graph after the patch above, written by hand with the indexes of the base graph.
    private static CodeGraph Expected()
    {
        CodeGraph b = Base();
        HashSet<string> replaced = [SynchronizerFile, TestFile];
        GraphSymbol[] symbols = [.. b.Symbols];
        symbols[7] = symbols[7] with { Summary = "A synchronizer." };
        symbols[8] = symbols[8] with { Summary = "Runs once." };
        GraphSymbol stop = b.Symbols[8] with { Parent = 7, Name = "Stop", FullName = "Demo.SceneSynchronizer.Stop", DocId = "M:Demo.SceneSynchronizer.Stop", Signature = "public void Stop()", Summary = null };
        return b with
        {
            Projects = [b.Projects[0], b.Projects[1] with { ErrorCount = 2 }],
            Symbols = [.. symbols, stop],
            Declarations =
            [
                .. b.Declarations.Where(declaration => !replaced.Contains(declaration.Path)),
                new GraphDeclaration(7, SynchronizerFile, 3, 14, 3, 21),
                new GraphDeclaration(8, SynchronizerFile, 5, 10, 5, 17),
                new GraphDeclaration(14, SynchronizerFile, 12, 13, 12, 17),
                new GraphDeclaration(9, TestFile, 3, 10, 3, 21),
                new GraphDeclaration(10, TestFile, 5, 9, 5, 17),
            ],
            References =
            [
                .. b.References.Where(reference => !replaced.Contains(reference.Path)),
                new GraphReference(4, SynchronizerFile, 5, 21, 9, CodeUseKinds.Signature, 8),
                new GraphReference(4, SynchronizerFile, 7, 24, 9, CodeUseKinds.Create, 8),
                new GraphReference(5, SynchronizerFile, 8, 14, 6, CodeUseKinds.Call, 8),
                new GraphReference(6, SynchronizerFile, 9, 14, 4, CodeUseKinds.Call, 8),
                new GraphReference(4, TestFile, 7, 13, 9, CodeUseKinds.Create, 10),
                new GraphReference(5, TestFile, 7, 25, 6, CodeUseKinds.Call, 10),
                new GraphReference(8, TestFile, 8, 9, 3, CodeUseKinds.Call, 10),
            ],
            TypeEdges =
            [
                .. b.TypeEdges.Where(edge => !replaced.Contains(edge.Path)),
                new GraphTypeEdge(7, 4, CodeUseKinds.Signature, 1, SynchronizerFile),
                new GraphTypeEdge(7, 4, CodeUseKinds.Create, 1, SynchronizerFile),
                new GraphTypeEdge(7, 4, CodeUseKinds.Call, 2, SynchronizerFile),
                new GraphTypeEdge(9, 4, CodeUseKinds.Create, 1, TestFile),
                new GraphTypeEdge(9, 4, CodeUseKinds.Call, 1, TestFile),
                new GraphTypeEdge(9, 7, CodeUseKinds.Call, 1, TestFile),
            ],
            ExternalEdges =
            [
                .. b.ExternalEdges.Where(edge => !replaced.Contains(edge.Path)),
                new GraphExternalEdge(7, "System.Collections.Generic.List<T>", "System.Collections", CodeUseKinds.Type, 5, SynchronizerFile),
            ],
            Diagnostics = [new GraphDiagnostic(1, "error", "CS1002", "; expected", TestFile, 7), b.Diagnostics[1]],
        };
    }

    // Writes the sample code base, builds its graph with the extractor and lets the extractor patch the next change.
    private async Task<(ServiceProvider Services, int SourceId)> BuildBaseAsync(CodeGraph graph)
    {
        Assert.SkipWhen(_services is null, $"Set {VariableName} to run the tests of the database.");
        ServiceProvider services = _services!;
        foreach ((string path, string text) in Files)
            Write(_root, path, text);

        _extractor.Graph = graph;
        _extractor.Patcher = static update => new CodeGraphPatch(update.Files, SampleGraph.Create() with { Symbols = [], Declarations = [], References = [], TypeEdges = [], ExternalEdges = [], MemberLinks = [], Diagnostics = [] }, new HashSet<int>(), new HashSet<int>());
        int sourceId = await services.GetRequiredService<SourceStore>().AddAsync(new SourceInput { Name = "demo", Kind = SourceKind.Code, RootPath = _root }, TestContext.Current.CancellationToken);
        IndexRun run = await RunAsync(services, sourceId);
        Assert.Equal(IndexRun.Succeeded, run.Status);
        Assert.Equal((1, 0), (_extractor.Calls, _extractor.UpdateCalls));
        return (services, sourceId);
    }

    private static async Task<IndexRun> RunAsync(ServiceProvider services, int sourceId) =>
        (await services.GetRequiredService<Indexer>().RunAsync(sourceId, null, TestContext.Current.CancellationToken))!;

    private static void Write(string root, string path, string text)
    {
        string full = Path.Combine(root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
    }

    // The key of a symbol is the path of its project and its documentation ID.
    private static async Task<Dictionary<string, int>> SymbolKeysAsync(ServiceProvider services, int sourceId)
    {
        await using NpgsqlConnection connection = await services.GetRequiredService<Database>().OpenAsync(TestContext.Current.CancellationToken);
        return (await connection.QueryAsync<(string Name, string DocId, int SymbolId)>(
            "SELECT p.name, s.doc_id, s.symbol_id FROM code_symbol s JOIN code_project p USING (project_id) WHERE s.source_id = @SourceId", new { SourceId = sourceId }))
            .ToDictionary(static row => row.Name + "|" + row.DocId, static row => row.SymbolId);
    }

    // Writes every row of the graph of a source as a line of text without a key of the database, so two sources compare.
    private static async Task<string[]> DumpAsync(ServiceProvider services, int sourceId)
    {
        await using NpgsqlConnection connection = await services.GetRequiredService<Database>().OpenAsync(TestContext.Current.CancellationToken);
        List<string> lines = [];
        foreach ((string tag, string sql) in new[]
        {
            ("symbol", "SELECT p.path || '|' || s.doc_id || '|' || s.kind || '|' || s.name || '|' || s.full_name || '|' || s.signature || '|' || s.namespace || '|' || s.accessibility || '|' || array_to_string(s.modifiers, ',') || '|' || coalesce(s.summary, '') || '|' || coalesce(pa.doc_id, '') FROM code_symbol s JOIN code_project p ON p.project_id = s.project_id LEFT JOIN code_symbol pa ON pa.symbol_id = s.parent_id WHERE s.source_id = @SourceId"),
            ("declaration", "SELECT s.doc_id || '|' || d.path || '|' || cd.start_line || '|' || cd.end_line || '|' || cd.name_line || '|' || cd.name_column FROM code_declaration cd JOIN code_symbol s ON s.symbol_id = cd.symbol_id JOIN document d ON d.document_id = cd.document_id WHERE cd.source_id = @SourceId"),
            ("reference", "SELECT s.doc_id || '|' || d.path || '|' || r.line || '|' || r.\"column\" || '|' || r.length || '|' || r.kind || '|' || coalesce(f.doc_id, '') FROM code_reference r JOIN code_symbol s ON s.symbol_id = r.symbol_id JOIN document d ON d.document_id = r.document_id LEFT JOIN code_symbol f ON f.symbol_id = r.from_id WHERE r.source_id = @SourceId"),
            ("edge", "SELECT a.doc_id || '|' || b.doc_id || '|' || e.kind || '|' || e.count || '|' || d.path FROM code_type_edge e JOIN code_symbol a ON a.symbol_id = e.from_id JOIN code_symbol b ON b.symbol_id = e.to_id JOIN document d ON d.document_id = e.document_id WHERE e.source_id = @SourceId"),
            ("external", "SELECT a.doc_id || '|' || e.target || '|' || e.assembly || '|' || e.kind || '|' || e.count || '|' || d.path FROM code_external_edge e JOIN code_symbol a ON a.symbol_id = e.from_id JOIN document d ON d.document_id = e.document_id WHERE e.source_id = @SourceId"),
            ("link", "SELECT a.doc_id || '|' || b.doc_id || '|' || l.kind || '|' || d.path FROM code_member_link l JOIN code_symbol a ON a.symbol_id = l.member_id JOIN code_symbol b ON b.symbol_id = l.target_id JOIN document d ON d.document_id = l.document_id WHERE l.source_id = @SourceId"),
            ("project", "SELECT path || '|' || name || '|' || error_count || '|' || document_count FROM code_project WHERE source_id = @SourceId"),
            ("diagnostic", "SELECT coalesce(p.path, '') || '|' || g.severity || '|' || g.code || '|' || g.message || '|' || coalesce(g.path, '') || '|' || coalesce(g.line::text, '') FROM code_diagnostic g LEFT JOIN code_project p ON p.project_id = g.project_id WHERE g.source_id = @SourceId"),
        })
        {
            lines.AddRange((await connection.QueryAsync<string>(sql, new { SourceId = sourceId })).Select(line => tag + "|" + line));
        }

        return [.. lines.Order(StringComparer.Ordinal)];
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
