namespace ProjectRag.Core.Sources;

/// <summary>
/// The kinds of a source.
/// </summary>
public static class SourceKind
{
    /// <summary>The kind of a source of documentation: folders and web pages. Every file becomes Markdown with chunks and vectors.</summary>
    public const string Docs = "docs";

    /// <summary>The kind of a local code base. See ADR 0003.</summary>
    public const string Code = "code";

    /// <summary>
    /// Checks whether a text names a kind.
    /// </summary>
    /// <param name="kind">The text.</param>
    /// <returns><see langword="true"/> for <see cref="Docs"/> and <see cref="Code"/>.</returns>
    public static bool IsValid(string? kind) => kind is Docs or Code;
}
