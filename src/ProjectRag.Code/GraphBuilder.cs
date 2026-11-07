using System.Diagnostics;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.VisualStudio.SolutionPersistence;
using Microsoft.VisualStudio.SolutionPersistence.Model;
using Microsoft.VisualStudio.SolutionPersistence.Serializer;

using ProjectRag.Core.Code.Graph;

namespace ProjectRag.Code;

/// <summary>
/// Loads the projects of a code base and builds its graph.
/// </summary>
internal sealed class GraphBuilder(CodeGraphInput input, CodeGraphOptions options, string sdkVersion)
{
    private static readonly TimeSpan SdkCheckTimeout = TimeSpan.FromSeconds(30);

    private static readonly string[] TestAssemblies = ["xunit.core", "xunit.v3.core", "nunit.framework", "Microsoft.VisualStudio.TestPlatform.TestFramework", "Microsoft.TestPlatform"];

    private readonly List<GraphDiagnostic> _diagnostics = [];
    private readonly StringComparison _pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>
    /// Loads the projects and builds the graph.
    /// </summary>
    /// <param name="keep">Tells to return the workspace with what an update needs. The caller then owns the workspace.</param>
    /// <param name="cancellationToken">The token that stops the work.</param>
    /// <returns>The graph, and the kept workspace when <paramref name="keep"/> is set.</returns>
    public async Task<(CodeGraph Graph, KeptWorkspace? Kept)> BuildAsync(bool keep, CancellationToken cancellationToken)
    {
        string root = Path.GetFullPath(input.RootPath);
        IReadOnlySet<string> known = keep ? new HashSet<string>(input.KnownFiles, StringComparer.Ordinal) : input.KnownFiles;
        FileCatalog catalog = new(root, known);
        await CheckSdkAsync(root, cancellationToken).ConfigureAwait(false);

        MSBuildWorkspace workspace = MSBuildWorkspace.Create(new Dictionary<string, string>(input.Properties));
        bool kept = false;
        try
        {
            List<string> inputProjects = [.. input.ProjectPaths.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
            await LoadProjectsAsync(workspace, root, inputProjects, cancellationToken).ConfigureAwait(false);

            List<ProjectUnit> units = SelectProjects(workspace.CurrentSolution, root, inputProjects, catalog, out Dictionary<string, int> owners, out List<GraphFileProject> fileProjects);
            if (units.Count == 0)
                throw new CodeGraphException("No project of the code base could load. " + string.Join(' ', _diagnostics.Where(diagnostic => diagnostic.Severity == "error").Take(3).Select(diagnostic => diagnostic.Message)));

            AddWorkspaceDiagnostics(workspace, units);
            ProjectResult[] results = new ProjectResult[units.Count];
            await Parallel.ForEachAsync(
                units,
                new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, options.MaxDegreeOfParallelism), CancellationToken = cancellationToken },
                async (unit, token) => results[unit.Index] = await AnalyzeAsync(unit, catalog, owners, token).ConfigureAwait(false)).ConfigureAwait(false);

            List<GraphProject> projects = [];
            Dictionary<string, int> projectIndexes = new(StringComparer.OrdinalIgnoreCase);
            foreach (ProjectUnit unit in units)
                projectIndexes[unit.Project.FilePath!] = unit.Index;

            foreach (ProjectUnit unit in units)
                projects.Add(DescribeProject(unit, results[unit.Index], projectIndexes, fileProjects));

            List<GraphSolution> solutions = await ReadSolutionsAsync(root, projectIndexes, cancellationToken).ConfigureAwait(false);
            CodeGraph graph = Merge(units, results, projects, solutions, fileProjects);
            if (!keep)
                return (graph, null);

            KeptWorkspace entry = new(
                KeptWorkspace.KeyOf(root),
                input,
                workspace,
                sdkVersion,
                catalog,
                owners,
                [.. units.Select(unit => new UnitInfo(unit.Index, unit.Path, unit.Project.Id, unit.Files.ToDictionary(file => file.Path, file => file.Document.Id, StringComparer.Ordinal)))],
                MapDocuments(workspace.CurrentSolution, catalog),
                projects,
                [.. _diagnostics]);
            kept = true;
            return (graph, entry);
        }
        finally
        {
            if (!kept)
                workspace.Dispose();
        }
    }

    private static Dictionary<string, List<DocumentId>> MapDocuments(Solution solution, FileCatalog catalog)
    {
        Dictionary<string, List<DocumentId>> map = new(StringComparer.Ordinal);
        foreach (Project project in solution.Projects)
        {
            foreach (Document document in project.Documents)
            {
                if (catalog.Resolve(document.FilePath) is not { } file)
                    continue;

                if (!map.TryGetValue(file, out List<DocumentId>? ids))
                    map[file] = ids = [];

                ids.Add(document.Id);
            }
        }

        return map;
    }

    private static string? FindTargetFramework(Project project)
    {
        int open = project.Name.LastIndexOf('(');
        if (open >= 0 && project.Name.EndsWith(')'))
            return project.Name[(open + 1)..^1];

        foreach (MetadataReference reference in project.MetadataReferences)
        {
            if (reference is not PortableExecutableReference { FilePath: { } file } || !string.Equals(Path.GetFileName(file), "System.Runtime.dll", StringComparison.OrdinalIgnoreCase))
                continue;

            string[] parts = file.Split('/', '\\');
            for (int i = parts.Length - 3; i >= 0; i--)
            {
                if (parts[i] == "ref")
                    return parts[i + 1];
            }
        }

        return null;
    }

    private async Task<ProjectResult> AnalyzeAsync(ProjectUnit unit, FileCatalog catalog, IReadOnlyDictionary<string, int> owners, CancellationToken cancellationToken)
    {
        ProjectAnalyzer analyzer = new(unit, catalog, owners, options.MaxErrorsPerProject);
        try
        {
            return await analyzer.AnalyzeAsync(readErrors: true, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return FailedResult(unit.Index, exception);
        }
    }

    internal static ProjectResult FailedResult(int projectIndex, Exception exception)
    {
        ProjectResult failed = new() { ErrorCount = 1 };
        failed.Diagnostics.Add(new GraphDiagnostic(projectIndex, "error", string.Empty, "The analysis failed: " + exception.Message, null, null));
        return failed;
    }

    private async Task CheckSdkAsync(string root, CancellationToken cancellationToken)
    {
        string? failure = null;
        try
        {
            ProcessStartInfo start = new("dotnet") { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("--version");
            using Process process = Process.Start(start) ?? throw new InvalidOperationException("The process did not start.");
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(SdkCheckTimeout);
            Task<string> output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            Task<string> error = process.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                await Task.WhenAll(output, error).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                process.Kill(entireProcessTree: true);
                failure = "dotnet --version did not finish in 30 seconds.";
            }

            if (failure is null && process.ExitCode != 0)
                failure = "dotnet --version failed: " + (await error.ConfigureAwait(false)).Trim();
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            failure = "dotnet --version could not run: " + exception.Message;
        }

        if (failure is not null)
            _diagnostics.Add(new GraphDiagnostic(null, "warning", string.Empty, failure + " The SDK that global.json asks for may be missing.", null, null));
    }

    private async Task LoadProjectsAsync(MSBuildWorkspace workspace, string root, List<string> inputProjects, CancellationToken cancellationToken)
    {
        foreach (string relative in inputProjects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string full = Path.GetFullPath(Path.Combine(root, relative));
            if (workspace.CurrentSolution.Projects.Any(project => string.Equals(project.FilePath, full, StringComparison.OrdinalIgnoreCase)))
                continue;

            try
            {
                await workspace.OpenProjectAsync(full, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _diagnostics.Add(new GraphDiagnostic(null, "error", string.Empty, "The project did not load: " + exception.Message, relative, null));
            }
        }
    }

    private List<ProjectUnit> SelectProjects(Solution solution, string root, List<string> inputProjects, FileCatalog catalog, out Dictionary<string, int> owners, out List<GraphFileProject> fileProjects)
    {
        List<(string Relative, Project Project)> selected = [];
        foreach (string relative in inputProjects)
        {
            string full = Path.GetFullPath(Path.Combine(root, relative));
            List<Project> candidates = [.. solution.Projects.Where(project => string.Equals(project.FilePath, full, StringComparison.OrdinalIgnoreCase))];
            if (candidates.Count == 0)
                continue;

            if (candidates.Count > 1)
            {
                _diagnostics.Add(new GraphDiagnostic(null, "warning", string.Empty, $"The project has several target frameworks. The graph keeps {candidates[0].Name} and skips {string.Join(", ", candidates.Skip(1).Select(project => project.Name))}.", relative, null));
            }

            selected.Add((relative, candidates[0]));
        }

        Dictionary<string, List<(int Project, string FullPath)>> byFile = new(StringComparer.Ordinal);
        List<int> documentCounts = [];
        for (int index = 0; index < selected.Count; index++)
        {
            HashSet<string> seen = new(StringComparer.Ordinal);
            foreach (Document document in selected[index].Project.Documents)
            {
                string? relativeFile = catalog.Resolve(document.FilePath);
                if (relativeFile is null || !seen.Add(relativeFile))
                    continue;

                if (!byFile.TryGetValue(relativeFile, out List<(int Project, string FullPath)>? list))
                    byFile[relativeFile] = list = [];

                list.Add((index, document.FilePath!));
            }
        }

        owners = new Dictionary<string, int>(StringComparer.Ordinal);
        fileProjects = [];
        List<List<OwnedFile>> owned = [.. selected.Select(_ => new List<OwnedFile>())];
        foreach ((string file, List<(int Project, string FullPath)> compiling) in byFile.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            int owner = compiling.Select(entry => entry.Project).Min();
            int longest = -1;
            foreach ((int project, string fullPath) in compiling)
            {
                string directory = Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(selected[project].Project.FilePath)!) + Path.DirectorySeparatorChar;
                if (fullPath.StartsWith(directory, _pathComparison) && directory.Length > longest)
                {
                    longest = directory.Length;
                    owner = project;
                }
            }

            owners[file] = owner;
            foreach ((int project, string fullPath) in compiling.OrderBy(entry => entry.Project))
            {
                fileProjects.Add(new GraphFileProject(file, project, project == owner));
                if (project == owner)
                {
                    Document document = selected[project].Project.Documents.First(candidate => string.Equals(candidate.FilePath, fullPath, StringComparison.Ordinal));
                    owned[project].Add(new OwnedFile(document, file));
                }
            }
        }

        List<ProjectUnit> units = [];
        for (int index = 0; index < selected.Count; index++)
            units.Add(new ProjectUnit(index, selected[index].Relative, selected[index].Project, owned[index]));

        return units;
    }

    private void AddWorkspaceDiagnostics(MSBuildWorkspace workspace, List<ProjectUnit> units)
    {
        foreach (WorkspaceDiagnostic diagnostic in workspace.Diagnostics)
        {
            ProjectUnit? match = units.FirstOrDefault(unit => diagnostic.Message.Contains(unit.Project.FilePath!, StringComparison.OrdinalIgnoreCase));
            string severity = diagnostic.Kind == WorkspaceDiagnosticKind.Failure ? "error" : "warning";
            _diagnostics.Add(new GraphDiagnostic(match?.Index, severity, string.Empty, diagnostic.Message, null, null));
        }
    }

    private static GraphProject DescribeProject(ProjectUnit unit, ProjectResult result, Dictionary<string, int> projectIndexes, List<GraphFileProject> fileProjects)
    {
        Project project = unit.Project;
        List<int> references = [];
        foreach (ProjectReference reference in project.ProjectReferences)
        {
            if (project.Solution.GetProject(reference.ProjectId)?.FilePath is { } path && projectIndexes.TryGetValue(path, out int target) && target != unit.Index)
                references.Add(target);
        }

        bool isTest = project.MetadataReferences.OfType<PortableExecutableReference>()
            .Any(reference => reference.FilePath is { } file && TestAssemblies.Contains(Path.GetFileNameWithoutExtension(file), StringComparer.OrdinalIgnoreCase));
        if (!isTest)
        {
            try
            {
                isTest = File.ReadAllText(project.FilePath!).Contains("<IsTestProject>true", StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                isTest = false;
            }
        }

        int documentCount = fileProjects.Count(row => row.Project == unit.Index);
        return new GraphProject(unit.Path, project.AssemblyName, FindTargetFramework(project), isTest, [.. references.Distinct().Order()], documentCount, result.ErrorCount);
    }

    private async Task<List<GraphSolution>> ReadSolutionsAsync(string root, Dictionary<string, int> projectIndexes, CancellationToken cancellationToken)
    {
        List<GraphSolution> solutions = [];
        foreach (string relative in input.SolutionPaths.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            string full = Path.GetFullPath(Path.Combine(root, relative));
            try
            {
                ISolutionSerializer serializer = SolutionSerializers.GetSerializerByMoniker(full) ?? throw new InvalidOperationException("The format of the solution is not known.");
                SolutionModel model = await serializer.OpenAsync(full, cancellationToken).ConfigureAwait(false);
                string directory = Path.GetDirectoryName(full)!;
                SortedSet<int> members = [];
                foreach (SolutionProjectModel project in model.SolutionProjects)
                {
                    string projectPath = Path.GetFullPath(Path.Combine(directory, project.FilePath.Replace('\\', Path.DirectorySeparatorChar)));
                    if (projectIndexes.TryGetValue(projectPath, out int index))
                        members.Add(index);
                }

                solutions.Add(new GraphSolution(relative, [.. members]));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _diagnostics.Add(new GraphDiagnostic(null, "warning", string.Empty, "The solution did not parse: " + exception.Message, relative, null));
                solutions.Add(new GraphSolution(relative, []));
            }
        }

        return solutions;
    }

    private CodeGraph Merge(List<ProjectUnit> units, ProjectResult[] results, List<GraphProject> projects, List<GraphSolution> solutions, List<GraphFileProject> fileProjects)
    {
        AssembledGraph assembled = new GraphAssembler(null).Assemble(units, results);
        List<GraphDiagnostic> diagnostics = [.. _diagnostics];
        foreach (ProjectUnit unit in units)
            diagnostics.AddRange(results[unit.Index].Diagnostics);

        return new CodeGraph(
            sdkVersion,
            projects,
            solutions,
            fileProjects,
            assembled.Symbols,
            assembled.Declarations,
            assembled.References,
            assembled.TypeEdges,
            assembled.ExternalEdges,
            assembled.Links,
            diagnostics);
    }
}
