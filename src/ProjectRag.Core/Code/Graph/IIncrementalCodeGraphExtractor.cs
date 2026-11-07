namespace ProjectRag.Core.Code.Graph;

/// <summary>
/// An extractor that keeps the loaded projects of a code base after a full build and updates the graph for changed files.
/// </summary>
/// <remarks>
/// <para>
/// A full build with <see cref="ICodeGraphExtractor.ExtractAsync"/> keeps its workspace for the root path and the properties of its input.
/// An update edits the texts of the changed files in that workspace and reads the graph of the files of the update again.
/// </para>
/// <para>
/// The caller decides which files an update reads: the changed files and the files that refer to symbols declared in them.
/// The caller sends only changes of the content of existing C# files. A new, deleted or renamed file, a change of a project file, a solution,
/// <c>global.json</c> or the properties needs a full build.
/// </para>
/// </remarks>
public interface IIncrementalCodeGraphExtractor : ICodeGraphExtractor
{
    /// <summary>
    /// Updates the kept workspace of a code base and reads the graph of some of its files.
    /// </summary>
    /// <param name="update">The input of the code base, the changed files and the files to read.</param>
    /// <param name="cancellationToken">The token that stops the work.</param>
    /// <returns>The patch, or <see langword="null"/> when no workspace is kept for the input. Then the caller runs a full build.</returns>
    Task<CodeGraphPatch?> UpdateAsync(CodeGraphUpdate update, CancellationToken cancellationToken);

    /// <summary>
    /// Drops the kept workspace of a code base, for example after its source is deleted.
    /// </summary>
    /// <param name="rootPath">The absolute path of the folder of the code base.</param>
    void Release(string rootPath);
}

/// <summary>
/// A request to update the graph of a code base.
/// </summary>
/// <param name="Input">The input of the code base, as for a full build. It must match the input of the kept workspace in its root path, its projects and its properties.</param>
/// <param name="ChangedFiles">The C# files whose content changed since the last build or update, relative to the folder, with forward slashes.</param>
/// <param name="Files">The files that the patch replaces: the changed files and the files that refer to their symbols. It holds <paramref name="ChangedFiles"/>.</param>
public sealed record CodeGraphUpdate(CodeGraphInput Input, IReadOnlyList<string> ChangedFiles, IReadOnlyList<string> Files);

/// <summary>
/// The new graph of some files of a code base.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Graph"/> lists all projects of the code base in the order of a full build, so a project index means the same project.
/// Its symbols are the symbols declared in <see cref="Files"/>, plus stubs: symbols declared elsewhere that the patch points to,
/// as the target of a reference, an edge or a link, or as the parent of a symbol. The store finds a stub by the path of its project and its documentation ID and writes no row for it.
/// A stub that the store cannot find drops the rows that point to it.
/// </para>
/// <para>
/// The declarations, references, type edges, external edges and member links of the patch all belong to <see cref="Files"/>: the store replaces the rows of these files with them.
/// A symbol of the code base that keeps no declaration after the replace is gone, with the rows that point to it.
/// </para>
/// </remarks>
/// <param name="Files">The files that the patch replaces.</param>
/// <param name="Graph">The graph of the files with the stubs. Its file projects and solutions are empty.</param>
/// <param name="Stubs">The indexes of the stub symbols in <c>Graph.Symbols</c>.</param>
/// <param name="CheckedProjects">The indexes of the projects whose compiler errors the patch counted again. Their error counts and diagnostics replace the stored ones; other projects keep theirs.</param>
public sealed record CodeGraphPatch(IReadOnlyList<string> Files, CodeGraph Graph, IReadOnlySet<int> Stubs, IReadOnlySet<int> CheckedProjects);
