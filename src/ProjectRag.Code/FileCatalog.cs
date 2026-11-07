using System.Collections.Concurrent;

namespace ProjectRag.Code;

/// <summary>
/// Maps the absolute path of a file to its path in the list of known files.
/// </summary>
internal sealed class FileCatalog
{
    private readonly string _root;
    private readonly IReadOnlySet<string> _known;
    private readonly Lazy<Dictionary<string, string>> _foldedKnown;
    private readonly ConcurrentDictionary<string, string?> _cache = new(StringComparer.Ordinal);

    public FileCatalog(string root, IReadOnlySet<string> known)
    {
        _root = root;
        _known = known;
        _foldedKnown = new Lazy<Dictionary<string, string>>(() =>
        {
            Dictionary<string, string> folded = new(StringComparer.OrdinalIgnoreCase);
            foreach (string path in known.Order(StringComparer.Ordinal))
                folded.TryAdd(path, path);

            return folded;
        });
    }

    /// <summary>
    /// Gets the known path of a file.
    /// </summary>
    /// <param name="fullPath">The absolute path, or <see langword="null"/>.</param>
    /// <returns>The path as the list of known files spells it, or <see langword="null"/> for a file that is not known.</returns>
    public string? Resolve(string? fullPath)
    {
        if (string.IsNullOrEmpty(fullPath))
            return null;

        return _cache.GetOrAdd(fullPath, Compute);
    }

    private string? Compute(string fullPath)
    {
        string relative;
        try
        {
            relative = Path.GetRelativePath(_root, fullPath).Replace('\\', '/');
        }
        catch (ArgumentException)
        {
            return null;
        }

        if (relative == ".." || relative.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            return null;

        if (_known.Contains(relative))
            return relative;

        return OperatingSystem.IsWindows() && _foldedKnown.Value.TryGetValue(relative, out string? exact) ? exact : null;
    }
}
