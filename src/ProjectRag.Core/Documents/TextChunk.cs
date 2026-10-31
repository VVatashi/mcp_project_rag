namespace ProjectRag.Core.Documents;

/// <summary>
/// A part of a section of a document: the unit of the search.
/// </summary>
/// <param name="Ordinal">The number of the chunk in the document, from 0.</param>
/// <param name="Heading">The path of the section, for example <c>Title &gt; Section &gt; Subsection</c>.</param>
/// <param name="Body">The text of the chunk.</param>
/// <param name="Start">The index of the first character of the body in the content of the document.</param>
/// <param name="End">The index after the last character of the body.</param>
public sealed record TextChunk(int Ordinal, string Heading, string Body, int Start, int End)
{
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
