namespace ProjectRag.Core.Code;

/// <summary>
/// Finds the data files of a code source: files of numbers or other data that give no useful vectors.
/// </summary>
public static class CodeDataFiles
{
    /// <summary>
    /// Checks whether a file is a data file. The indexer keeps its document for the exact search but makes no chunks for it.
    /// </summary>
    /// <remarks>
    /// Only the files of JSON, XML, YAML, TOML and text can be data files. Such a file is data when its longest line is longer than <see cref="CodeOptions.DataLineChars"/>.
    /// A font atlas or a minified file is one long line of numbers, and its chunks can be longer than the slot of the embedding server.
    /// </remarks>
    /// <param name="language">The identifier of the language of the file.</param>
    /// <param name="content">The text of the file.</param>
    /// <param name="options">The options of the code sources.</param>
    /// <returns><see langword="true"/> for a data file.</returns>
    public static bool IsData(string language, string content, CodeOptions options)
    {
        ArgumentNullException.ThrowIfNull(language);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(options);
        if (!CodeLanguages.IsConfig(language) && language != CodeLanguages.Text)
            return false;

        foreach (ReadOnlySpan<char> line in content.AsSpan().EnumerateLines())
        {
            if (line.Length > options.DataLineChars)
                return true;
        }

        return false;
    }
}
