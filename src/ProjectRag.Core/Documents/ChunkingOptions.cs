namespace ProjectRag.Core.Documents;

/// <summary>
/// The options of the chunker and the readers.
/// </summary>
public sealed class ChunkingOptions
{
    /// <summary>
    /// The configuration section that holds these options.
    /// </summary>
    public const string SectionName = "Chunking";

    /// <summary>
    /// Gets or sets the most characters in the body of a chunk.
    /// </summary>
    /// <remarks>
    /// A slot of the llama.cpp servers holds 2048 tokens. 1800 characters of English, Russian or code stay far below that with the heading.
    /// A change makes the indexer read every file again.
    /// </remarks>
    public int MaxChars { get; set; } = 1800;

    /// <summary>
    /// Gets or sets the most characters in the body of a chunk with code, a formula or a table. Such a block splits only beyond this size.
    /// </summary>
    /// <remarks>
    /// Code and TeX hold about three characters per token, so 3000 characters stay below the 2048 tokens of a slot. A change makes the indexer read every file again.
    /// </remarks>
    public int AtomicMaxChars { get; set; } = 3000;

    /// <summary>
    /// Gets or sets the most characters that a chunk repeats from the previous chunk when a block splits. A change makes the indexer read every file again.
    /// </summary>
    public int OverlapChars { get; set; } = 300;

    /// <summary>
    /// Gets or sets the size of the largest file that the indexer reads. A larger file is skipped with a warning.
    /// </summary>
    public long MaxFileBytes { get; set; } = 64L * 1024 * 1024;
}
