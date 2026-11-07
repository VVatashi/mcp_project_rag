using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

using ProjectRag.Core.Code.Graph;

namespace ProjectRag.Code;

/// <summary>
/// Applies changed files to a kept workspace and reads the graph of some files again.
/// </summary>
internal sealed class GraphUpdater(KeptWorkspace entry, CodeGraphOptions options)
{
    /// <summary>
    /// Reads the patch. The caller holds the gate of the entry.
    /// </summary>
    /// <param name="update">The update.</param>
    /// <param name="cancellationToken">The token that stops the work.</param>
    /// <returns>The patch, or <see langword="null"/> when a changed file cannot be read.</returns>
    public async Task<CodeGraphPatch?> UpdateAsync(CodeGraphUpdate update, CancellationToken cancellationToken)
    {
        string root = Path.GetFullPath(update.Input.RootPath);
        List<string> files = [.. update.Files.Concat(update.ChangedFiles).Distinct(StringComparer.Ordinal)];
        List<string> changed = [.. update.ChangedFiles.Distinct(StringComparer.Ordinal)];

        Solution solution = entry.Solution;
        foreach (string file in changed)
        {
            if (!entry.FileDocuments.TryGetValue(file, out List<DocumentId>? ids))
                continue;

            SourceText? text = await ReadAsync(Path.GetFullPath(Path.Combine(root, file)), cancellationToken).ConfigureAwait(false);
            if (text is null)
                return null;

            foreach (DocumentId id in ids)
                solution = solution.WithDocumentText(id, text, PreservationMode.PreserveIdentity);
        }

        entry.Solution = solution;

        HashSet<string> patchFiles = new(StringComparer.Ordinal);
        Dictionary<int, SortedSet<string>> filesByOwner = [];
        foreach (string file in files)
        {
            if (!entry.Owners.TryGetValue(file, out int owner))
                continue;

            patchFiles.Add(file);
            if (!filesByOwner.TryGetValue(owner, out SortedSet<string>? set))
                filesByOwner[owner] = set = new SortedSet<string>(StringComparer.Ordinal);

            set.Add(file);
        }

        HashSet<int> checkedProjects = [.. changed.Where(entry.Owners.ContainsKey).Select(file => entry.Owners[file])];
        List<ProjectUnit> units = [];
        foreach (UnitInfo info in entry.Units.Where(info => filesByOwner.ContainsKey(info.Index)))
        {
            Project project = solution.GetProject(info.ProjectId) ?? throw new InvalidOperationException("The kept workspace lost the project " + info.Path + ".");
            List<OwnedFile> owned = [.. filesByOwner[info.Index].Select(file => new OwnedFile(solution.GetDocument(info.Owned[file]) ?? throw new InvalidOperationException("The kept workspace lost the file " + file + "."), file))];
            units.Add(new ProjectUnit(info.Index, info.Path, project, owned));
        }

        ProjectResult[] results = new ProjectResult[units.Count];
        await Parallel.ForEachAsync(
            Enumerable.Range(0, units.Count),
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, options.MaxDegreeOfParallelism), CancellationToken = cancellationToken },
            async (index, token) =>
            {
                ProjectAnalyzer analyzer = new(units[index], entry.Catalog, entry.Owners, options.MaxErrorsPerProject, patchFiles);
                results[index] = await analyzer.AnalyzeAsync(checkedProjects.Contains(units[index].Index), token).ConfigureAwait(false);
            }).ConfigureAwait(false);

        AssembledGraph assembled = new GraphAssembler(patchFiles).Assemble(units, results);

        List<GraphProject> projects = [.. entry.Projects];
        List<GraphDiagnostic> diagnostics = [];
        foreach (int index in checkedProjects.Order())
        {
            diagnostics.AddRange(entry.Diagnostics.Where(diagnostic => diagnostic.Project == index));
            int position = units.FindIndex(unit => unit.Index == index);
            projects[index] = projects[index] with { ErrorCount = results[position].ErrorCount };
            diagnostics.AddRange(results[position].Diagnostics);
        }

        entry.Projects = projects;
        CodeGraph graph = new(
            entry.SdkVersion,
            projects,
            [],
            [],
            assembled.Symbols,
            assembled.Declarations,
            assembled.References,
            assembled.TypeEdges,
            assembled.ExternalEdges,
            assembled.Links,
            diagnostics);
        return new CodeGraphPatch(files, graph, assembled.Stubs, checkedProjects);
    }

    private static async Task<SourceText?> ReadAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            byte[] bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            using MemoryStream stream = new(bytes, writable: false);
            return SourceText.From(stream, encoding: null, SourceHashAlgorithm.Sha256);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
