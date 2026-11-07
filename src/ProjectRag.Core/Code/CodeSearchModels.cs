using ProjectRag.Core.Documents;

namespace ProjectRag.Core.Code;

/// <summary>
/// The request of the exact search over the files of the code sources.
/// </summary>
public sealed class CodeGrepRequest
{
    /// <summary>
    /// Gets or sets the text or the regular expression to find. It is one line.
    /// </summary>
    public string Pattern { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the key of the code source, or <see langword="null"/> to search all code sources. <see cref="Source"/> names the source by its name instead.
    /// </summary>
    public int? SourceId { get; set; }

    /// <summary>
    /// Gets or sets the name of the code source, or <see langword="null"/> to search all code sources.
    /// </summary>
    public string? Source { get; set; }

    /// <summary>
    /// Gets or sets a value that tells that <see cref="Pattern"/> is a regular expression of .NET. The default is a literal text.
    /// </summary>
    public bool IsRegex { get; set; }

    /// <summary>
    /// Gets or sets a value that tells that the case of letters matters. The default is <see langword="true"/>.
    /// </summary>
    public bool CaseSensitive { get; set; } = true;

    /// <summary>
    /// Gets or sets a pattern of the paths, for example <c>src/**/*.cs</c>. A pattern without a slash matches the name of a file at any depth.
    /// </summary>
    public string? Path { get; set; }

    /// <summary>
    /// Gets or sets the language of the files, for example <c>csharp</c>.
    /// </summary>
    public string? Language { get; set; }

    /// <summary>
    /// Gets or sets a value that tells whether the files of test projects take part. The default is <see langword="true"/>.
    /// A file without a graph has no owner project and always takes part.
    /// </summary>
    public bool IncludeTests { get; set; } = true;

    /// <summary>
    /// Gets or sets the most matches. The default is <see cref="CodeSearchService.DefaultLimit"/> and the largest is <see cref="CodeSearchService.MaxLimit"/>.
    /// </summary>
    public int? Limit { get; set; }
}

/// <summary>
/// A line of a file that matches.
/// </summary>
/// <param name="DocumentId">The key of the document.</param>
/// <param name="SourceId">The key of the source.</param>
/// <param name="Source">The name of the source.</param>
/// <param name="Path">The path of the file relative to the folder of the source.</param>
/// <param name="Language">The language of the file.</param>
/// <param name="Line">The number of the line, from 1.</param>
/// <param name="Column">The index of the match in the line, from 1, in UTF-16 units.</param>
/// <param name="Length">The length of the match in UTF-16 units.</param>
/// <param name="Text">A part of the line with the match, at most 400 characters.</param>
/// <param name="TextStart">The index of <paramref name="Text"/> in the line, from 0, in UTF-16 units. It is 0 unless the match lies beyond the first 400 characters.</param>
public sealed record CodeMatch(long DocumentId, int SourceId, string Source, string Path, string Language, int Line, int Column, int Length, string Text, int TextStart);

/// <summary>
/// The answer of the exact search.
/// </summary>
/// <param name="Matches">The matches in the order of the source names, the paths and the lines. A line has one match.</param>
/// <param name="FilesMatched">The number of files with a match.</param>
/// <param name="Truncated"><see langword="true"/> when more matches exist than the limit allowed.</param>
/// <param name="ElapsedMs">The time of the search in milliseconds.</param>
public sealed record CodeGrepResult(IReadOnlyList<CodeMatch> Matches, int FilesMatched, bool Truncated, long ElapsedMs);

/// <summary>
/// A range of lines of a file.
/// </summary>
/// <param name="Document">The document.</param>
/// <param name="Start">The number of the first line, from 1.</param>
/// <param name="End">The number of the last line. It is <c>Start - 1</c> when the range holds no line.</param>
/// <param name="LineCount">The number of lines of the file.</param>
/// <param name="Lines">The lines.</param>
public sealed record CodeLines(DocumentSummary Document, int Start, int End, int LineCount, IReadOnlyList<string> Lines);

/// <summary>
/// A line of a text that matches a pattern.
/// </summary>
/// <param name="Line">The number of the line, from 1.</param>
/// <param name="Column">The index of the match in the line, from 1.</param>
/// <param name="Length">The length of the match.</param>
/// <param name="Text">A part of the line with the match, at most <see cref="CodeLineMatcher.MaxLineText"/> characters.</param>
/// <param name="TextStart">The index of <paramref name="Text"/> in the line, from 0.</param>
public sealed record LineMatch(int Line, int Column, int Length, string Text, int TextStart = 0);

/// <summary>
/// A file of a code source that the exact search reads.
/// </summary>
public sealed class CodeFile
{
    /// <summary>Gets the key of the document.</summary>
    public long DocumentId { get; init; }

    /// <summary>Gets the key of the source.</summary>
    public int SourceId { get; init; }

    /// <summary>Gets the name of the source.</summary>
    public string Source { get; init; } = string.Empty;

    /// <summary>Gets the path relative to the folder of the source.</summary>
    public string Path { get; init; } = string.Empty;

    /// <summary>Gets the language.</summary>
    public string Language { get; init; } = string.Empty;

    /// <summary>Gets the exact text with the line ends "\n".</summary>
    public string Content { get; init; } = string.Empty;
}

/// <summary>
/// A request of the exact search that cannot run: a pattern that is empty or wrong, or a source that is not a code source.
/// </summary>
/// <param name="field">The name of the field of the request with the problem.</param>
/// <param name="message">The reason, for the user.</param>
public sealed class CodeSearchException(string field, string message) : Exception(message)
{
    /// <summary>
    /// Gets the name of the field of the request with the problem, for example <c>Pattern</c>.
    /// </summary>
    public string Field { get; } = field;
}
