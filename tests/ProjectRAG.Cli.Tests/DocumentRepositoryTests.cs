using System.Collections.Immutable;
using FluentMigrator.Runner;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using ProjectRAG.Cli;
using ProjectRAG.Cli.Migrations;

namespace ProjectRAG.Cli.Tests;

/// <summary>Marks a test that needs PostgreSQL. The test is skipped when nobody answers on the connection string.
/// The answer is read once, because the attribute is built for every test at discovery.</summary>
public sealed class DatabaseFactAttribute : FactAttribute
{
    private static readonly Lazy<string?> Answer = new(Ask);

    public DatabaseFactAttribute()
    {
        if (Answer.Value is not null)
            Skip = Answer.Value;
    }

    private static string? Ask()
    {
        try
        {
            using NpgsqlConnection connection = new(DatabaseFixture.Connection(DatabaseFixture.MaintenanceDatabase));
            connection.Open();

            return null;
        }
        catch (NpgsqlException error)
        {
            return $"PostgreSQL is not available: {error.Message}";
        }
    }
}

/// <summary>Opens one data source for every test of the collection and brings the schema up to date.
/// The tests write into a database of their own, so a test run never touches the working index.</summary>
public sealed class DatabaseFixture : IDisposable
{
    public const string TestDatabase = "projectrag_tests";

    public const string MaintenanceDatabase = "postgres";

    public DatabaseFixture()
    {
        Create(TestDatabase);
        string connectionString = Connection(TestDatabase);

        ServiceCollection services = new();
        services.AddFluentMigratorCore().ConfigureRunner(runner => runner
            .AddPostgres15_0()
            .WithGlobalConnectionString(connectionString)
            .ScanIn(typeof(CreateDocumentsAndChunks).Assembly).For.Migrations());

        using ServiceProvider provider = services.BuildServiceProvider();
        provider.GetRequiredService<IMigrationRunner>().MigrateUp();

        Source = NpgsqlDataSource.Create(connectionString);
        Repository = new DocumentRepository(Source);
    }

    public NpgsqlDataSource Source { get; }

    public IDocumentRepository Repository { get; }

    /// <summary>The connection string of the settings, pointed at another database on the same server.</summary>
    public static string Connection(string database) => new NpgsqlConnectionStringBuilder(new DatabaseOptions().ConnectionString) { Database = database }.ConnectionString;

    private static void Create(string database)
    {
        using NpgsqlConnection connection = new(Connection(MaintenanceDatabase));
        connection.Open();

        using NpgsqlCommand known = new("SELECT 1 FROM pg_database WHERE datname = @name;", connection);
        known.Parameters.AddWithValue("name", database);
        if (known.ExecuteScalar() is not null)
            return;

        using NpgsqlCommand create = new($"CREATE DATABASE \"{database}\";", connection);
        create.ExecuteNonQuery();
    }

    public void Dispose() => Source.Dispose();
}

[Collection(IntegrationCollection.Name)]
public sealed class DocumentRepositoryTests(DatabaseFixture fixture) : IAsyncLifetime
{
    private readonly string _path = $"tests://{Guid.NewGuid():N}/notes.md";

    private static IndexedChunk Indexed(string source, int ordinal, string text) =>
        new(new Chunk(source, ["Graphics", "Vulkan"], text, ordinal * 10, ordinal * 10 + 2, text.Length), Vector(ordinal));

    /// <summary>A vector that points along one axis. Two of them are orthogonal, which is enough to tell them apart in a search.</summary>
    private static float[] Vector(int axis)
    {
        float[] vector = new float[Qwen3EmbeddingProvider.VectorSize];
        vector[axis % vector.Length] = 1f;

        return vector;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        StoredDocument? document = await fixture.Repository.FindAsync(_path);
        if (document is not null)
            await fixture.Repository.RemoveAsync(document.Id);
    }

