namespace ProjectRag.Core.Code.Chunks;

/// <summary>
/// Picks the chunker for the language of a file.
/// </summary>
/// <remarks>
/// The first registered chunker that supports the language wins. Any other language gets the <see cref="LineCodeChunker"/>.
/// </remarks>
/// <param name="registered">The chunkers from the container, in the order of their registration.</param>
public sealed class CodeChunkers(IEnumerable<ICodeChunker> registered)
{
    private readonly ICodeChunker[] _registered = [.. registered];

    /// <summary>
    /// Gets the chunker that splits any file, which the indexer also uses when another chunker fails on a file.
    /// </summary>
    public ICodeChunker Fallback { get; } = new LineCodeChunker();

    /// <summary>
    /// Picks the chunker for a language.
    /// </summary>
    /// <param name="language">The language from <see cref="CodeLanguages"/>.</param>
    /// <returns>The chunker.</returns>
    public ICodeChunker For(string language)
    {
        ArgumentNullException.ThrowIfNull(language);
        return _registered.FirstOrDefault(chunker => chunker.Supports(language)) ?? Fallback;
    }
}
