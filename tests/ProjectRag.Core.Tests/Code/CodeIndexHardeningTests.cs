using System.Text;

using Dapper;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Npgsql;

using ProjectRag.Core.Code;
using ProjectRag.Core.Embeddings;
using ProjectRag.Core.Indexing;
using ProjectRag.Core.Reranking;
using ProjectRag.Core.Sources;
using ProjectRag.Core.Storage;

namespace ProjectRag.Core.Tests.Code;

/// <summary>
/// Runs the indexer and the exact search against a real PostgreSQL: the links outside the folder, the files that are never read, and the early stop of the search.
/// </summary>
/// <remarks>
/// The tests use the variable PROJECTRAG_TEST_DATABASE like the other tests of the database, but a database of their own with the suffix <c>_hardening</c>.
/// Without the variable they skip themselves.
/// </remarks>
public sealed class CodeIndexHardeningTests : IAsyncLifetime
{
    private const string VariableName = "PROJECTRAG_TEST_DATABASE";

    private readonly string _root = Directory.CreateTempSubdirectory("project-rag-hardening-").FullName;
    private readonly string _outside = Directory.CreateTempSubdirectory("project-rag-outside-").FullName;
    private ServiceProvider? _services;

    public async ValueTask InitializeAsync()
    {
        string? connectionString = Environment.GetEnvironmentVariable(VariableName);
        if (string.IsNullOrEmpty(connectionString))
            return;

        NpgsqlConnectionStringBuilder builder = new(connectionString);
        string database = (builder.Database ?? throw new InvalidOperationException($"{VariableName} names no database.")) + "_hardening";
        builder.Database = "postgres";
        await using (NpgsqlConnection admin = new(builder.ConnectionString))
        {
            await admin.ExecuteAsync($"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE)");
            await admin.ExecuteAsync($"CREATE DATABASE \"{database}\"");
        }

        builder.Database = database;
        NpgsqlConnection.ClearAllPools();
        DatabaseMigrator.MigrateUp(builder.ConnectionString, NullLoggerFactory.Instance);
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:ProjectRag"] = builder.ConnectionString,
            ["Embedding:Model"] = "fake",
        }).Build();

        ServiceCollection services = new();
        services.AddLogging();
        services.AddKnowledgeBase(configuration);
        services.AddSingleton<IPageClient, NoPageClient>();
        services.AddSingleton<IEmbeddingClient, BagEmbeddingClient>();
        services.AddSingleton<IRerankClient, TokenRerankClient>();
        _services = services.BuildServiceProvider();
    }

    public async ValueTask DisposeAsync()
    {
        if (_services is not null)
            await _services.DisposeAsync();

        Directory.Delete(_root, recursive: true);
        Directory.Delete(_outside, recursive: true);
    }

    [Fact]
    public async Task Indexer_SkipsALinkToAFileOutsideTheFolderOfADocumentationSource()
    {
        ServiceProvider services = RequireDatabase();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        string secret = Path.Combine(_outside, "key.md");
        File.WriteAllText(secret, "# Key\n\nThe private key is hidden here.\n");
        Write("real.md", "# Real\n\nThe real text of the guide.\n");
        CreateLink("docs/key.md", secret);
        CreateLink("alias.md", "real.md");
        int sourceId = await services.GetRequiredService<SourceStore>().AddAsync(new SourceInput { Name = "docs", RootPath = _root }, cancellationToken);

        IndexRun run = (await services.GetRequiredService<Indexer>().RunAsync(sourceId, null, cancellationToken))!;

        Assert.Equal(IndexRun.Succeeded, run.Status);
        Assert.Equal(["alias.md", "real.md"], await ReadPathsAsync(services, cancellationToken));
    }

    [Fact]
    public async Task Indexer_SkipsALinkToAFileOutsideTheFolderOfACodeSource()
    {
        ServiceProvider services = RequireDatabase();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        string secret = Path.Combine(_outside, "id_key");
        File.WriteAllText(secret, "PRIVATE KEY\n");
        Write("real.txt", "The real text.\n");
        CreateLink("docs/key.txt", secret);
        CreateLink("alias.txt", "real.txt");
        int sourceId = await services.GetRequiredService<SourceStore>().AddAsync(new SourceInput { Name = "demo", Kind = SourceKind.Code, RootPath = _root }, cancellationToken);

        IndexRun run = (await services.GetRequiredService<Indexer>().RunAsync(sourceId, null, cancellationToken))!;

        Assert.Equal(IndexRun.Succeeded, run.Status);
        Assert.Equal(["alias.txt", "real.txt"], await ReadPathsAsync(services, cancellationToken));
        Assert.Empty((await services.GetRequiredService<CodeSearchService>().GrepAsync(new CodeGrepRequest { Pattern = "PRIVATE KEY" }, cancellationToken)).Matches);
    }

    [Fact]
    public async Task Indexer_DoesNotOpenABinaryFileOrATextWithNulAgain()
    {
        ServiceProvider services = RequireDatabase();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        Write("src/a.txt", "The text of a.\n");
        string binary = Path.Combine(_root, "assets", "blob.dat");
        string nul = Path.Combine(_root, "assets", "nul.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(binary)!);
        File.WriteAllBytes(binary, [0x89, 0x50, 0, 1, 2]);
        File.WriteAllBytes(nul, [.. Encoding.UTF8.GetBytes(new string('a', CodeText.BinaryProbeBytes + 100)), 0, .. Encoding.UTF8.GetBytes("tail\n")]);
        int sourceId = await services.GetRequiredService<SourceStore>().AddAsync(new SourceInput { Name = "demo", Kind = SourceKind.Code, RootPath = _root }, cancellationToken);
        Indexer indexer = services.GetRequiredService<Indexer>();

        IndexRun first = (await indexer.RunAsync(sourceId, null, cancellationToken))!;

        Assert.Equal((IndexRun.Succeeded, 1, 0), (first.Status, first.DocumentsAdded, first.DocumentsFailed));
        IndexRun second;
        using (FileStream lockBinary = new(binary, FileMode.Open, FileAccess.Read, FileShare.None))
        using (FileStream lockNul = new(nul, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Throws<IOException>(() => File.OpenRead(binary).Dispose());
            second = (await indexer.RunAsync(sourceId, null, cancellationToken))!;
        }

        Assert.Equal((IndexRun.Succeeded, 0, 1), (second.Status, second.DocumentsFailed, second.DocumentsUnchanged));

        File.WriteAllText(binary, "Now a text.\n");
        IndexRun third = (await indexer.RunAsync(sourceId, null, cancellationToken))!;

        Assert.Equal((1, 0), (third.DocumentsAdded, third.DocumentsFailed));
        Assert.Equal(["assets/blob.dat", "src/a.txt"], await ReadPathsAsync(services, cancellationToken));
    }

    [Fact]
    public async Task Grep_StopsAfterTheBatchThatFillsTheLimit()
    {
        ServiceProvider services = RequireDatabase();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        const int files = 120;
        for (int i = 0; i < files; i++)
            Write($"src/f{i:000}.txt", $"first line\nthe needle {i}\n");

        await services.GetRequiredService<SourceStore>().AddAsync(new SourceInput { Name = "demo", Kind = SourceKind.Code, RootPath = _root }, cancellationToken);
        await services.GetRequiredService<Indexer>().RunAsync(1, null, cancellationToken);
        CodeSearchStore store = services.GetRequiredService<CodeSearchStore>();
        CodeSearchService code = services.GetRequiredService<CodeSearchService>();

        foreach (bool isRegex in new[] { false, true })
        {
            string pattern = isRegex ? "need+le" : "needle";
            long before = store.DocumentsRead;
            CodeGrepResult limited = await code.GrepAsync(new CodeGrepRequest { Pattern = pattern, IsRegex = isRegex, Limit = 3 }, cancellationToken);

            Assert.Equal(["src/f000.txt", "src/f001.txt", "src/f002.txt"], limited.Matches.Select(static match => match.Path));
            Assert.True(limited.Truncated);
            Assert.Equal(CodeSearchStore.BatchSize, store.DocumentsRead - before);

            before = store.DocumentsRead;
            CodeGrepResult all = await code.GrepAsync(new CodeGrepRequest { Pattern = pattern, IsRegex = isRegex, Limit = CodeSearchService.MaxLimit }, cancellationToken);

            Assert.Equal(files, all.Matches.Count);
            Assert.Equal(files, all.FilesMatched);
            Assert.False(all.Truncated);
            Assert.Equal(Enumerable.Range(0, files).Select(static i => $"src/f{i:000}.txt"), all.Matches.Select(static match => match.Path));
            Assert.All(all.Matches, static match => Assert.Equal(2, match.Line));
            Assert.Equal(files, store.DocumentsRead - before);
        }

        long beforePath = store.DocumentsRead;
        CodeGrepResult one = await code.GrepAsync(new CodeGrepRequest { Pattern = "needle", Path = "src/f007.txt" }, cancellationToken);

        Assert.Equal("src/f007.txt", Assert.Single(one.Matches).Path);
        Assert.Equal(1, store.DocumentsRead - beforePath);
    }

    [Fact]
    public async Task Grep_RejectsAPatternWithANulCharacter()
    {
        ServiceProvider services = RequireDatabase();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        CodeSearchService code = services.GetRequiredService<CodeSearchService>();

        foreach (bool isRegex in new[] { false, true })
        {
            CodeSearchException error = await Assert.ThrowsAsync<CodeSearchException>(() => code.GrepAsync(new CodeGrepRequest { Pattern = "a\0b", IsRegex = isRegex }, cancellationToken));

            Assert.Equal("Pattern", error.Field);
        }
    }

    private static async Task<List<string>> ReadPathsAsync(ServiceProvider services, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await services.GetRequiredService<Database>().OpenAsync(cancellationToken);
        return [.. await connection.QueryAsync<string>(new CommandDefinition("SELECT path FROM document ORDER BY path COLLATE \"C\"", cancellationToken: cancellationToken))];
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

    // Windows denies symbolic links to a process without the right of the developer mode.
    private void CreateLink(string path, string target)
    {
        string full = Path.Combine(_root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        try
        {
            File.CreateSymbolicLink(full, target);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            Assert.Skip($"The system does not allow a symbolic link: {exception.Message}");
        }
    }
}
