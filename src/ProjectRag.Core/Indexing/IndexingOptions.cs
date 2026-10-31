namespace ProjectRag.Core.Indexing;

/// <summary>
/// The options of the indexer.
/// </summary>
public sealed class IndexingOptions
{
    /// <summary>
    /// The configuration section that holds these options.
    /// </summary>
    public const string SectionName = "Indexing";

    /// <summary>
    /// Gets or sets the interval of the scans of all sources in minutes. A scan reads only the files with a new size or time. 0 turns the scans off.
    /// </summary>
    public int ScanIntervalMinutes { get; set; } = 10;

    /// <summary>
    /// Gets or sets the number of chunks that the indexer reads for one round of embeddings.
    /// </summary>
    public int EmbeddingRoundSize { get; set; } = 128;
}
