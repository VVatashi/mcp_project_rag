using Microsoft.Extensions.FileSystemGlobbing;

namespace ProjectRag.Core.Sources;

/// <summary>
/// Finds the files of a source.
/// </summary>
public static class SourceScanner
{
    /// <summary>
    /// Lists the files under the folder of a source that match its patterns.
    /// </summary>
    /// <param name="source">The source.</param>
    /// <returns>The paths relative to the folder, with forward slashes, in ordinal order.</returns>
    /// <exception cref="DirectoryNotFoundException">The folder does not exist.</exception>
    public static IReadOnlyList<string> Scan(Source source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!Directory.Exists(source.RootPath))
            throw new DirectoryNotFoundException($"The folder {source.RootPath} of the source {source.Name} does not exist.");

        // The patterns of the users come from Windows and from Linux, so the case of a name does not matter.
        Matcher matcher = new(StringComparison.OrdinalIgnoreCase);
        matcher.AddIncludePatterns(source.IncludeGlobs);
        matcher.AddExcludePatterns(source.ExcludeGlobs);
        return [.. matcher.GetResultsInFullPath(source.RootPath)
            .Select(path => Path.GetRelativePath(source.RootPath, path).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)];
    }

    /// <summary>
    /// Gives a document of a source its category: the category of the first rule whose pattern matches the path, or the name of the source.
    /// </summary>
    /// <param name="source">The source.</param>
    /// <param name="path">The path of the document relative to the folder of the source.</param>
    /// <returns>The category.</returns>
    public static string Category(Source source, string path)
    {
        ArgumentNullException.ThrowIfNull(source);
        foreach (CategoryRule rule in source.CategoryRules)
        {
            Matcher matcher = new(StringComparison.OrdinalIgnoreCase);
            matcher.AddInclude(rule.Pattern);
            if (matcher.Match(path).HasMatches)
                return rule.Category;
        }

        return source.Name;
    }
}
