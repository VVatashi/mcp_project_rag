using Dapper;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Npgsql;

using ProjectRag.Core.Code;
using ProjectRag.Core.Code.Chunks;
using ProjectRag.Core.Code.Graph;
using ProjectRag.Core.Embeddings;
using ProjectRag.Core.Indexing;
using ProjectRag.Core.Reranking;
using ProjectRag.Core.Search;
using ProjectRag.Core.Sources;
using ProjectRag.Core.Storage;

namespace ProjectRag.Core.Tests.Code;

/// <summary>
/// Runs the chunks of code files and the search in tiers against a real PostgreSQL with pgvector. The models and the graph are fakes.
/// </summary>
/// <remarks>
/// The tests use the variable PROJECTRAG_TEST_DATABASE like the other tests of the database, but a database of their own with the suffix <c>_tiers</c>,
/// so they can run next to those tests. Without the variable they skip themselves.
/// </remarks>
public sealed class CodeTierSearchTests : IAsyncLifetime
{
    private const string VariableName = "PROJECTRAG_TEST_DATABASE";

    private readonly string _root = Directory.CreateTempSubdirectory("project-rag-tiers-").FullName;
    private readonly FakeCodeGraphExtractor _extractor = new();
    private readonly BagEmbeddingClient _embedder = new();
    private readonly BlockChunker _blocks = new();
    private ServiceProvider? _services;

