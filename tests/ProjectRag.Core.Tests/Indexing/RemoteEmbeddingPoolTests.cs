using System.Text;
using System.Text.Json.Nodes;

using Dapper;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Npgsql;

using ProjectRag.Core.Embeddings;
using ProjectRag.Core.Indexing;
using ProjectRag.Core.Sources;
using ProjectRag.Core.Storage;

namespace ProjectRag.Core.Tests.Indexing;

/// <summary>
/// Runs the indexer with a local and a remote fake embedding backend against a real PostgreSQL with pgvector.
/// </summary>
/// <remarks>
/// The tests use the variable PROJECTRAG_TEST_DATABASE like the other tests of the database, but a database of their own with the suffix <c>_remote</c>.
/// Without the variable they skip themselves.
/// </remarks>
public sealed class RemoteEmbeddingPoolTests : IAsyncLifetime
{
    private const string VariableName = "PROJECTRAG_TEST_DATABASE";
    private const int Sections = 40;

    private readonly string _root = Directory.CreateTempSubdirectory("project-rag-remote-").FullName;
    private string? _connectionString;

    public async ValueTask InitializeAsync()
    {
        string? connectionString = Environment.GetEnvironmentVariable(VariableName);
        if (string.IsNullOrEmpty(connectionString))
            return;

        NpgsqlConnectionStringBuilder builder = new(connectionString);
        string database = (builder.Database ?? throw new InvalidOperationException($"{VariableName} names no database.")) + "_remote";
        builder.Database = "postgres";
        await using (NpgsqlConnection admin = new(builder.ConnectionString))
        {
            await admin.ExecuteAsync($"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE)");
            await admin.ExecuteAsync($"CREATE DATABASE \"{database}\"");
        }

        NpgsqlConnection.ClearAllPools();
        builder.Database = database;
        _connectionString = builder.ConnectionString;
        DatabaseMigrator.MigrateUp(_connectionString, NullLoggerFactory.Instance);
    }

