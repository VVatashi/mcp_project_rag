namespace ProjectRag.Core.Sources;

/// <summary>
/// Finds the files of a source folder that are links to a place outside the folder.
/// </summary>
/// <remarks>
/// A link to a file outside the folder could bring a private file, such as a key, into the index and into the answers of the search.
/// The guard resolves the final target of the file and of each folder above it, and compares the real path with the real path of the folder.
/// A link to a file inside the folder is fine. The guard caches the real paths of the folders, so create one guard for each run.
/// A guard is not safe for several threads.
/// </remarks>
internal sealed class SourceLinkGuard
{
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private readonly string _rootPath;
    private readonly string _rootPrefix;
    private readonly Dictionary<string, string> _folders;

    /// <summary>
    /// Creates a guard for a folder.
    /// </summary>
    /// <param name="rootPath">The folder of the source.</param>
    public SourceLinkGuard(string rootPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(rootPath);
        _folders = new Dictionary<string, string>(PathComparison == StringComparison.Ordinal ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);
        _rootPath = Path.GetFullPath(rootPath);
        string real = RealFolder(Path.TrimEndingDirectorySeparator(_rootPath));
        _rootPrefix = Path.EndsInDirectorySeparator(real) ? real : real + Path.DirectorySeparatorChar;
    }

    /// <summary>
    /// Checks that the real file behind a path lies inside the folder.
    /// </summary>
    /// <param name="relativePath">The path of the file relative to the folder.</param>
    /// <returns><see langword="false"/> for a link, or a file under a linked folder, whose final target is outside the folder or cannot be resolved.</returns>
    public bool IsInside(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        try
        {
            string full = Path.GetFullPath(Path.Combine(_rootPath, relativePath));
            string? folder = Path.GetDirectoryName(full);
            string real = folder is null ? full : Path.Combine(RealFolder(folder), Path.GetFileName(full));
            FileInfo info = new(real);
            if (IsReparsePoint(info) && info.ResolveLinkTarget(returnFinalTarget: true) is { } target)
            {
                string? targetFolder = Path.GetDirectoryName(target.FullName);
                real = targetFolder is null ? target.FullName : Path.Combine(RealFolder(targetFolder), target.Name);
            }

            return real.StartsWith(_rootPrefix, PathComparison);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsReparsePoint(FileSystemInfo info)
    {
        FileAttributes attributes = info.Attributes;
        return attributes != (FileAttributes)(-1) && attributes.HasFlag(FileAttributes.ReparsePoint);
    }

    // Resolves the links of a folder from the top down, so a link in the middle of the path counts.
    private string RealFolder(string folder)
    {
        if (_folders.TryGetValue(folder, out string? known))
            return known;

        string? parent = Path.GetDirectoryName(folder);
        string real = folder;
        if (parent is not null)
        {
            string candidate = Path.Combine(RealFolder(parent), Path.GetFileName(folder));
            DirectoryInfo info = new(candidate);
            real = IsReparsePoint(info) && info.ResolveLinkTarget(returnFinalTarget: true) is { } target
                ? RealFolder(Path.TrimEndingDirectorySeparator(target.FullName))
                : candidate;
        }

        _folders[folder] = real;
        return real;
    }
}
