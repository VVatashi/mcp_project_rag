using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

using Npgsql;

using ProjectRag.Core.Documents;
using ProjectRag.Core.Embeddings;
using ProjectRag.Core.Indexing;
using ProjectRag.Core.Reranking;
using ProjectRag.Core.Search;
using ProjectRag.Core.Sources;
using ProjectRag.Core.Status;
using ProjectRag.Core.Storage;

namespace ProjectRag.Core;

/// <summary>
/// Adds the knowledge base to a service collection.
/// </summary>
public static class KnowledgeBaseServiceCollectionExtensions
{
    /// <summary>
    /// Adds the database, the llama.cpp clients, the indexer with its background worker and the search.
    /// </summary>
    /// <param name="services">The services.</param>
    /// <param name="configuration">The configuration with the connection string <c>ProjectRag</c> and the sections of the options.</param>
    /// <returns>The services.</returns>
    /// <exception cref="InvalidOperationException">The configuration has no connection string.</exception>
    public static IServiceCollection AddKnowledgeBase(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        string connectionString = configuration.GetConnectionString(Database.ConnectionStringName) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"The configuration has no connection string ConnectionStrings:{Database.ConnectionStringName}.");

        Database.ConfigureDapper();
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString));
        services.AddSingleton<Database>();

        services.Configure<ChunkingOptions>(configuration.GetSection(ChunkingOptions.SectionName));
        services.Configure<EmbeddingOptions>(configuration.GetSection(EmbeddingOptions.SectionName));
        services.Configure<RerankOptions>(configuration.GetSection(RerankOptions.SectionName));
        services.Configure<IndexingOptions>(configuration.GetSection(IndexingOptions.SectionName));
        services.Configure<SearchOptions>(configuration.GetSection(SearchOptions.SectionName));

        services.AddHttpClient(LlamaEmbeddingClient.HttpClientName, static (provider, http) =>
            http.Timeout = TimeSpan.FromSeconds(Math.Max(1, provider.GetRequiredService<IOptions<EmbeddingOptions>>().Value.TimeoutSeconds)));
        services.AddHttpClient(LlamaRerankClient.HttpClientName, static (provider, http) =>
            http.Timeout = TimeSpan.FromSeconds(Math.Max(1, provider.GetRequiredService<IOptions<RerankOptions>>().Value.TimeoutSeconds)));
        // A page of documentation has no reason to take long. The name in User-Agent lets the owner of a site see who reads it.
        services.AddHttpClient(HttpPageClient.HttpClientName, static http =>
        {
            http.Timeout = TimeSpan.FromSeconds(60);
            http.DefaultRequestHeaders.UserAgent.ParseAdd("ProjectRag/1.0");
        });
        services.AddSingleton<IEmbeddingClient, LlamaEmbeddingClient>();
        services.AddSingleton<IPageClient, HttpPageClient>();
        services.AddSingleton<IRerankClient, LlamaRerankClient>();

        services.AddSingleton<SourceStore>();
        services.AddSingleton<DocumentStore>();
        services.AddSingleton<StructureStore>();
        services.AddSingleton<EmbeddingStore>();
        services.AddSingleton<IndexRunStore>();
        services.AddSingleton<SearchStore>();
        services.AddSingleton<EmbeddingModelRegistry>();
        services.AddSingleton<Indexer>();
        services.AddSingleton<IndexScheduler>();
        services.AddHostedService<IndexWorker>();
        services.AddSingleton<SearchService>();
        services.AddSingleton<StatusService>();
        return services;
    }
}
