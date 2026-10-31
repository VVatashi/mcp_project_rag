namespace ProjectRag.Core.Sources;

/// <summary>
/// A rule of a source that gives the documents of some paths a category, for example <c>ECMA-33*.pdf</c> to <c>csharp</c>.
/// </summary>
public sealed class CategoryRule
{
    /// <summary>
    /// Gets or sets the pattern of the paths relative to the folder of the source, as in the patterns of the files.
    /// </summary>
    public string Pattern { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the category: lowercase letters, digits, '.', '_' and '-', as in the name of a source.
    /// </summary>
    public string Category { get; set; } = string.Empty;
}
