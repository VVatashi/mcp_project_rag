using ProjectRag.Core.Documents.Pdf;

namespace ProjectRag.Core.Documents;

/// <summary>
/// Reads a file of a source into Markdown, the intermediate form of every document.
/// </summary>
/// <remarks>
/// A PDF file goes through <see cref="PdfMarkdownImporter"/>. Any other file is text: Markdown, plain text, reStructuredText or AsciiDoc.
/// <see cref="MarkdownStructureReader"/> reads Markdown structure only, so the other text formats split at empty lines.
/// </remarks>
public static class DocumentReader
{
    /// <summary>
    /// The version of the importers, the structure reader and the chunker. A new version makes the indexer read every file again.
    /// </summary>
    public const int Version = 9;

    /// <summary>
    /// Reads a file.
    /// </summary>
    /// <param name="path">The path of the file. Its extension selects the importer, and its name is the title when the file has none.</param>
    /// <param name="bytes">The bytes of the file.</param>
    /// <returns>The text of the document in Markdown.</returns>
    public static DocumentText Read(string path, byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(bytes);
        string name = Path.GetFileNameWithoutExtension(path);
        if (Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            return PdfMarkdownImporter.Read(bytes, name);

        string content = TextCleanup.Decode(bytes);
        return new DocumentText(MarkdownStructureReader.FindTitle(content) ?? name, content);
    }
}
