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
}
