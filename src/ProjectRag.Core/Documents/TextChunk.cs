namespace ProjectRag.Core.Documents;

/// <summary>
/// A part of a section of a document: the unit of the search.
/// </summary>
/// <param name="Ordinal">The number of the chunk in the document, from 0.</param>
/// <param name="Heading">The path of the section, for example <c>Title &gt; Section &gt; Subsection</c>.</param>
/// <param name="Body">The Markdown text of the chunk without the service marks.</param>
/// <param name="Start">The index of the first character of the source range in the content of the document.</param>
/// <param name="End">The index after the last character of the source range.</param>
/// <param name="Section">The index of the section in <see cref="DocumentStructure.Sections"/>.</param>
/// <param name="PageStart">The first page of the chunk, or <see langword="null"/> for a document without pages.</param>
/// <param name="PageEnd">The last page of the chunk, or <see langword="null"/> for a document without pages.</param>
/// <param name="OverlapChars">The number of characters at the start of the source range that the previous chunk also holds.</param>
public sealed record TextChunk(int Ordinal, string Heading, string Body, int Start, int End, int Section = 0, int? PageStart = null, int? PageEnd = null, int OverlapChars = 0)
{
    /// <summary>The separator of the headings in <see cref="Heading"/>.</summary>
    public const string HeadingSeparator = " > ";

    /// <summary>
    /// Gets the text that the embedding model and the reranker see: the heading, an empty line and the body.
    /// </summary>
    public string EmbeddingText => Compose(Heading, Body);

    /// <summary>
    /// Joins a heading and a body the way <see cref="EmbeddingText"/> does.
    /// </summary>
    /// <param name="heading">The path of the section.</param>
    /// <param name="body">The text of the chunk.</param>
    /// <returns>The text for the models.</returns>
    public static string Compose(string heading, string body) => heading.Length == 0 ? body : $"{heading}\n\n{body}";
}
