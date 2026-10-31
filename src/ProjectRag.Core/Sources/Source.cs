namespace ProjectRag.Core.Sources;

/// <summary>
/// A source of documents: a folder on the host with the documentation of one project.
/// </summary>
public sealed class Source
{
    /// <summary>
    /// Gets the key of the source.
    /// </summary>
    public int SourceId { get; init; }

    /// <summary>
    /// Gets the short name that agents use, for example <c>axion</c>.
    /// </summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Gets the description for agents and people: what the documents are about.
    /// </summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>
    /// Gets the absolute path of the folder.
    /// </summary>
    public string RootPath { get; init; } = string.Empty;

    /// <summary>
    /// Gets the patterns of the files to read, for example <c>**/*.md</c>.
    /// </summary>
    public IReadOnlyList<string> IncludeGlobs { get; init; } = [];

    /// <summary>
    /// Gets the patterns of the files to skip, for example <c>**/node_modules/**</c>.
    /// </summary>
    public IReadOnlyList<string> ExcludeGlobs { get; init; } = [];

    /// <summary>
    /// Gets the rules that give the documents their categories, in their order. A document without a matching rule takes the name of the source.
    /// </summary>
    public IReadOnlyList<CategoryRule> CategoryRules { get; init; } = [];

    /// <summary>
    /// Gets the time when the source was added.
    /// </summary>
    public DateTime CreatedAt { get; init; }

    /// <summary>
    /// Gets the end of the last successful run of the indexer, or <see langword="null"/> before the first one.
    /// </summary>
    public DateTime? IndexedAt { get; init; }

    /// <summary>
    /// Gets the number of documents.
    /// </summary>
    public long DocumentCount { get; init; }

    /// <summary>
    /// Gets the number of chunks.
    /// </summary>
    public long ChunkCount { get; init; }

    /// <summary>
    /// Gets the number of chunks with a vector of the current embedding model.
    /// </summary>
    public long EmbeddedCount { get; init; }
}
