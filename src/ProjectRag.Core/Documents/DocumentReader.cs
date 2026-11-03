using ProjectRag.Core.Documents.Html;
using ProjectRag.Core.Documents.Pdf;

namespace ProjectRag.Core.Documents;

/// <summary>
/// Reads a file or a web page of a source into Markdown, the intermediate form of every document.
/// </summary>
/// <remarks>
/// A PDF file goes through <see cref="PdfMarkdownImporter"/>, and an HTML file or a web page through <see cref="HtmlMarkdownImporter"/>.
/// Any other file is text: Markdown, plain text, reStructuredText or AsciiDoc.
/// <see cref="MarkdownStructureReader"/> reads Markdown structure only, so the other text formats split at empty lines.
/// </remarks>
public static class DocumentReader
{
    /// <summary>
    /// The version of the importers, the structure reader and the chunker. A new version makes the indexer read every file again.
    /// </summary>
    public const int Version = 14;

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase) { ".md", ".markdown", ".txt", ".rst", ".adoc" };

    /// <summary>
    /// Reads a file or a web page.
    /// </summary>
    /// <param name="path">The path of the file, or the address of a web page. Its extension selects the importer, and its name is the title when the document has none.
    /// A web page without the extension of another format is HTML.</param>
    /// <param name="bytes">The bytes of the file.</param>
    /// <returns>The text of the document in Markdown.</returns>
    public static DocumentText Read(string path, byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(bytes);
        Uri? address = IsWebPage(path) ? new Uri(path) : null;
        string localPath = address is null ? path : Uri.UnescapeDataString(address.AbsolutePath);
        string extension = Path.GetExtension(localPath);
        string name = Path.GetFileNameWithoutExtension(localPath) is { Length: > 0 } fileName ? fileName : address?.Host ?? path;
        if (extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            return PdfMarkdownImporter.Read(bytes, name);

        if (extension.Equals(".html", StringComparison.OrdinalIgnoreCase) || extension.Equals(".htm", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".xhtml", StringComparison.OrdinalIgnoreCase) || (address is not null && !TextExtensions.Contains(extension)))
            return HtmlMarkdownImporter.Read(bytes, name, address);

        string content = TextCleanup.Decode(bytes);
        return new DocumentText(MarkdownStructureReader.FindTitle(content) ?? name, content);
    }

    /// <summary>
    /// Checks whether the path of a document is the address of a web page.
    /// </summary>
    /// <param name="path">The path of the document.</param>
    /// <returns><see langword="true"/> for an address of http or https.</returns>
    public static bool IsWebPage(string path) =>
        path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || path.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
}
