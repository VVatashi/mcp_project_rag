using System.Collections.Immutable;
using System.Diagnostics;
using FluentMigrator.Exceptions;
using FluentMigrator.Runner;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Configuration;
using Microsoft.Extensions.Options;
using Npgsql;
using ProjectRAG.Cli.Migrations;

namespace ProjectRAG.Cli;

internal static class Program
{
    private const int MaxTextLength = 100;

    private const int SearchLimit = 5;

    /// <summary>Reads appsettings.json and the local file that overrides it. The local file is optional and stays out of the repository.</summary>
    private static IConfigurationRoot ReadConfiguration() => new ConfigurationBuilder()
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
        .AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false)
        .Build();

    /// <summary>Registers the settings, the console logger, the tokenizer, the embedding model, the database and the migrations. The heavy parts are single instances that live as long as the application.</summary>
    private static ServiceProvider CreateServices()
    {
        IConfigurationRoot configuration = ReadConfiguration();

        ServiceCollection services = new();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging(logging => logging.AddConfiguration(configuration.GetSection("Logging")).AddSimpleConsole(console => console.SingleLine = true));
        services.AddOptions<EmbeddingOptions>().Bind(configuration.GetSection(EmbeddingOptions.SectionName))
            .Validate(options => options.BatchSize > 0, "Embedding:BatchSize has to be greater than zero.")
            .Validate(options => !string.IsNullOrWhiteSpace(options.ModelFile) && !string.IsNullOrWhiteSpace(options.TokenizerFile), "Embedding:ModelFile and Embedding:TokenizerFile have to name a file.");
        services.AddOptions<ChunkOptions>().Bind(configuration.GetSection(ChunkOptions.SectionName))
            .Validate(options => options.MaxTokens > 0, "Chunking:MaxTokens has to be greater than zero.");
        services.AddOptions<DatabaseOptions>().Bind(configuration.GetSection(DatabaseOptions.SectionName))
            .Validate(options => !string.IsNullOrWhiteSpace(options.ConnectionString), "Database:ConnectionString has to be filled in.");
        services.AddSingleton(provider => Qwen3EmbeddingProvider.CreateTokenizer(provider.GetRequiredService<IOptions<EmbeddingOptions>>().Value));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IDocumentSplitter, MarkdownSplitter>());
        services.AddSingleton(provider => NpgsqlDataSource.Create(provider.GetRequiredService<IOptions<DatabaseOptions>>().Value.ConnectionString));
        services.AddSingleton<IDocumentRepository, DocumentRepository>();
        services.AddSingleton<DocumentIndexer>();
        services.AddSingleton<ISearchService, SearchService>();
        services.AddSingleton<Qwen3EmbeddingProvider>();
        services.AddSingleton<IEmbeddingProvider>(provider => provider.GetRequiredService<Qwen3EmbeddingProvider>());

        // The model costs a second and almost two gigabytes, so a full text search never asks for it.
        services.AddSingleton(provider => new Lazy<IEmbeddingProvider>(provider.GetRequiredService<IEmbeddingProvider>));

        services.AddFluentMigratorCore().ConfigureRunner(runner => runner
            .AddPostgres15_0()
            .WithGlobalConnectionString(provider => provider.GetRequiredService<IOptions<DatabaseOptions>>().Value.ConnectionString)
            .ScanIn(typeof(CreateDocumentsAndChunks).Assembly).For.Migrations());

        return services.BuildServiceProvider();
    }

    /// <summary>Brings the database up to the newest migration.</summary>
    private static void Migrate(IServiceProvider services)
    {
        using IServiceScope scope = services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IMigrationRunner>().MigrateUp();
    }

    private static string Flatten(string text)
    {
        string flat = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return flat.Length > MaxTextLength ? $"{flat[..MaxTextLength]}..." : flat;
    }

    private static string Headings(ImmutableArray<string> path) => path.Length > 0 ? string.Join(" / ", path) : "(preamble)";

    private static void WriteChunks(ILogger logger, ImmutableArray<StoredChunk> chunks)
    {
        foreach (StoredChunk chunk in chunks)
        {
            logger.LogInformation("  #{Ordinal} lines {Start}-{End} ({Tokens} tokens) {Path}", chunk.Ordinal, chunk.StartLine, chunk.EndLine, chunk.TokenCount, Headings(chunk.HeadingPath));
            logger.LogInformation("    {Text}", Flatten(chunk.Content));
        }
    }

    private static void WriteHits(ILogger logger, ImmutableArray<SearchHit> hits)
    {
        foreach (SearchHit hit in hits)
        {
            logger.LogInformation("  {Score:F4} {Path}:{Start}-{End} {Headings}", hit.Score, hit.Path, hit.StartLine, hit.EndLine, Headings(hit.HeadingPath));
            logger.LogInformation("    {Text}", Flatten(hit.Content));
        }
    }

    private static async Task<int> IndexAsync(IServiceProvider services, ILogger logger, string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            logger.LogError("File not found: {Path}", path);
            return 1;
        }

        DocumentIndexer indexer;
        Stopwatch load = Stopwatch.StartNew();
        try
        {
            indexer = services.GetRequiredService<DocumentIndexer>();
        }
        catch (FileNotFoundException error)
        {
            logger.LogError(error, "Model file is missing");
            return 1;
        }

        load.Stop();
        logger.LogInformation("Model loaded in {Seconds:F1} s", load.Elapsed.TotalSeconds);

        IndexResult result;
        try
        {
            result = await indexer.IndexAsync(path, cancellationToken);
        }
        catch (NotSupportedException error)
        {
            logger.LogError("{Message}", error.Message);
            return 1;
        }

        WriteChunks(logger, await services.GetRequiredService<IDocumentRepository>().ReadChunksAsync(result.DocumentId, cancellationToken));
        return 0;
    }

    private static async Task<int> SearchAsync(IServiceProvider services, ILogger logger, string query, int limit, bool byVector, CancellationToken cancellationToken)
    {
        ISearchService search = services.GetRequiredService<ISearchService>();
        ImmutableArray<SearchHit> hits = byVector
            ? await search.SearchVectorAsync(query, limit, cancellationToken)
            : await search.SearchTextAsync(query, limit, cancellationToken);

        WriteHits(logger, hits);
        return 0;
    }

    private static void WriteUsage(ILogger logger) => logger.LogInformation("Usage: ProjectRAG.Cli help | migrate | index <path> | search <query> [limit] | search-text <query> [limit]");

    /// <summary>The number of hits a search returns. The optional third argument overrides the default.</summary>
    private static int Limit(string[] args) => args.Length == 3 && int.TryParse(args[2], out int limit) && limit > 0 ? limit : SearchLimit;

    private static async Task<int> Main(string[] args)
    {
        using ServiceProvider services = CreateServices();
        ILogger logger = services.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(Program));

        if (args.Length == 0)
        {
            WriteUsage(logger);
            return 1;
        }

        using CancellationTokenSource cancellation = new();
        Console.CancelKeyPress += (_, stop) =>
        {
            stop.Cancel = true;
            cancellation.Cancel();
            logger.LogWarning("Stopping");
        };

        try
        {
            switch (args[0])
            {
                case "help" when args.Length == 1:
                    WriteUsage(logger);
                    return 0;

                case "migrate" when args.Length == 1:
                    Migrate(services);
                    logger.LogInformation("The database holds the newest schema");
                    return 0;

                case "index" when args.Length == 2:
                    Migrate(services);
                    return await IndexAsync(services, logger, args[1], cancellation.Token);

                case "search" when args.Length is 2 or 3:
                    return await SearchAsync(services, logger, args[1], Limit(args), byVector: true, cancellation.Token);

                case "search-text" when args.Length is 2 or 3:
                    return await SearchAsync(services, logger, args[1], Limit(args), byVector: false, cancellation.Token);

                default:
                    logger.LogError("Unknown command or wrong number of arguments: {Command}", args[0]);
                    WriteUsage(logger);
                    return 1;
            }
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Stopped before the work was done");
            return 1;
        }
        catch (OptionsValidationException error)
        {
            logger.LogError("{Message}", string.Join(" ", error.Failures));
            return 1;
        }
        catch (PostgresException error) when (error.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            logger.LogError("The schema is missing. Run ProjectRAG.Cli migrate first.");
            return 1;
        }
        catch (Exception error) when (error is UndeterminableConnectionException or NpgsqlException)
        {
            logger.LogError("The database is not available: {Message}", error.Message);
            return 1;
        }
    }
}
