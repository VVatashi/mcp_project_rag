namespace ProjectRag.Core.Sources;

/// <summary>
/// The folder of a source, for the watcher.
/// </summary>
/// <param name="SourceId">The key of the source.</param>
/// <param name="Name">The name of the source.</param>
/// <param name="RootPath">The absolute path of the folder.</param>
/// <param name="DotGlobs">The patterns that bring dot files back into a code source.</param>
public sealed record SourceFolder(int SourceId, string Name, string RootPath, IReadOnlyList<string> DotGlobs);