    public ValueTask DisposeAsync()
    {
        Directory.Delete(_root, recursive: true);
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task ListedSource_UsesBothBackendsAndStoresAllVectors()
    {
        FakeLocal local = new();
        FakeRemote remote = new();
        await using ServiceProvider services = Create(local, remote);
        int sourceId = await AddSourceAsync(services, "listed", Sections);

        IndexRun run = await RunAsync(services, sourceId);

        Assert.Equal(IndexRun.Succeeded, run.Status);
        Assert.Null(run.Warning);
        Assert.True(local.Texts > 0);
        Assert.True(remote.Texts > 0);
        Assert.Equal(Sections, local.Texts + remote.Texts);
        Assert.Equal(Sections, run.ChunksEmbedded);
        Assert.Equal(remote.Texts, run.ChunksEmbeddedRemote);
        Assert.Equal(remote.Batches * remote.CostPerBatch, run.RemoteCost);
        await AssertAllEmbeddedAsync(services, sourceId);

        IndexRun stored = (await services.GetRequiredService<IndexRunStore>().ListAsync(sourceId, 1, TestContext.Current.CancellationToken))[0];
        Assert.Equal((run.ChunksEmbeddedRemote, run.RemoteCost), (stored.ChunksEmbeddedRemote, stored.RemoteCost));
        Assert.True(services.GetRequiredService<RemoteEmbeddingGuard>().Last!.Ok);
    }

    [Fact]
    public async Task UnlistedSource_UsesTheLocalBackendOnly()
    {
        FakeLocal local = new();
        FakeRemote remote = new();
        await using ServiceProvider services = Create(local, remote);
        int sourceId = await AddSourceAsync(services, "unlisted", 12);

        IndexRun run = await RunAsync(services, sourceId);

        Assert.Equal(IndexRun.Succeeded, run.Status);
        Assert.Equal(12, run.ChunksEmbedded);
        Assert.Equal((0, 0m), (run.ChunksEmbeddedRemote, run.RemoteCost));
        Assert.Equal(0, remote.Calls);
        await AssertAllEmbeddedAsync(services, sourceId);
    }

    [Fact]
    public async Task RemoteFailure_FallsBackToTheLocalBackendForTheRestOfTheRun()
    {
        FakeLocal local = new();
        FakeRemote remote = new() { FailAfterBatches = 2 };
        await using ServiceProvider services = Create(local, remote);
        int sourceId = await AddSourceAsync(services, "listed", Sections);

        IndexRun run = await RunAsync(services, sourceId);

        Assert.Equal(IndexRun.Succeeded, run.Status);
        Assert.Contains("The remote embedding stopped", run.Warning, StringComparison.Ordinal);
        Assert.Equal(Sections, run.ChunksEmbedded);
        Assert.Equal(remote.Texts, run.ChunksEmbeddedRemote);
        Assert.True(remote.Batches <= 2);
        // Each of the two remote workers may meet the failure once. The second round of the run never calls the backend.
        Assert.True(remote.FailedCalls is >= 1 and <= 2);
        Assert.Equal(Sections - remote.Texts, local.Texts);
        await AssertAllEmbeddedAsync(services, sourceId);
    }

    [Fact]
    public async Task CostLimit_StopsTheRemoteWorkers()
    {
        FakeLocal local = new();
        FakeRemote remote = new() { CostPerBatch = 0.6m };
        await using ServiceProvider services = Create(local, remote, new()
        {
            ["Embedding:Remote:Parallelism"] = "1",
            ["Embedding:Remote:BatchSize"] = "3",
            ["Embedding:Remote:MaxCostPerRun"] = "1.0",
        });
        int sourceId = await AddSourceAsync(services, "listed", Sections);

        IndexRun run = await RunAsync(services, sourceId);

        Assert.Equal(IndexRun.Succeeded, run.Status);
        Assert.Contains("limit of 1 USD", run.Warning, StringComparison.Ordinal);
        Assert.Equal(2, remote.Batches);
        Assert.Equal(1.2m, run.RemoteCost);
        Assert.Equal(6, run.ChunksEmbeddedRemote);
        Assert.Equal(Sections, run.ChunksEmbedded);
        Assert.Equal(Sections - 6, local.Texts);
        await AssertAllEmbeddedAsync(services, sourceId);
    }

    [Fact]
    public async Task BilledInvalidAnswer_CountsItsCostAndStopsTheRemoteBackend()
    {
        FakeLocal local = new();
        FakeRemote remote = new() { BilledFailureCost = 0.4m };
        await using ServiceProvider services = Create(local, remote);
        int sourceId = await AddSourceAsync(services, "listed", Sections);

        IndexRun run = await RunAsync(services, sourceId);

        Assert.Equal(IndexRun.Succeeded, run.Status);
        Assert.Contains("The remote embedding stopped", run.Warning, StringComparison.Ordinal);
        Assert.True(remote.FailedCalls is >= 1 and <= 2);
        Assert.Equal(remote.FailedCalls * 0.4m, run.RemoteCost);
        Assert.Equal((0, Sections), (run.ChunksEmbeddedRemote, run.ChunksEmbedded));
        await AssertAllEmbeddedAsync(services, sourceId);
    }

    [Fact]
    public async Task CostLimit_UsesTheTokensTimesThePriceWhenTheApiReportsNoCost()
    {
        FakeLocal local = new();
        string variable = "PROJECTRAG_TEST_KEY_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(variable, "test-key-for-the-fake-api");
        try
        {
            // Each request reports 600000 tokens at 1 USD per million tokens: 0.6 USD, and no cost.
            FakeApi api = new(usage: """{"prompt_tokens":600000}""");
            await using ServiceProvider services = Create(local, RealRemote(api, variable, 1.0m), LimitSettings());
            int sourceId = await AddSourceAsync(services, "listed", Sections);

            IndexRun run = await RunAsync(services, sourceId);

            Assert.Equal(IndexRun.Succeeded, run.Status);
            Assert.Contains("limit of 1 USD", run.Warning, StringComparison.Ordinal);
            Assert.Equal(2, api.Batches);
            Assert.Equal(1.2m, run.RemoteCost);
            Assert.Equal((6, Sections), (run.ChunksEmbeddedRemote, run.ChunksEmbedded));
            await AssertAllEmbeddedAsync(services, sourceId);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    [Fact]
    public async Task CostLimit_UsesTheCharacterEstimateWhenTheApiReportsNoUsage()
    {
        FakeLocal local = new();
        string variable = "PROJECTRAG_TEST_KEY_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(variable, "test-key-for-the-fake-api");
        try
        {
            // At 1000000 USD per million tokens, each estimated token costs 1 USD, so one batch passes the limit.
            FakeApi api = new(usage: null);
            await using ServiceProvider services = Create(local, RealRemote(api, variable, 1_000_000m), LimitSettings());
            int sourceId = await AddSourceAsync(services, "listed", Sections);

            IndexRun run = await RunAsync(services, sourceId);

            Assert.Equal(IndexRun.Succeeded, run.Status);
            Assert.Contains("limit of 1 USD", run.Warning, StringComparison.Ordinal);
            Assert.Equal(1, api.Batches);
            Assert.True(run.RemoteCost > 1m);
            Assert.Equal((3, Sections), (run.ChunksEmbeddedRemote, run.ChunksEmbedded));
            await AssertAllEmbeddedAsync(services, sourceId);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    [Fact]
    public async Task FailedGuard_KeepsTheRemoteBackendOff()
    {
        FakeLocal local = new();
        FakeRemote remote = new() { Negate = true };
        await using ServiceProvider services = Create(local, remote);
        int sourceId = await AddSourceAsync(services, "listed", 12);

        IndexRun run = await RunAsync(services, sourceId);

        Assert.Equal(IndexRun.Succeeded, run.Status);
        Assert.Contains("The remote embedding is not used", run.Warning, StringComparison.Ordinal);
        Assert.Contains("other vectors", run.Warning, StringComparison.Ordinal);
        Assert.Equal(0, remote.Texts);
        Assert.Equal((12, 0), (run.ChunksEmbedded, run.ChunksEmbeddedRemote));
        Assert.False(services.GetRequiredService<RemoteEmbeddingGuard>().Last!.Ok);
        await AssertAllEmbeddedAsync(services, sourceId);
    }

    [Fact]
    public async Task MissingKey_KeepsTheRemoteBackendOffWithAReason()
    {
        FakeLocal local = new();
        FakeRemote remote = new() { Unavailable = "The environment variable OPENROUTER_API_KEY holds no API key." };
        await using ServiceProvider services = Create(local, remote);
        int sourceId = await AddSourceAsync(services, "listed", 6);

        IndexRun run = await RunAsync(services, sourceId);

        Assert.Contains("holds no API key", run.Warning, StringComparison.Ordinal);
        Assert.Equal((6, 0), (run.ChunksEmbedded, run.ChunksEmbeddedRemote));
        Assert.Equal(0, remote.Calls);
    }

    [Fact]
    public async Task LocalServerDown_LetsTheRemoteBackendEmbedAlone()
    {
        FakeLocal local = new();
        FakeRemote remote = new();
        await using ServiceProvider services = Create(local, remote);
        int sourceId = await AddSourceAsync(services, "listed", 6);
        Assert.Equal(IndexRun.Succeeded, (await RunAsync(services, sourceId)).Status);

        // The model is stored now. The server goes down, and the registry forgets its last answer.
        local.Down = true;
        services.GetRequiredService<EmbeddingModelRegistry>().Invalidate();
        Write("listed", "more.md", Section(100, 18));
        remote.Reset();
        IndexRun run = await RunAsync(services, sourceId);

        Assert.Equal(IndexRun.Succeeded, run.Status);
        Assert.Equal(18, run.ChunksEmbedded);
        Assert.Equal(18, run.ChunksEmbeddedRemote);
        Assert.Equal(18, remote.Texts);
        await AssertAllEmbeddedAsync(services, sourceId);
    }

    [Fact]
    public async Task LocalServerDownMidRun_LeavesTheRestToTheRemoteBackend()
    {
        FakeLocal local = new() { DownAfterCalls = 2 };
        FakeRemote remote = new();
        await using ServiceProvider services = Create(local, remote);
        int sourceId = await AddSourceAsync(services, "listed", Sections);

        IndexRun run = await RunAsync(services, sourceId);

        Assert.Equal(IndexRun.Succeeded, run.Status);
        Assert.Contains("The local embedding server stopped", run.Warning, StringComparison.Ordinal);
        Assert.Equal(Sections, run.ChunksEmbedded);
        Assert.Equal(remote.Texts, run.ChunksEmbeddedRemote);
        await AssertAllEmbeddedAsync(services, sourceId);
    }

    [Fact]
    public async Task LocalServerDownWithoutTheRemoteBackend_StoresNoVectors()
    {
        FakeLocal local = new() { Down = true };
        FakeRemote remote = new();
        await using ServiceProvider services = Create(local, remote);
        int sourceId = await AddSourceAsync(services, "unlisted", 6);

        IndexRun run = await RunAsync(services, sourceId);

        Assert.Equal(IndexRun.Succeeded, run.Status);
        Assert.Contains("The chunks got no vectors", run.Warning, StringComparison.Ordinal);
        Assert.Equal(0, run.ChunksEmbedded);
        Assert.Equal(0, remote.Calls);
    }

    private static Dictionary<string, string?> LimitSettings() => new()
    {
        ["Embedding:Remote:Parallelism"] = "1",
        ["Embedding:Remote:BatchSize"] = "3",
        ["Embedding:Remote:MaxCostPerRun"] = "1.0",
    };

    private static RemoteEmbeddingClient RealRemote(FakeApi api, string variable, decimal price) =>
        new(new FakeHttpClientFactory(api), new FixedOptions<RemoteEmbeddingOptions>(new RemoteEmbeddingOptions { Enabled = true, ApiKeyVariable = variable, PricePerMillionTokens = price, Retries = 0 }));

    private ServiceProvider Create(FakeLocal local, IRemoteEmbeddingClient remote, Dictionary<string, string?>? overrides = null)
    {
        Assert.SkipWhen(_connectionString is null, $"Set {VariableName} to run the tests of the database.");
        Dictionary<string, string?> settings = new()
        {
            ["ConnectionStrings:ProjectRag"] = _connectionString,
            ["Chunking:MaxChars"] = "200",
            ["Chunking:MinChars"] = "0",
            ["Embedding:Model"] = "fake",
            ["Embedding:BatchSize"] = "2",
            ["Embedding:Parallelism"] = "1",
            ["Embedding:Remote:Enabled"] = "true",
            ["Embedding:Remote:Sources:0"] = "listed",
            ["Embedding:Remote:Parallelism"] = "2",
            ["Embedding:Remote:BatchSize"] = "3",
            ["Indexing:EmbeddingRoundSize"] = "4",
        };
        foreach ((string key, string? value) in overrides ?? [])
            settings[key] = value;

        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        ServiceCollection services = new();
        services.AddLogging();
        services.AddKnowledgeBase(configuration);
        services.AddSingleton<IEmbeddingClient>(local);
        services.AddSingleton<IRemoteEmbeddingClient>(remote);
        return services.BuildServiceProvider();
    }

    private async Task<int> AddSourceAsync(ServiceProvider services, string name, int sections)
    {
        Write(name, "sections.md", Section(0, sections));
        return await services.GetRequiredService<SourceStore>().AddAsync(new SourceInput { Name = name, RootPath = Path.Combine(_root, name) }, TestContext.Current.CancellationToken);
    }

    private static string Section(int first, int count)
    {
        StringBuilder text = new("# Title\n\n");
        for (int i = first; i < first + count; i++)
            text.Append(System.Globalization.CultureInfo.InvariantCulture, $"## Section {i}\n\nThe text of the section {i} holds the unique word w{i}x.\n\n");

        return text.ToString();
    }

    private void Write(string name, string file, string text)
    {
        string folder = Path.Combine(_root, name);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, file), text);
    }

    private static async Task<IndexRun> RunAsync(ServiceProvider services, int sourceId) =>
        (await services.GetRequiredService<Indexer>().RunAsync(sourceId, null, TestContext.Current.CancellationToken))!;

    private static async Task AssertAllEmbeddedAsync(ServiceProvider services, int sourceId)
    {
        Source source = (await services.GetRequiredService<SourceStore>().GetAsync(sourceId, 1, TestContext.Current.CancellationToken))!;
        Assert.True(source.ChunkCount > 0);
        Assert.Equal(source.ChunkCount, source.EmbeddedCount);
    }

    // The vectors of both fake backends come from one function, so the guard sees the same model.
    private static class FakeVectors
    {
        public const int Dims = 16;

        public static float[] Embed(string text)
        {
            float[] vector = new float[Dims];
            foreach (char c in text)
                vector[(c * 7) % Dims] += 1;

            vector[0] += 0.01f;
            float norm = MathF.Sqrt(vector.Sum(static value => value * value));
            return [.. vector.Select(value => value / norm)];
        }

        public static bool IsProbe(string text) => text.StartsWith("The probe of the knowledge base", StringComparison.Ordinal);
    }

    private sealed class FakeLocal : IEmbeddingClient
    {
        private int _calls;
        private int _texts;

        public bool Down { get; set; }

        public int DownAfterCalls { get; init; } = 1_000_000;

        public int Texts => Volatile.Read(ref _texts);

        public async Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken)
        {
            int call = Interlocked.Increment(ref _calls);
            if (Down || call > DownAfterCalls + 1)
                throw new HttpRequestException("The local server is down.");

            await Task.Delay(20, cancellationToken);
            if (!texts.Any(FakeVectors.IsProbe))
                Interlocked.Add(ref _texts, texts.Count);

            return [.. texts.Select(FakeVectors.Embed)];
        }

        public Task<bool> IsHealthyAsync(CancellationToken cancellationToken) => Task.FromResult(!Down);
    }

    // The fake HTTP side of the real remote client: it answers with the vectors of FakeVectors and a fixed usage.
    private sealed class FakeApi(string? usage) : HttpMessageHandler
    {
        private int _batches;

        public int Batches => Volatile.Read(ref _batches);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string body = await request.Content!.ReadAsStringAsync(cancellationToken);
            string[] texts = [.. JsonNode.Parse(body)!["input"]!.AsArray().Select(static item => item!.GetValue<string>())];
            if (!texts.Any(FakeVectors.IsProbe))
                Interlocked.Increment(ref _batches);

            JsonArray data = [.. texts.Select((text, index) => new JsonObject
            {
                ["index"] = index,
                ["embedding"] = new JsonArray([.. FakeVectors.Embed(text).Select(static value => (JsonNode?)value)]),
            })];
            JsonObject answer = new() { ["data"] = data };
            if (usage is not null)
                answer["usage"] = JsonNode.Parse(usage);

            string json = answer.ToJsonString();
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class FakeRemote : IRemoteEmbeddingClient
    {
        private int _calls;
        private int _texts;
        private int _batches;
        private int _failed;
        private int _started;

        public string? Unavailable { get; init; }

        public bool Negate { get; init; }

        public decimal CostPerBatch { get; init; } = 0.01m;

        public int FailAfterBatches { get; init; } = int.MaxValue;

        public decimal? BilledFailureCost { get; init; }

        public int Calls => Volatile.Read(ref _calls);

        public int Texts => Volatile.Read(ref _texts);

        public int Batches => Volatile.Read(ref _batches);

        public int FailedCalls => Volatile.Read(ref _failed);

        public void Reset() => (_calls, _texts, _batches, _failed, _started) = (0, 0, 0, 0, 0);

        public async Task<RemoteEmbedding> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            bool probe = texts.Any(FakeVectors.IsProbe);
            if (!probe && BilledFailureCost is { } billed)
            {
                Interlocked.Increment(ref _failed);
                throw new RemoteEmbeddingException("The API sent 0 vectors for 3 texts.", 10, billed);
            }

            if (!probe && Interlocked.Increment(ref _started) > FailAfterBatches)
            {
                Interlocked.Increment(ref _failed);
                throw new HttpRequestException("The API answered 500.", null, System.Net.HttpStatusCode.InternalServerError);
            }

            await Task.Delay(20, cancellationToken);
            float[][] vectors = [.. texts.Select(text => FakeVectors.Embed(text).Select(value => Negate ? -value : value).ToArray())];
            if (probe)
                return new RemoteEmbedding(vectors, 5, 0);

            Interlocked.Increment(ref _batches);
            Interlocked.Add(ref _texts, texts.Count);
            return new RemoteEmbedding(vectors, texts.Count * 10, CostPerBatch);
        }
    }
}
