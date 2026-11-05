using Dapper;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Npgsql;

using ProjectRag.Core.Documents;
using ProjectRag.Core.Embeddings;
using ProjectRag.Core.Indexing;
using ProjectRag.Core.Reranking;
using ProjectRag.Core.Search;
using ProjectRag.Core.Sources;
using ProjectRag.Core.Storage;

namespace ProjectRag.Core.Tests.Storage;

/// <summary>
/// Runs the indexer and the search against a real PostgreSQL with pgvector. The models are fakes.
/// </summary>
/// <remarks>
/// The tests need the variable PROJECTRAG_TEST_DATABASE with a connection string, for example
/// <c>Host=127.0.0.1;Port=5434;Database=project_rag_test;Username=project_rag;Password=project_rag</c>.
/// The tests drop and create that database, so it must not be the database of the application. Without the variable the tests skip themselves.
/// </remarks>
public sealed class KnowledgeBaseTests : IAsyncLifetime
{
    private const string VariableName = "PROJECTRAG_TEST_DATABASE";

    private readonly string _root = Directory.CreateTempSubdirectory("project-rag-").FullName;
    private readonly FakePageClient _pages = new();
    private readonly ManualTime _time = new();
    private ServiceProvider? _services;

    public async ValueTask InitializeAsync()
    {
        string? connectionString = Environment.GetEnvironmentVariable(VariableName);
        if (string.IsNullOrEmpty(connectionString))
            return;

        NpgsqlConnectionStringBuilder builder = new(connectionString);
        string database = builder.Database ?? throw new InvalidOperationException($"{VariableName} names no database.");
        builder.Database = "postgres";
        await using (NpgsqlConnection admin = new(builder.ConnectionString))
        {
            await admin.ExecuteAsync($"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE)");
            await admin.ExecuteAsync($"CREATE DATABASE \"{database}\"");
        }

        // The pool still holds the connections of the last test to the dropped database.
        NpgsqlConnection.ClearAllPools();

        DatabaseMigrator.MigrateUp(connectionString, NullLoggerFactory.Instance);
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:ProjectRag"] = connectionString,
            ["Chunking:MaxChars"] = "200",
            // The tests count the chunks of each section, so small sections stay apart.
            ["Chunking:MinChars"] = "0",
            ["Embedding:Model"] = "fake",
            ["Rerank:Candidates"] = "10",
        }).Build();

        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(_time);
        services.AddKnowledgeBase(configuration);
        services.AddSingleton<IPageClient>(_pages);
        services.AddSingleton<IEmbeddingClient>(new FakeEmbeddingClient(0));
        services.AddSingleton<IRerankClient, FakeRerankClient>();
        _services = services.BuildServiceProvider();
    }

    public async ValueTask DisposeAsync()
    {
        if (_services is not null)
            await _services.DisposeAsync();

        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Indexer_FollowsTheChangesOfTheFolder()
    {
        ServiceProvider services = RequireDatabase();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        Write("docs/textures.md", "# Textures\n\n## Filtering\n\nMipmap filtering picks a level of the texture.\n\n## Wrapping\n\nThe wrap mode repeats the coordinates.\n");
        Write("docs/buffers.md", "# Buffers\n\nA buffer object stores vertex data.\n");
        int sourceId = await services.GetRequiredService<SourceStore>().AddAsync(new SourceInput { Name = "gl", RootPath = _root }, cancellationToken);
        Indexer indexer = services.GetRequiredService<Indexer>();

        IndexRun first = (await indexer.RunAsync(sourceId, null, cancellationToken))!;
        Assert.Equal(IndexRun.Succeeded, first.Status);
        Assert.Equal(2, first.DocumentsAdded);
        Assert.Equal(3, first.ChunksEmbedded);

        IndexRun second = (await indexer.RunAsync(sourceId, null, cancellationToken))!;
        Assert.Equal(2, second.DocumentsUnchanged);
        Assert.Equal(0, second.ChunksEmbedded);

        // A new section: the two old chunks keep their vectors, and only the new chunk goes to the model.
        Write("docs/textures.md", "# Textures\n\n## Filtering\n\nMipmap filtering picks a level of the texture.\n\n## Wrapping\n\nThe wrap mode repeats the coordinates.\n\n## Formats\n\nA compressed format saves memory.\n");
        File.Delete(Path.Combine(_root, "docs/buffers.md"));
        IndexRun third = (await indexer.RunAsync(sourceId, null, cancellationToken))!;
        Assert.Equal(1, third.DocumentsUpdated);
        Assert.Equal(1, third.DocumentsRemoved);
        Assert.Equal(2, third.ChunksReused);
        Assert.Equal(1, third.ChunksEmbedded);

        Source source = (await services.GetRequiredService<SourceStore>().GetAsync(sourceId, 1, cancellationToken))!;
        Assert.Equal(1, source.DocumentCount);
        Assert.Equal(3, source.ChunkCount);
        Assert.Equal(3, source.EmbeddedCount);
        Assert.NotNull(source.IndexedAt);
    }

    [Fact]
    public async Task Search_FindsTheSectionWithBothLegsAndTheReranker()
    {
        ServiceProvider services = RequireDatabase();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        Write("textures.md", "# Textures\n\n## Filtering\n\nMipmap filtering picks a level of the texture.\n\n## Wrapping\n\nThe wrap mode repeats the coordinates.\n");
        Write("buffers.md", "# Buffers\n\nA buffer object stores vertex data.\n");
        int sourceId = await services.GetRequiredService<SourceStore>().AddAsync(new SourceInput { Name = "gl", RootPath = _root }, cancellationToken);
        await services.GetRequiredService<Indexer>().RunAsync(sourceId, null, cancellationToken);
        SearchService search = services.GetRequiredService<SearchService>();

        SearchResult result = await search.SearchAsync(new SearchRequest { Query = "mipmap filtering", Sources = ["gl"] }, cancellationToken);

        Assert.Equal("hybrid", result.Mode);
        Assert.True(result.Reranked);
        Assert.Empty(result.Notes);
        SearchHit best = result.Hits[0];
        Assert.Equal("textures.md", best.Path);
        Assert.Equal("Textures > Filtering", best.Heading);
        Assert.Equal(1, best.TextRank);
        Assert.NotNull(best.VectorRank);
        await Assert.ThrowsAsync<ArgumentException>(() => search.SearchAsync(new SearchRequest { Query = "texture", Sources = ["missing"] }, cancellationToken));

        (IReadOnlyList<DocumentSummary> found, long total) = await services.GetRequiredService<DocumentStore>().ListAsync([], string.Empty, "textur", 0, 10, cancellationToken);
        Assert.Equal(1, total);
        Assert.Equal("textures.md", Assert.Single(found).Path);
    }

    [Fact]
    public async Task Search_ShowsATextOfTwoDocumentsOnceAlsoWithOtherLinks()
    {
        ServiceProvider services = RequireDatabase();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        // The links of the two copies name other pages, as in the core and the compatibility profile of OpenGL.
        Write("core.md", "# Core\n\n## Depth\n\nThe depth test compares the depth of a fragment with the [buffer](#p10-2) of the depth.\n\n## Stencil\n\nThe stencil test compares a reference value.\n");
        Write("compatibility.md", "# Compatibility\n\n## Depth\n\nThe depth test compares the depth of a fragment with the [buffer](#p12-5) of the depth.\n\n## Alpha\n\nThe alpha test discards a fragment by its alpha.\n");
        int sourceId = await services.GetRequiredService<SourceStore>().AddAsync(new SourceInput { Name = "gl", RootPath = _root }, cancellationToken);
        await services.GetRequiredService<Indexer>().RunAsync(sourceId, null, cancellationToken);
        SearchService search = services.GetRequiredService<SearchService>();

        foreach (bool rerank in new[] { true, false })
        {
            SearchResult result = await search.SearchAsync(new SearchRequest { Query = "depth test of a fragment", Rerank = rerank }, cancellationToken);

            SearchHit depth = Assert.Single(result.Hits, static hit => hit.Heading.EndsWith("Depth", StringComparison.Ordinal));
            HitCopy copy = Assert.Single(depth.Copies);
            Assert.Equal(["compatibility.md", "core.md"], new[] { depth.Path, copy.Path }.Order(StringComparer.Ordinal));
            Assert.EndsWith("Depth", copy.Heading, StringComparison.Ordinal);
            Assert.Equal(1, copy.Similarity);
            Assert.Equal(3, result.Hits.Count);
        }
    }

    [Fact]
    public async Task Search_MergesALongTextWithAnotherWordButNotAShortOne()
    {
        ServiceProvider services = RequireDatabase();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        // The prose of these tests splits at 200 characters, so the long text is code, which stays whole up to 3000 characters.
        string common = string.Join('\n', Enumerable.Range(1, 20).Select(static i => $"blend stage {i} reads the color of the fragment"));
        Write("core.md", $"# Core\n\n## Blend\n\n```\n{common}\nfactor is zero\n```\n\n## Rule\n\nThe blend value must be set before the draw.\n");
        Write("compatibility.md", $"# Compatibility\n\n## Blend\n\n```\n{common}\nfactor is one\n```\n\n## Rule\n\nThe blend value must not be set before the draw.\n");
        int sourceId = await services.GetRequiredService<SourceStore>().AddAsync(new SourceInput { Name = "gl", RootPath = _root }, cancellationToken);
        await services.GetRequiredService<Indexer>().RunAsync(sourceId, null, cancellationToken);

        SearchResult result = await services.GetRequiredService<SearchService>().SearchAsync(new SearchRequest { Query = "blend value", Rerank = false, Limit = 10 }, cancellationToken);

        // The long text differs in one word and merges with a similarity below 1. The short rules differ in their meaning and stay apart.
        HitCopy copy = Assert.Single(Assert.Single(result.Hits, static hit => hit.Heading.EndsWith("Blend", StringComparison.Ordinal)).Copies);
        Assert.InRange(copy.Similarity, 0.9, 0.999);
        Assert.Equal(2, result.Hits.Count(static hit => hit.Heading.EndsWith("Rule", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Replacements_FixTheTextOfTheMatchingFiles()
    {
        ServiceProvider services = RequireDatabase();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        Write("metal.md", "# Metal\n\n## Sparse\n\nThe function returns sparse_color-Tv for a color.\n");
        Write("other.md", "# Other\n\n## Sparse\n\nThe text keeps sparse_color-Tv as it is.\n");
        SourceStore sources = services.GetRequiredService<SourceStore>();
        SourceInput input = new() { Name = "gl", RootPath = _root };
        int sourceId = await sources.AddAsync(input, cancellationToken);
        Indexer indexer = services.GetRequiredService<Indexer>();
        DocumentStore documents = services.GetRequiredService<DocumentStore>();
        await indexer.RunAsync(sourceId, null, cancellationToken);

        // A new replacement reads the matching file again and leaves the other file alone.
        input.Replacements = [new TextReplacement { Pattern = "metal.md", Find = @"sparse_color-(Tv?)\b", Replace = "sparse_color<$1>" }];
        await sources.UpdateAsync(sourceId, input, cancellationToken);
        IndexRun run = (await indexer.RunAsync(sourceId, null, cancellationToken))!;

        Assert.Equal(1, run.DocumentsUpdated);
        Assert.Equal(1, run.DocumentsUnchanged);
        DocumentSummary metal = (await documents.FindAsync("gl", "metal.md", cancellationToken))!;
        Assert.Contains("sparse_color<Tv> for", (await documents.GetContentAsync(metal, 0, 1000, cancellationToken)).Text, StringComparison.Ordinal);
        DocumentSummary other = (await documents.FindAsync("gl", "other.md", cancellationToken))!;
        Assert.Contains("sparse_color-Tv as", (await documents.GetContentAsync(other, 0, 1000, cancellationToken)).Text, StringComparison.Ordinal);
        Assert.Single((await sources.GetAsync(sourceId, null, cancellationToken))!.Replacements);
    }

    [Theory]
    [InlineData(@"sparse_color-(Tv?)\b", true)]
    [InlineData("sparse_color-(", false)]
    [InlineData("", false)]
    public void Replacement_ChecksItsExpression(string find, bool valid) =>
        Assert.Equal(valid, new TextReplacement { Pattern = "*.pdf", Find = find, Replace = "x" }.IsValid());

    [Fact]
    public async Task Categories_ComeFromTheRulesOfTheSource()
    {
        ServiceProvider services = RequireDatabase();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        Write("specs/vkspec.md", "# Vulkan\n\n## Pipelines\n\nA pipeline of the Vulkan API holds the state of the shaders.\n");
        Write("glspec.md", "# OpenGL\n\n## Pipelines\n\nThe pipeline of the OpenGL API runs the shaders of a program.\n");
        SourceStore sources = services.GetRequiredService<SourceStore>();
        SourceInput input = new() { Name = "gl", RootPath = _root, CategoryRules = [new CategoryRule { Pattern = "specs/vk*.md", Category = "vulkan" }] };
        int sourceId = await sources.AddAsync(input, cancellationToken);
        Indexer indexer = services.GetRequiredService<Indexer>();
        await indexer.RunAsync(sourceId, null, cancellationToken);
        DocumentStore documents = services.GetRequiredService<DocumentStore>();
        SearchService search = services.GetRequiredService<SearchService>();

        Assert.Equal("vulkan", Assert.Single((await sources.GetAsync(sourceId, null, cancellationToken))!.CategoryRules).Category);
        Assert.Equal(["gl", "vulkan"], (await documents.GetCategoriesAsync(null, cancellationToken)).Select(static category => category.Name));
        Assert.Equal("specs/vkspec.md", Assert.Single((await documents.ListAsync([], "vulkan", string.Empty, 0, 10, cancellationToken)).Items).Path);
        SearchResult result = await search.SearchAsync(new SearchRequest { Query = "pipeline shaders", Categories = ["vulkan"], Rerank = false }, cancellationToken);
        Assert.All(result.Hits, static hit => Assert.Equal("vulkan", hit.Category));
        Assert.NotEmpty(result.Hits);
        await Assert.ThrowsAsync<ArgumentException>(() => search.SearchAsync(new SearchRequest { Query = "pipeline", Categories = ["missing"] }, cancellationToken));

        // A hidden category leaves the search without categories, and a request that names it still finds it.
        SearchOptions searchOptions = services.GetRequiredService<IOptionsMonitor<SearchOptions>>().CurrentValue;
        searchOptions.HiddenCategories = ["vulkan"];
        try
        {
            SearchResult hidden = await search.SearchAsync(new SearchRequest { Query = "pipeline shaders", Rerank = false }, cancellationToken);
            Assert.NotEmpty(hidden.Hits);
            Assert.DoesNotContain(hidden.Hits, static hit => hit.Category == "vulkan");
            Assert.NotEmpty((await search.SearchAsync(new SearchRequest { Query = "pipeline shaders", Categories = ["vulkan"], Rerank = false }, cancellationToken)).Hits);
        }
        finally
        {
            searchOptions.HiddenCategories = [];
        }

        // A new rule changes the categories of the stored documents without a new read of the files.
        input.CategoryRules = [new CategoryRule { Pattern = "*.md", Category = "opengl" }];
        await sources.UpdateAsync(sourceId, input, cancellationToken);
        Assert.Equal(2, await documents.ApplyCategoriesAsync((await sources.GetAsync(sourceId, null, cancellationToken))!, cancellationToken));
        Assert.Equal("opengl", Assert.Single((await documents.ListAsync([], string.Empty, "glspec", 0, 10, cancellationToken)).Items).Category);
        Assert.Equal("gl", Assert.Single((await documents.ListAsync([], string.Empty, "vkspec", 0, 10, cancellationToken)).Items).Category);
    }

    [Fact]
    public async Task Indexer_SkipsRejectedChunksAndKeepsTheDocumentsOfAnEmptyFolder()
    {
        ServiceProvider services = RequireDatabase();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        Write("guide.md", "# Guide\n\n## Good\n\nA normal section.\n\n## Bad\n\nRejectMe is too long for the server.\n");
        int sourceId = await services.GetRequiredService<SourceStore>().AddAsync(new SourceInput { Name = "gl", RootPath = _root }, cancellationToken);
        Indexer indexer = services.GetRequiredService<Indexer>();

        IndexRun first = (await indexer.RunAsync(sourceId, null, cancellationToken))!;
        Assert.Equal(IndexRun.Succeeded, first.Status);
        Assert.Equal(1, first.ChunksEmbedded);
        Assert.Contains("rejected 1 chunks", first.Warning, StringComparison.Ordinal);

        File.Delete(Path.Combine(_root, "guide.md"));
        IndexRun second = (await indexer.RunAsync(sourceId, null, cancellationToken))!;
        Assert.Equal(IndexRun.Succeeded, second.Status);
        Assert.Equal(0, second.DocumentsRemoved);
        Assert.Contains("holds no matching files", second.Warning, StringComparison.Ordinal);
        Assert.Equal(1, (await services.GetRequiredService<SourceStore>().GetAsync(sourceId, null, cancellationToken))!.DocumentCount);
    }

    [Fact]
    public async Task Indexer_ReadsTheWebPagesOnceInTheIntervalOfTheRefresh()
    {
        ServiceProvider services = RequireDatabase();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        const string url = "https://example.com/specs/format.html";
        _pages.Pages[url] = "<title>Format</title><h2 id=\"_header\">1. Header</h2><p>The header holds the size of the texture. See <a href=\"#_levels\">the levels</a>.</p>"
            + "<h2 id=\"_levels\">2. Levels</h2><p>The levels follow the header.</p>";
        SourceStore store = services.GetRequiredService<SourceStore>();
        SourceInput input = new() { Name = "web", Urls = [url], CategoryRules = [new CategoryRule { Pattern = "**/format.html", Category = "format" }] };
        int sourceId = await store.AddAsync(input, cancellationToken);
        Indexer indexer = services.GetRequiredService<Indexer>();

        IndexRun first = (await indexer.RunAsync(sourceId, null, cancellationToken))!;
        Assert.Equal(IndexRun.Succeeded, first.Status);
        Assert.Equal(1, first.DocumentsAdded);
        Assert.Equal(1, _pages.Requests);
        Source source = (await store.GetAsync(sourceId, null, cancellationToken))!;
        Assert.Equal(["format"], source.Categories);
        SearchResult result = await services.GetRequiredService<SearchService>().SearchAsync(new SearchRequest { Query = "size of the texture" }, cancellationToken);
        Assert.Equal(url, result.Hits[0].Path);

        // Within a day the page stays as it is, without a request.
        IndexRun second = (await indexer.RunAsync(sourceId, null, cancellationToken))!;
        Assert.Equal(1, second.DocumentsUnchanged);
        Assert.Equal(1, _pages.Requests);

        // After a day the same page only gets its new time.
        _time.Advance(TimeSpan.FromHours(25));
        IndexRun third = (await indexer.RunAsync(sourceId, null, cancellationToken))!;
        Assert.Equal(1, third.DocumentsUnchanged);
        Assert.Equal(2, _pages.Requests);

        // A page that does not load keeps its document.
        _pages.Pages.Remove(url);
        _time.Advance(TimeSpan.FromHours(25));
        IndexRun fourth = (await indexer.RunAsync(sourceId, null, cancellationToken))!;
        Assert.Equal(IndexRun.Succeeded, fourth.Status);
        Assert.Equal(1, fourth.DocumentsFailed);
        Assert.Contains("did not load", fourth.Warning, StringComparison.Ordinal);
        Assert.Equal(1, (await store.GetAsync(sourceId, null, cancellationToken))!.DocumentCount);

        // A page that leaves the source leaves the search.
        input.Urls = [];
        input.RootPath = _root;
        await store.UpdateAsync(sourceId, input, cancellationToken);
        IndexRun fifth = (await indexer.RunAsync(sourceId, null, cancellationToken))!;
        Assert.Equal(1, fifth.DocumentsRemoved);
    }

    [Fact]
    public async Task Structure_ResolvesLinksMentionsAndBacklinks()
    {
        ServiceProvider services = RequireDatabase();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        Write("docs/a.md", "# A\n\nSee [usage](b.md#usage) and section 2 for more.\n\n## 2 Second\n\nText of the second section.\n");
        Write("docs/b.md", "# B\n\nIntro of B.\n\n## Usage\n\nHow to use it.\n");
        int sourceId = await services.GetRequiredService<SourceStore>().AddAsync(new SourceInput { Name = "gl", RootPath = _root }, cancellationToken);
        await services.GetRequiredService<Indexer>().RunAsync(sourceId, null, cancellationToken);
        DocumentStore documents = services.GetRequiredService<DocumentStore>();
        StructureStore structure = services.GetRequiredService<StructureStore>();

        DocumentSummary a = (await documents.FindAsync("gl", "docs/a.md", cancellationToken))!;
        ChunkPage page = await documents.GetChunksAsync(a.DocumentId, null, 0, 1, null, cancellationToken);
        ChunkView first = Assert.Single(page.Items);
        Assert.True(page.Total >= 1);
        Assert.Contains(first.CharStart, await documents.GetBoundariesAsync(a.DocumentId, cancellationToken));
        ChunkDetail chunk = (await structure.GetChunkAsync(first.ChunkId, null, cancellationToken))!;

        ChunkLinkView link = Assert.Single(chunk.Links, static link => link.Kind == "link");
        Assert.Equal("A > 2 Second", Assert.Single(chunk.Links, static link => link.Kind == "mention").TargetHeading);
        Assert.Equal("B > Usage", link.TargetHeading);
        Assert.NotNull(chunk.NextChunkId);
        Assert.Null(chunk.PreviousChunkId);

        ChunkDetail usage = (await structure.GetChunkAsync(link.TargetChunkId!.Value, null, cancellationToken))!;
        Assert.Contains(usage.Backlinks, backlink => backlink.ChunkId == first.ChunkId && backlink.Label == "usage");
        IReadOnlyList<SectionView> sections = await structure.GetSectionsAsync(a.DocumentId, cancellationToken);
        Assert.Equal(["A", "A", "A > 2 Second"], sections.Select(static section => section.Path));
        Assert.Equal("2", sections[2].Number);
        Assert.Equal(sections[1].SectionId, sections[2].ParentId);

        DocumentSummary b = (await documents.FindAsync("gl", "docs/b.md", cancellationToken))!;
        AnchorView anchor = (await structure.GetAnchorAsync(b.DocumentId, "usage", cancellationToken))!;
        Assert.Equal(link.TargetChunkId, anchor.ChunkId);
        Assert.Null(await structure.GetAnchorAsync(b.DocumentId, "missing", cancellationToken));
    }

    [Fact]
    public async Task Registry_RejectsAServerWithOtherVectors()
    {
        ServiceProvider services = RequireDatabase();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        Assert.True((await services.GetRequiredService<EmbeddingModelRegistry>().GetStatusAsync(cancellationToken)).IsReady);

        using EmbeddingModelRegistry other = new(
            new FakeEmbeddingClient(1),
            services.GetRequiredService<EmbeddingStore>(),
            services.GetRequiredService<IOptionsMonitor<EmbeddingOptions>>(),
            TimeProvider.System,
            NullLogger<EmbeddingModelRegistry>.Instance);
        EmbeddingModelStatus status = await other.GetStatusAsync(cancellationToken);

        Assert.False(status.IsReady);
        Assert.Contains("cosine", status.Problem, StringComparison.Ordinal);
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

    // The pages by their addresses. A missing page fails like a server that answers 404.
    private sealed class FakePageClient : IPageClient
    {
        public Dictionary<string, string> Pages { get; } = new(StringComparer.Ordinal);

        public int Requests { get; private set; }

        public Task<WebPage> GetAsync(Uri address, long maxBytes, CancellationToken cancellationToken)
        {
            Requests++;
            return Pages.TryGetValue(address.AbsoluteUri, out string? page)
                ? Task.FromResult(new WebPage(System.Text.Encoding.UTF8.GetBytes(page), address))
                : throw new HttpRequestException("The server answered 404 Not Found.", null, System.Net.HttpStatusCode.NotFound);
        }
    }

    // A clock that moves only when the test moves it.
    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan time) => _now += time;
    }

    // A bag of words: each word adds 1 to a dimension from its hash. A salt gives another "model".
    // A request with the word RejectMe fails like a text that is too long for the server.
    private sealed class FakeEmbeddingClient(int salt) : IEmbeddingClient
    {
        private const int Dims = 64;

        public Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken) =>
            texts.Any(static text => text.Contains("RejectMe", StringComparison.Ordinal))
                ? throw new HttpRequestException("The input is too long.", null, System.Net.HttpStatusCode.InternalServerError)
                : Task.FromResult<IReadOnlyList<float[]>>([.. texts.Select(Embed)]);

        public Task<bool> IsHealthyAsync(CancellationToken cancellationToken) => Task.FromResult(true);

        private float[] Embed(string text)
        {
            float[] vector = new float[Dims];
            foreach (string word in Words(text))
                vector[(int)((uint)(StableHash(word) + salt) % Dims)] += 1;

            vector[0] += 0.01f;
            float norm = MathF.Sqrt(vector.Sum(static value => value * value));
            return [.. vector.Select(value => value / norm)];
        }
    }

    // The score of a text is the number of the words of the query in it.
    private sealed class FakeRerankClient : IRerankClient
    {
        public Task<IReadOnlyList<double>> RerankAsync(string query, IReadOnlyList<string> documents, CancellationToken cancellationToken)
        {
            HashSet<string> words = [.. Words(query)];
            return Task.FromResult<IReadOnlyList<double>>([.. documents.Select(document => (double)Words(document).Count(words.Contains))]);
        }

        public Task<bool> IsHealthyAsync(CancellationToken cancellationToken) => Task.FromResult(true);
    }

    private static string[] Words(string text) =>
        text.ToUpperInvariant().Split([' ', '\n', '.', ',', ':', '>', '#'], StringSplitOptions.RemoveEmptyEntries);

    private static int StableHash(string word)
    {
        unchecked
        {
            int hash = (int)2166136261;
            foreach (char c in word)
                hash = (hash ^ c) * 16777619;

            return hash & int.MaxValue;
        }
    }
}