    public async ValueTask InitializeAsync()
    {
        string? connectionString = Environment.GetEnvironmentVariable(VariableName);
        if (string.IsNullOrEmpty(connectionString))
            return;

        NpgsqlConnectionStringBuilder builder = new(connectionString);
        string database = (builder.Database ?? throw new InvalidOperationException($"{VariableName} names no database.")) + "_tiers";
        builder.Database = "postgres";
        await using (NpgsqlConnection admin = new(builder.ConnectionString))
        {
            await admin.ExecuteAsync($"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE)");
            await admin.ExecuteAsync($"CREATE DATABASE \"{database}\"");
        }

        builder.Database = database;
        string tiersConnection = builder.ConnectionString;
        NpgsqlConnection.ClearAllPools();
        DatabaseMigrator.MigrateUp(tiersConnection, NullLoggerFactory.Instance);
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:ProjectRag"] = tiersConnection,
            ["Embedding:Model"] = "fake",
            ["Rerank:Candidates"] = "10",
            ["CodeChunking:MaxChars"] = "100",
            ["CodeChunking:MinChars"] = "0",
        }).Build();

        ServiceCollection services = new();
        services.AddLogging();
        services.AddKnowledgeBase(configuration);
        services.AddSingleton<IPageClient, NoPageClient>();
        services.AddSingleton<IEmbeddingClient>(_embedder);
        services.AddSingleton<IRerankClient, TokenRerankClient>();
        services.AddSingleton<ICodeGraphExtractor>(_extractor);
        services.AddSingleton<ICodeChunker>(_blocks);
        _services = services.BuildServiceProvider();
    }

    public async ValueTask DisposeAsync()
    {
        if (_services is not null)
            await _services.DisposeAsync();

        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Indexer_ChunksTheFilesWithKindsLinesAndTokens()
    {
        ServiceProvider services = RequireDatabase();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        const string scene = "namespace Demo;\n\npublic sealed class SceneSynchronizer\n{\n    public void Sync()\n    {\n    }\n}\n\npublic sealed class Cache\n{\n    private int _softGPU;\n}\n";
        const string blur = "#version 450\n\nvoid main()\n{\n    gl_Position = vec4(0.0);\n}\n\n// The second block is longer than the limit of the chunks and needs a split by lines.\nvec3 blur(vec3 color)\n{\n    return color * 0.5;\n}\n";
        Write("src/Scene.cs", scene);
        Write("shaders/blur.glsl", blur);
        Write("config/app.json", "{\"name\": \"demo\", \"SceneSynchronizer\": true}\n");
        Write("docs/guide.md", "# Guide\n\nThe scene synchronizer pairs the scene with the server.\n");
        int sourceId = await services.GetRequiredService<SourceStore>().AddAsync(new SourceInput { Name = "demo", Kind = SourceKind.Code, RootPath = _root }, cancellationToken);

        IndexRun run = (await services.GetRequiredService<Indexer>().RunAsync(sourceId, null, cancellationToken))!;

        Assert.Equal(IndexRun.Succeeded, run.Status);
        Assert.Null(run.Warning);
        List<ChunkRow> rows = await ReadChunksAsync(services, cancellationToken);
        Assert.Equal(rows.Count, run.ChunksEmbedded);
        Assert.All(rows, static row => Assert.True(row.Embedded));

        ChunkRow[] sceneChunks = [.. rows.Where(static row => row.Path == "src/Scene.cs").OrderBy(static row => row.Ordinal)];
        Assert.Equal([0, 1, 2], sceneChunks.Select(static row => row.Ordinal));
        Assert.Equal([(1, 1), (3, 8), (10, 13)], sceneChunks.Select(static row => (row.LineStart!.Value, row.LineEnd!.Value)));
        Assert.All(sceneChunks, static row => Assert.Equal(("code", true), (row.Kind, row.HasTokens)));
        Assert.Equal("src/Scene.cs > public sealed class SceneSynchronizer", sceneChunks[1].Heading);

        ChunkRow[] blurChunks = [.. rows.Where(static row => row.Path == "shaders/blur.glsl").OrderBy(static row => row.Ordinal)];
        Assert.True(blurChunks.Length > 2);
        Assert.All(blurChunks, static row => Assert.Equal(("code", "shaders/blur.glsl", true), (row.Kind, row.Heading, row.HasTokens)));

        ChunkRow config = Assert.Single(rows, static row => row.Path == "config/app.json");
        Assert.Equal(("config", 1, 1), (config.Kind, config.LineStart, config.LineEnd));

        ChunkRow prose = Assert.Single(rows, static row => row.Path == "docs/guide.md");
        Assert.Equal("prose", prose.Kind);
        Assert.Null(prose.LineStart);
        Assert.False(prose.HasTokens);

        foreach (ChunkRow row in rows.Where(static row => row.Kind != "prose"))
        {
            string content = await File.ReadAllTextAsync(Path.Combine(_root, row.Path), cancellationToken);
            Assert.Equal(content[row.CharStart..row.CharEnd], row.Body);
            Assert.Equal(1 + content[..row.CharStart].Count(static c => c == '\n'), row.LineStart);
            Assert.Equal(1 + content[..(row.CharEnd - 1)].Count(static c => c == '\n'), row.LineEnd);
        }

        await using NpgsqlConnection connection = await services.GetRequiredService<Database>().OpenAsync(cancellationToken);
        Assert.Equal(1, await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM chunk WHERE code_tsv @@ to_tsquery('simple', 'softgpu') AND kind = 'code'"));
        Assert.Equal(1, await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM chunk WHERE code_tsv @@ to_tsquery('simple', 'soft & gpu')"));
        Assert.Equal(2, await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM chunk WHERE code_tsv @@ to_tsquery('simple', 'synchronizer')"));
    }

    [Fact]
    public async Task Indexer_EmbedsOnlyTheChangedChunkAfterAnEdit()
    {
        ServiceProvider services = RequireDatabase();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        const string before = "// first\nvoid A() { }\n\n// second\nvoid B() { }\n\n// third\nvoid C() { }\n";
        Write("Items.cs", before);
        int sourceId = await services.GetRequiredService<SourceStore>().AddAsync(new SourceInput { Name = "demo", Kind = SourceKind.Code, RootPath = _root }, cancellationToken);
        Indexer indexer = services.GetRequiredService<Indexer>();

        IndexRun first = (await indexer.RunAsync(sourceId, null, cancellationToken))!;
        Assert.Equal((3, 0), (first.ChunksEmbedded, first.ChunksReused));
        Assert.Equal("code:2;100;0;2;BlockChunker:1", await ReadVersionAsync(services, "Items.cs", cancellationToken));

        IndexRun same = (await indexer.RunAsync(sourceId, null, cancellationToken))!;
        Assert.Equal((1, 0, 0), (same.DocumentsUnchanged, same.ChunksEmbedded, same.ChunksReused));

        Write("Items.cs", before.Replace("void B() { }", "void B() { Run(); }", StringComparison.Ordinal));
        IndexRun edited = (await indexer.RunAsync(sourceId, null, cancellationToken))!;
        Assert.Equal((1, 2, 1), (edited.DocumentsUpdated, edited.ChunksReused, edited.ChunksEmbedded));
        Assert.Equal(3, (await ReadChunksAsync(services, cancellationToken)).Count);

        _blocks.Version = 2;
        IndexRun newChunker = (await indexer.RunAsync(sourceId, null, cancellationToken))!;
        Assert.Equal((1, 3, 0), (newChunker.DocumentsUpdated, newChunker.ChunksReused, newChunker.ChunksEmbedded));
        Assert.Equal("code:2;100;0;2;BlockChunker:2", await ReadVersionAsync(services, "Items.cs", cancellationToken));
    }

    [Fact]
    public async Task Search_KeepsTheChunksOfCodeOutOfTheDocumentation()
    {
        ServiceProvider services = RequireDatabase();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        Write("docs/guide.md", "# Guide\n\nThe synchronizer pairs the scene with the server.\n");
        Write("src/Scene.cs", "// The synchronizer pairs the scene with the server.\npublic sealed class Synchronizer { }\n");
        Write("config/scene.json", "{\"synchronizer\": \"pairs the scene with the server\"}\n");
        int sourceId = await services.GetRequiredService<SourceStore>().AddAsync(new SourceInput { Name = "demo", Kind = SourceKind.Code, RootPath = _root }, cancellationToken);
        await services.GetRequiredService<Indexer>().RunAsync(sourceId, null, cancellationToken);
        await using NpgsqlConnection connection = await services.GetRequiredService<Database>().OpenAsync(cancellationToken);
        Assert.Equal(2, await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM chunk WHERE kind <> 'prose' AND tsv @@ plainto_tsquery('russian', 'synchronizer')"));
        SearchService search = services.GetRequiredService<SearchService>();

        foreach (bool rerank in new[] { true, false })
        {
            SearchResult result = await search.SearchAsync(new SearchRequest { Query = "synchronizer pairs the scene with the server", Limit = 20, Rerank = rerank }, cancellationToken);

            Assert.Equal("docs/guide.md", Assert.Single(result.Hits).Path);
            Assert.Equal("hybrid", result.Mode);
        }
    }

    [Fact]
    public async Task CodeSearch_ReturnsTheTiersWithoutRepeatingAPlace()
    {
        (ServiceProvider services, _) = await IndexSampleAsync();
        CodeTierSearch tiers = services.GetRequiredService<CodeTierSearch>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        CodeSearchResult result = await tiers.SearchAsync(new CodeSearchRequest { Query = "SceneHost", Source = "demo", Limit = 50, Rerank = false }, cancellationToken);

        Assert.Equal("SceneHost", result.Query);
        SymbolMatch symbol = Assert.Single(result.Symbols, static match => match.Match == CodeMatchKinds.Exact);
        Assert.Equal("Demo.SceneHost", symbol.FullName);
        Assert.All(result.Symbols, static match => Assert.Contains(match.Match, new[] { CodeMatchKinds.Exact, CodeMatchKinds.ExactIgnoreCase, CodeMatchKinds.Prefix, CodeMatchKinds.Humps, CodeMatchKinds.Qualified }));

        (string Path, int Line)[] lines = [.. result.Matches.Select(static match => (match.Path, match.Line))];
        Assert.Contains(("src/Core/SceneSynchronizer.cs", 5), lines);
        Assert.Contains(("src/Core/SceneHost.cs", 4), lines);
        Assert.DoesNotContain((symbol.Path!, symbol.Line!.Value), lines);
        Assert.Equal(lines.Distinct().Count(), lines.Length);

        Assert.Equal(("hybrid", false), (result.Mode, result.Reranked));
        Assert.Empty(result.Notes);
        Assert.NotEmpty(result.Chunks);
        CodeChunkHit declaration = Assert.Single(result.Chunks, static hit => hit.Path == "src/Core/SceneHost.cs" && hit.LineStart == 3);
        Assert.Equal(("demo", "csharp", 8), (declaration.Source, declaration.Language, declaration.LineEnd));
        Assert.Equal("Demo.SceneHost", declaration.Symbol?.FullName);
        Assert.Contains("class SceneHost", declaration.Text, StringComparison.Ordinal);
        CodeChunkHit reset = Assert.Single(result.Chunks, static hit => hit.Path == "src/Core/SceneHost.cs" && hit.LineStart == 10);
        Assert.Equal("Demo.SceneHost.Reset", reset.Symbol?.FullName);
        Assert.All(result.Chunks, static hit => Assert.True(hit.VectorRank is not null || hit.TextRank is not null));
        Assert.Contains(result.Chunks, static hit => hit.TextRank is not null);
        Assert.Contains(result.Chunks, static hit => hit.VectorRank is not null);

        CodeSearchResult words = await tiers.SearchAsync(new CodeSearchRequest { Query = "reset overloads of the scene", Source = "demo" }, cancellationToken);
        Assert.Empty(words.Symbols);
        Assert.Empty(words.Matches);
        Assert.True(words.Reranked);
        Assert.Contains(words.Chunks, static hit => hit.Path == "src/Core/SceneHost.cs" && hit.LineStart == 10);
        Assert.Equal(words.Chunks.Select(static hit => hit.RerankScore).Order().Reverse(), words.Chunks.Select(static hit => hit.RerankScore));
        Assert.All(words.Chunks, static hit => Assert.Equal(hit.RerankScore, hit.Score));

        CodeSearchResult lowerCase = await tiers.SearchAsync(new CodeSearchRequest { Query = "HOST.SYNC" }, cancellationToken);
        Assert.Equal("src/Core/SceneSynchronizer.cs", Assert.Single(lowerCase.Matches).Path);
        Assert.Contains("host.Sync", lowerCase.Matches[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CodeSearch_AppliesTheFilters()
    {
        (ServiceProvider services, _) = await IndexSampleAsync();
        CodeTierSearch tiers = services.GetRequiredService<CodeTierSearch>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        const string testFile = "tests/Core.Tests/SceneTests.cs";

        CodeSearchResult all = await tiers.SearchAsync(new CodeSearchRequest { Query = "SceneHost", Limit = 50, Rerank = false }, cancellationToken);
        Assert.Contains(all.Matches, static match => match.Path == testFile);
        Assert.Contains(all.Chunks, static hit => hit.Path == testFile);

        CodeSearchResult noTests = await tiers.SearchAsync(new CodeSearchRequest { Query = "SceneHost", Limit = 50, Rerank = false, IncludeTests = false }, cancellationToken);
        Assert.DoesNotContain(noTests.Matches, static match => match.Path == testFile);
        Assert.DoesNotContain(noTests.Chunks, static hit => hit.Path == testFile);
        Assert.Contains(noTests.Chunks, static hit => hit.Path == "src/Core/SceneHost.cs");

        Assert.Equal("Demo.SceneTests", Assert.Single((await tiers.SearchAsync(new CodeSearchRequest { Query = "SceneTests" }, cancellationToken)).Symbols).FullName);
        Assert.Empty((await tiers.SearchAsync(new CodeSearchRequest { Query = "SceneTests", IncludeTests = false }, cancellationToken)).Symbols);

        CodeSearchResult underTests = await tiers.SearchAsync(new CodeSearchRequest { Query = "SceneHost", Path = "tests/**/*.cs", Limit = 50, Rerank = false }, cancellationToken);
        Assert.Empty(underTests.Symbols);
        Assert.NotEmpty(underTests.Matches);
        Assert.NotEmpty(underTests.Chunks);
        Assert.All(underTests.Matches, static match => Assert.StartsWith("tests/", match.Path, StringComparison.Ordinal));
        Assert.All(underTests.Chunks, static hit => Assert.StartsWith("tests/", hit.Path, StringComparison.Ordinal));
        Assert.Single((await tiers.SearchAsync(new CodeSearchRequest { Query = "SceneTests", Path = "tests/**/*.cs" }, cancellationToken)).Symbols);
        Assert.Empty((await tiers.SearchAsync(new CodeSearchRequest { Query = "SceneTests", Path = "src/**/*.cs" }, cancellationToken)).Symbols);

        CodeSearchResult byName = await tiers.SearchAsync(new CodeSearchRequest { Query = "SceneHost", Path = "SceneHost.cs", Limit = 50, Rerank = false }, cancellationToken);
        Assert.All(byName.Chunks, static hit => Assert.Equal("src/Core/SceneHost.cs", hit.Path));
        Assert.NotEmpty(byName.Chunks);
        Assert.Empty((await tiers.SearchAsync(new CodeSearchRequest { Query = "SceneHost", Path = "nothing/**/*.cs" }, cancellationToken)).Chunks);

        CodeSearchResult msbuild = await tiers.SearchAsync(new CodeSearchRequest { Query = "SceneHost", Language = "MSBuild", Limit = 50, Rerank = false }, cancellationToken);
        Assert.Empty(msbuild.Symbols);
        Assert.NotEmpty(msbuild.Chunks);
        Assert.All(msbuild.Chunks, static hit => Assert.Equal("msbuild", hit.Language));
        Assert.All(msbuild.Matches, static match => Assert.Equal("msbuild", match.Language));

        CodeSearchResult json = await tiers.SearchAsync(new CodeSearchRequest { Query = "SceneHost", Language = "json", Limit = 50, Rerank = false }, cancellationToken);
        CodeChunkHit config = Assert.Single(json.Chunks);
        Assert.Equal(("config/app.json", "json"), (config.Path, config.Language));
        Assert.Null(config.Symbol);

        CodeSearchResult one = await tiers.SearchAsync(new CodeSearchRequest { Query = "SceneHost", Limit = 2, Rerank = false }, cancellationToken);
        Assert.Equal(2, one.Chunks.Count);
    }

    [Fact]
    public async Task CodeSearch_FallsBackToTheTokenSearchWithoutTheEmbeddingServer()
    {
        (ServiceProvider services, _) = await IndexSampleAsync();
        CodeTierSearch tiers = services.GetRequiredService<CodeTierSearch>();
        _embedder.Fail = true;

        CodeSearchResult result = await tiers.SearchAsync(new CodeSearchRequest { Query = "reset overloads" }, TestContext.Current.CancellationToken);

        Assert.Equal("text", result.Mode);
        Assert.Contains(result.Notes, static note => note.StartsWith("The vector search did not work", StringComparison.Ordinal));
        Assert.Contains(result.Notes, static note => note.Contains("token search only", StringComparison.Ordinal));
        Assert.NotEmpty(result.Chunks);
        Assert.All(result.Chunks, static hit => Assert.Null(hit.VectorRank));
        Assert.Contains(result.Chunks, static hit => hit.Path == "src/Core/SceneHost.cs" && hit.LineStart == 10);
    }

    [Fact]
    public async Task CodeSearch_RejectsABadRequest()
    {
        (ServiceProvider services, _) = await IndexSampleAsync();
        CodeTierSearch tiers = services.GetRequiredService<CodeTierSearch>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        int docsId = await services.GetRequiredService<SourceStore>().AddAsync(new SourceInput { Name = "docs", RootPath = _root }, cancellationToken);

        async Task<string> FieldAsync(CodeSearchRequest request) =>
            (await Assert.ThrowsAsync<CodeSearchException>(() => tiers.SearchAsync(request, cancellationToken))).Field;

        Assert.Equal("query", await FieldAsync(new CodeSearchRequest { Query = "  " }));
        Assert.Equal("query", await FieldAsync(new CodeSearchRequest { Query = new string('x', CodeTierSearch.MaxQueryLength + 1) }));
        Assert.Equal("sourceId", await FieldAsync(new CodeSearchRequest { Query = "x", SourceId = docsId }));
        Assert.Equal("sourceId", await FieldAsync(new CodeSearchRequest { Query = "x", SourceId = 9999 }));
        Assert.Equal("codebase", await FieldAsync(new CodeSearchRequest { Query = "x", Source = "missing" }));
        Assert.Equal("codebase", await FieldAsync(new CodeSearchRequest { Query = "x", Source = "docs" }));
        Assert.Equal("path", await FieldAsync(new CodeSearchRequest { Query = "x", Path = "a//b" }));
        Assert.NotEmpty((await tiers.SearchAsync(new CodeSearchRequest { Query = new string('x', CodeTierSearch.MaxQueryLength) }, cancellationToken)).Notes.Where(static _ => false).DefaultIfEmpty("ok"));
    }

    private async Task<(ServiceProvider Services, int SourceId)> IndexSampleAsync()
    {
        ServiceProvider services = RequireDatabase();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        foreach ((string path, string text) in SampleGraph.Files)
            Write(path, text);

        Write("config/app.json", "{\"host\": \"SceneHost\"}\n");
        int sourceId = await services.GetRequiredService<SourceStore>().AddAsync(new SourceInput { Name = "demo", Kind = SourceKind.Code, RootPath = _root }, cancellationToken);
        IndexRun run = (await services.GetRequiredService<Indexer>().RunAsync(sourceId, null, cancellationToken))!;
        Assert.Equal(IndexRun.Succeeded, run.Status);
        Assert.Null(run.Warning);
        return (services, sourceId);
    }

    private static async Task<List<ChunkRow>> ReadChunksAsync(ServiceProvider services, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await services.GetRequiredService<Database>().OpenAsync(cancellationToken);
        return [.. await connection.QueryAsync<ChunkRow>(new CommandDefinition(
            """
            SELECT d.path, c.ordinal, c.kind, c.heading, c.body, c.char_start, c.char_end, c.line_start, c.line_end, c.code_tsv IS NOT NULL AS has_tokens,
                   EXISTS (SELECT 1 FROM chunk_embedding e WHERE e.chunk_id = c.chunk_id) AS embedded
            FROM chunk c
            JOIN document d USING (document_id)
            ORDER BY d.path, c.ordinal
            """,
            cancellationToken: cancellationToken))];
    }

    private static async Task<string> ReadVersionAsync(ServiceProvider services, string path, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await services.GetRequiredService<Database>().OpenAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<string>(new CommandDefinition(
            "SELECT parser_version FROM document WHERE path = @Path", new { Path = path }, cancellationToken: cancellationToken)) ?? string.Empty;
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

    private sealed class ChunkRow
    {
        public string Path { get; init; } = string.Empty;

        public int Ordinal { get; init; }

        public string Kind { get; init; } = string.Empty;

        public string Heading { get; init; } = string.Empty;

        public string Body { get; init; } = string.Empty;

        public int CharStart { get; init; }

        public int CharEnd { get; init; }

        public int? LineStart { get; init; }

        public int? LineEnd { get; init; }

        public bool HasTokens { get; init; }

        public bool Embedded { get; init; }
    }
}