    [DatabaseFact]
    public async Task SaveAddsTheDocumentAndFindReadsItBack()
    {
        StoredDocument saved = await fixture.Repository.SaveAsync(new NewDocument(_path, "notes", "hash-one", "the whole file"));

        Assert.True(saved.Id > 0);
        Assert.NotEqual(Guid.Empty, saved.Uuid);

        StoredDocument? found = await fixture.Repository.FindAsync(_path);

        Assert.NotNull(found);
        Assert.Equal(saved.Id, found.Id);
        Assert.Equal("hash-one", found.Hash);
        Assert.Equal("the whole file", found.Content);
    }

    [DatabaseFact]
    public async Task SaveReplacesTheDocumentThatHoldsThePath()
    {
        StoredDocument first = await fixture.Repository.SaveAsync(new NewDocument(_path, "notes", "hash-one", "old text"));
        StoredDocument second = await fixture.Repository.SaveAsync(new NewDocument(_path, "notes", "hash-two", "new text"));

        Assert.Equal(first.Id, second.Id);
        Assert.Equal("hash-two", second.Hash);
        Assert.Equal("new text", second.Content);
    }

    [DatabaseFact]
    public async Task SaveWithChunksWritesBothSides()
    {
        StoredDocument document = await fixture.Repository.SaveWithChunksAsync(new NewDocument(_path, "notes", "hash-one", "text"), [Indexed(_path, 0, "first"), Indexed(_path, 1, "second")]);

        Assert.True(document.Id > 0);
        Assert.Equal(2, await fixture.Repository.CountChunksAsync(document.Id));
    }

    [DatabaseFact]
    public async Task SaveWithChunksStoresNothingWhenAChunkFails()
    {
        IndexedChunk broken = new(new Chunk(_path, ["Graphics"], "text", 1, 2, 3), new float[8]);

        await Assert.ThrowsAsync<PostgresException>(() => fixture.Repository.SaveWithChunksAsync(new NewDocument(_path, "notes", "hash-one", "text"), [broken]));

        Assert.Null(await fixture.Repository.FindAsync(_path));
    }

    [DatabaseFact]
    public async Task SaveWithChunksKeepsTheOldHashWhenAChunkFails()
    {
        StoredDocument first = await fixture.Repository.SaveWithChunksAsync(new NewDocument(_path, "notes", "hash-one", "old text"), [Indexed(_path, 0, "first")]);
        IndexedChunk broken = new(new Chunk(_path, ["Graphics"], "text", 1, 2, 3), new float[8]);

        await Assert.ThrowsAsync<PostgresException>(() => fixture.Repository.SaveWithChunksAsync(new NewDocument(_path, "notes", "hash-two", "new text"), [broken]));

        StoredDocument? stored = await fixture.Repository.GetAsync(first.Id);

        Assert.NotNull(stored);
        Assert.Equal("hash-one", stored.Hash);
        Assert.Equal(1, await fixture.Repository.CountChunksAsync(first.Id));
    }

    [DatabaseFact]
    public async Task ReplaceChunksDropsTheOldOnesAndKeepsTheOrder()
    {
        StoredDocument document = await fixture.Repository.SaveAsync(new NewDocument(_path, "notes", "hash-one", "text"));
        await fixture.Repository.ReplaceChunksAsync(document.Id, [Indexed(_path, 0, "first"), Indexed(_path, 1, "second"), Indexed(_path, 2, "third")]);
        await fixture.Repository.ReplaceChunksAsync(document.Id, [Indexed(_path, 0, "only one")]);

        ImmutableArray<StoredChunk> chunks = await fixture.Repository.ReadChunksAsync(document.Id);

        Assert.Single(chunks);
        Assert.Equal(1, await fixture.Repository.CountChunksAsync(document.Id));
        Assert.Equal(0, chunks[0].Ordinal);
        Assert.Equal("only one", chunks[0].Content);
        Assert.Equal(["Graphics", "Vulkan"], chunks[0].HeadingPath.ToArray());
        Assert.Equal(0, chunks[0].StartLine);
        Assert.Equal(2, chunks[0].EndLine);
    }

