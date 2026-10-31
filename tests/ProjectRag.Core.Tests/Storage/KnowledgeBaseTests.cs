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
            ["Embedding:Model"] = "fake",
            ["Rerank:Candidates"] = "10",
        }).Build();

        ServiceCollection services = new();
        services.AddLogging();
        services.AddKnowledgeBase(configuration);
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

        (IReadOnlyList<DocumentSummary> found, long total) = await services.GetRequiredService<DocumentStore>().ListAsync([], "textur", 0, 10, cancellationToken);
        Assert.Equal(1, total);
        Assert.Equal("textures.md", Assert.Single(found).Path);
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
        ChunkView first = (await documents.GetChunksAsync(a.DocumentId, null, cancellationToken))[0];
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
