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
    /// Code and TeX hold about three characters per token, so 3000 characters stay below the 2048 tokens of a slot.
    /// A digit counts as three characters, since a table of single digits holds one token per character. A change makes the indexer read every file again.
    /// </remarks>
    public int AtomicMaxChars { get; set; } = 3000;

    /// <summary>
    /// Gets or sets the most characters that a chunk repeats from the previous chunk when a block splits. A change makes the indexer read every file again.
    /// </summary>
    public int OverlapChars { get; set; } = 300;

    /// <summary>
    /// Gets or sets the size below which a chunk at the end of a section takes in the next small section of the same parent, with its heading in the text. A larger section starts a chunk of its own.
    /// </summary>
    /// <remarks>
    /// A specification has many short sections, such as "Revision" or "Contact" of an extension. A chunk of one line has no context for the search.
    /// A section whose subsections fit with it into one chunk also becomes one chunk, such as the short reference page of one function.
    /// 0 keeps every chunk inside one section. A change makes the indexer read every file again.
    /// </remarks>
    public int MinChars { get; set; } = 400;

    /// <summary>
    /// Gets or sets the size of the largest file that the indexer reads. A larger file is skipped with a warning.
    /// </summary>
    public long MaxFileBytes { get; set; } = 64L * 1024 * 1024;
}
