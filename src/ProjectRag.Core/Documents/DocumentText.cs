namespace ProjectRag.Core.Documents;

/// <summary>
/// The text of a document that a reader got from a file.
/// </summary>
/// <param name="Title">The title: the title of the front matter, the first top heading, the title of a PDF file or the file name.</param>
/// <param name="Content">The text in Markdown or plain text. Line ends are LF. The chunks point into this text.</param>
public sealed record DocumentText(string Title, string Content);
