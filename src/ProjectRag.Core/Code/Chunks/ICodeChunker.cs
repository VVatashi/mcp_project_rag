namespace ProjectRag.Core.Code.Chunks;

/// <summary>
/// Splits the text of a code file into chunks for the vector and token search.
/// </summary>
/// <remarks>
/// A chunk is an exact range of the text, so its lines match the file. The heading gives the model the context that the range lacks: the path, the type and the member.
/// The indexer takes the first registered chunker that supports the language of a file, and the chunker of lines for any other file.
/// </remarks>
public interface ICodeChunker
{
    /// <summary>
    /// Gets the version of the chunker. A new version splits every file of its languages again.
    /// </summary>
    int Version { get; }

    /// <summary>
    /// Checks whether the chunker splits files of a language.
    /// </summary>
    /// <param name="language">The language from <see cref="CodeLanguages"/>.</param>
    /// <returns><see langword="true"/> when the chunker supports the language.</returns>
    bool Supports(string language);

    /// <summary>
    /// Splits the text of a file.
    /// </summary>
    /// <param name="path">The path of the file relative to the folder of the source, with forward slashes.</param>
    /// <param name="content">The text with the line ends "\n", as the document stores it.</param>
    /// <param name="options">The limits of the chunks.</param>
    /// <returns>The chunks in the order of the text. An empty or blank text gives no chunks.</returns>
    IReadOnlyList<CodeChunk> Split(string path, string content, CodeChunkingOptions options);
}

/// <summary>
/// A chunk of a code file: a range of its text with a heading.
/// </summary>
/// <param name="Heading">The context of the range, such as <c>src/Scene/SceneHost.cs &gt; Axion.Scene.SceneHost &gt; public void Update(double time)</c>. The parts join with " &gt; ".</param>
/// <param name="Start">The index of the first character in the text.</param>
/// <param name="End">The index after the last character. The body of the chunk is <c>content[Start..End]</c>.</param>
/// <param name="LineStart">The line of <paramref name="Start"/>, from 1.</param>
/// <param name="LineEnd">The line of the last character, from 1.</param>
/// <param name="OverlapChars">The number of characters at the start that the previous chunk also holds, for a part of a long member.</param>
public sealed record CodeChunk(string Heading, int Start, int End, int LineStart, int LineEnd, int OverlapChars = 0);

/// <summary>
/// The limits of the chunks of code files.
/// </summary>
public sealed class CodeChunkingOptions
{
    /// <summary>
    /// The configuration section that holds these options.
    /// </summary>
    public const string SectionName = "CodeChunking";

    /// <summary>
    /// Gets or sets the most characters of a chunk body. Code has about 3 characters per token, so 2400 characters stay well within a slot of 2048 tokens of the embedding server.
    /// </summary>
    public int MaxChars { get; set; } = 2400;

    /// <summary>
    /// Gets or sets the size under which neighbor chunks of one type or one block merge into one chunk.
    /// </summary>
    public int MinChars { get; set; } = 400;

    /// <summary>
    /// Gets or sets the number of lines that a part of a long member repeats from the part before it.
    /// </summary>
    public int OverlapLines { get; set; } = 2;

    /// <summary>
    /// Gets the text of the options for the version of a file, so a change of a limit splits the files again.
    /// </summary>
    /// <returns>The text.</returns>
    public string Fingerprint() => $"{MaxChars};{MinChars};{OverlapLines}";
}
