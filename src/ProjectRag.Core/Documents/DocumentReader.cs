namespace ProjectRag.Core.Documents;

/// <summary>
/// Reads a file of a source into a <see cref="DocumentText"/>.
/// </summary>
/// <remarks>
/// A PDF file goes through <see cref="PdfTextReader"/>. Any other file is text: Markdown, plain text, reStructuredText or AsciiDoc.
/// The chunker reads Markdown structure only, so the other text formats split at empty lines.
/// </remarks>
public static class DocumentReader
{
    /// <summary>
    /// The version of the readers and the chunker. A new version makes the indexer read every file again.
    /// </summary>
    public const int Version = 1;

    /// <summary>
    /// Reads a file.
    /// </summary>
    /// <param name="path">The path of the file. Its extension selects the reader, and its name is the title when the file has none.</param>
    /// <param name="bytes">The bytes of the file.</param>
    /// <returns>The text of the document.</returns>
    public static DocumentText Read(string path, byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(bytes);
        string name = Path.GetFileNameWithoutExtension(path);
        if (Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            return PdfTextReader.Read(bytes, name);

        string content = TextCleanup.Decode(bytes);
        return new DocumentText(MarkdownChunker.FindTitle(content) ?? name, content);
    }
}
