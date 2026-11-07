using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using ProjectRag.Code.Chunks;
using ProjectRag.Core.Code.Chunks;
using ProjectRag.Core.Code.Graph;

namespace ProjectRag.Code;

/// <summary>
/// Adds the Roslyn extractor of the code graph to a service collection.
/// </summary>
public static class CodeGraphServiceCollectionExtensions
{
    /// <summary>
    /// Registers the extractor and binds its options.
    /// </summary>
    /// <param name="services">The services.</param>
    /// <param name="configuration">The configuration with the section <c>CodeGraph</c>.</param>
    /// <returns>The services.</returns>
    public static IServiceCollection AddRoslynCodeGraph(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.Configure<CodeGraphOptions>(configuration.GetSection(CodeGraphOptions.SectionName));
        services.AddSingleton<RoslynCodeGraphExtractor>();
        services.AddSingleton<ICodeGraphExtractor>(provider => provider.GetRequiredService<RoslynCodeGraphExtractor>());
        services.AddSingleton<IIncrementalCodeGraphExtractor>(provider => provider.GetRequiredService<RoslynCodeGraphExtractor>());
        services.AddSingleton<ICodeChunker, CSharpCodeChunker>();
        return services;
    }
}
