using System.Text.RegularExpressions;

namespace ProjectRag.Core.Sources;

/// <summary>
/// The fields of a new or a changed source.
/// </summary>
public sealed partial class SourceInput
{
    /// <summary>
    /// The patterns of the files that a new source reads when the input names none.
    /// </summary>
    public static IReadOnlyList<string> DefaultIncludeGlobs { get; } = ["**/*.md", "**/*.markdown", "**/*.txt", "**/*.rst", "**/*.adoc", "**/*.pdf"];

    /// <summary>
    /// The patterns of the files that a new source skips when the input names none: build output, dependencies and version control.
    /// </summary>
    public static IReadOnlyList<string> DefaultExcludeGlobs { get; } = ["**/.git/**", "**/node_modules/**", "**/bin/**", "**/obj/**", "**/artifacts/**", "**/.venv/**"];

    /// <summary>
    /// Gets or sets the short name: lowercase letters, digits, '.', '_' and '-', up to 64 characters.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the description.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Gets or sets the absolute path of the folder.
    /// </summary>
    public string RootPath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the patterns of the files to read. <see langword="null"/> or an empty list gives <see cref="DefaultIncludeGlobs"/>.
    /// </summary>
    public IReadOnlyList<string>? IncludeGlobs { get; set; }

    /// <summary>
    /// Gets or sets the patterns of the files to skip. <see langword="null"/> gives <see cref="DefaultExcludeGlobs"/>.
    /// </summary>
    public IReadOnlyList<string>? ExcludeGlobs { get; set; }

    /// <summary>
    /// Checks the fields.
    /// </summary>
    /// <returns>The errors by the name of the field. An empty dictionary means a valid input.</returns>
    public Dictionary<string, string[]> Validate()
    {
        Dictionary<string, string[]> errors = [];
        if (!NameRegex().IsMatch(Name))
            errors[nameof(Name)] = ["The name holds 1 to 64 characters: lowercase Latin letters, digits, '.', '_' and '-'. It starts with a letter or a digit."];

        if (!Path.IsPathFullyQualified(RootPath))
            errors[nameof(RootPath)] = ["The path of the folder must be absolute."];
        else if (!Directory.Exists(RootPath))
            errors[nameof(RootPath)] = [$"The folder {RootPath} does not exist."];

        if (Includes().Any(string.IsNullOrWhiteSpace) || Excludes().Any(string.IsNullOrWhiteSpace))
            errors[nameof(IncludeGlobs)] = ["A pattern must not be empty."];

        return errors;
    }

    /// <summary>
    /// Gets the patterns of the files to read with the defaults applied.
    /// </summary>
    /// <returns>The patterns.</returns>
    public IReadOnlyList<string> Includes() => IncludeGlobs is { Count: > 0 } globs ? globs : DefaultIncludeGlobs;

    /// <summary>
    /// Gets the patterns of the files to skip with the defaults applied.
    /// </summary>
    /// <returns>The patterns.</returns>
    public IReadOnlyList<string> Excludes() => ExcludeGlobs ?? DefaultExcludeGlobs;

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,63}$")]
    private static partial Regex NameRegex();
}