    [DatabaseFact]
    public async Task ReplaceChunksWritesMoreThanOneStatement()
    {
        const int count = 250;
        StoredDocument document = await fixture.Repository.SaveAsync(new NewDocument(_path, "notes", "hash-one", "text"));
        await fixture.Repository.ReplaceChunksAsync(document.Id, [.. Enumerable.Range(0, count).Select(ordinal => Indexed(_path, ordinal, $"chunk number {ordinal}"))]);

        ImmutableArray<StoredChunk> chunks = await fixture.Repository.ReadChunksAsync(document.Id);

        Assert.Equal(count, chunks.Length);
        Assert.Equal(0, chunks[0].Ordinal);
        Assert.Equal(count - 1, chunks[^1].Ordinal);
        Assert.Equal($"chunk number {count - 1}", chunks[^1].Content);
    }

    [DatabaseFact]
    public async Task RemoveDropsTheDocumentWithItsChunks()
    {
        StoredDocument document = await fixture.Repository.SaveAsync(new NewDocument(_path, "notes", "hash-one", "text"));
        await fixture.Repository.ReplaceChunksAsync(document.Id, [Indexed(_path, 0, "first")]);

        Assert.True(await fixture.Repository.RemoveAsync(document.Id));
        Assert.Null(await fixture.Repository.GetAsync(document.Id));
        Assert.Empty(await fixture.Repository.ReadChunksAsync(document.Id));
        Assert.False(await fixture.Repository.RemoveAsync(document.Id));
    }

    [DatabaseFact]
    public async Task BrowseReturnsTheStoredDocument()
    {
        StoredDocument document = await fixture.Repository.SaveAsync(new NewDocument(_path, "notes", "hash-one", "text"));
        ImmutableArray<StoredDocument> documents = await fixture.Repository.BrowseAsync(0, 1000);

        Assert.Contains(documents, stored => stored.Id == document.Id);
    }

    [DatabaseFact]
    public async Task TextSearchFindsTheWordsOfTheChunk()
    {
        StoredDocument document = await fixture.Repository.SaveAsync(new NewDocument(_path, "notes", "hash-one", "text"));
        await fixture.Repository.ReplaceChunksAsync(document.Id, [Indexed(_path, 0, "the borscht simmers in a marmalade cauldron")]);

        ImmutableArray<SearchHit> hits = await fixture.Repository.SearchByTextAsync("marmalade cauldron", 5);

        Assert.Contains(hits, hit => hit.DocumentId == document.Id);
        SearchHit first = hits.First(hit => hit.DocumentId == document.Id);
        Assert.Equal(_path, first.Path);
        Assert.Equal(["Graphics", "Vulkan"], first.HeadingPath.ToArray());
        Assert.True(first.Score > 0);
    }

    [DatabaseFact]
    public async Task TextSearchFindsAWordOfTheHeadingPath()
    {
        string marker = $"marker{Guid.NewGuid():N}";
        StoredDocument document = await fixture.Repository.SaveAsync(new NewDocument(_path, "notes", "hash-one", "text"));
        IndexedChunk chunk = new(new Chunk(_path, ["Graphics", marker], "the body says nothing about the title", 1, 2, 8), Vector(7));
        await fixture.Repository.ReplaceChunksAsync(document.Id, [chunk]);

        ImmutableArray<SearchHit> hits = await fixture.Repository.SearchByTextAsync(marker, 5);

        Assert.Single(hits);
        Assert.Equal(document.Id, hits[0].DocumentId);
    }

    [DatabaseFact]
    public async Task VectorSearchPutsTheClosestChunkFirst()
    {
        StoredDocument document = await fixture.Repository.SaveAsync(new NewDocument(_path, "notes", "hash-one", "text"));
        await fixture.Repository.ReplaceChunksAsync(document.Id, [Indexed(_path, 3, "along one axis"), Indexed(_path, 4, "along another axis")]);

        ImmutableArray<SearchHit> hits = await fixture.Repository.SearchByVectorAsync(Vector(3), 1);

        Assert.Single(hits);
        Assert.Equal("along one axis", hits[0].Content);
        Assert.Equal(1d, hits[0].Score, 3);
    }
}
