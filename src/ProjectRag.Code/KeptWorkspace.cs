using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;

using ProjectRag.Core.Code.Graph;

namespace ProjectRag.Code;

/// <summary>
/// A project of the graph, found again in a newer solution by its identity.
/// </summary>
/// <param name="Index">The index of the project in the graph.</param>
/// <param name="Path">The project file.</param>
/// <param name="ProjectId">The identity of the project in the workspace.</param>
/// <param name="Owned">The document of each file that the project owns.</param>
internal sealed record UnitInfo(int Index, string Path, ProjectId ProjectId, IReadOnlyDictionary<string, DocumentId> Owned);

/// <summary>
/// The loaded workspace of a code base after a full build, with what an update needs.
/// </summary>
/// <remarks>
/// An update holds <see cref="Gate"/>. A release or an expiry marks the entry as retired, and the entry disposes its workspace when nobody holds the gate.
/// </remarks>
internal sealed class KeptWorkspace
{
    private readonly MSBuildWorkspace _workspace;
    private volatile bool _retired;
    private volatile bool _disposed;

    public KeptWorkspace(
        string key,
        CodeGraphInput input,
        MSBuildWorkspace workspace,
        string sdkVersion,
        FileCatalog catalog,
        IReadOnlyDictionary<string, int> owners,
        IReadOnlyList<UnitInfo> units,
        IReadOnlyDictionary<string, List<DocumentId>> fileDocuments,
        List<GraphProject> projects,
        IReadOnlyList<GraphDiagnostic> diagnostics)
    {
        Key = key;
        ProjectPaths = [.. input.ProjectPaths.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        Properties = new Dictionary<string, string>(input.Properties, StringComparer.Ordinal);
        _workspace = workspace;
        Solution = workspace.CurrentSolution;
        SdkVersion = sdkVersion;
        Catalog = catalog;
        Owners = owners;
        Units = units;
        FileDocuments = fileDocuments;
        Projects = projects;
        Diagnostics = diagnostics;
    }

    public string Key { get; }

    public IReadOnlyList<string> ProjectPaths { get; }

    public IReadOnlyDictionary<string, string> Properties { get; }

    public SemaphoreSlim Gate { get; } = new(1, 1);

    public ITimer? Timer { get; set; }

    public Solution Solution { get; set; }

    public string SdkVersion { get; }

    public FileCatalog Catalog { get; }

    public IReadOnlyDictionary<string, int> Owners { get; }

    public IReadOnlyList<UnitInfo> Units { get; }

    public IReadOnlyDictionary<string, List<DocumentId>> FileDocuments { get; }

    public List<GraphProject> Projects { get; set; }

    public IReadOnlyList<GraphDiagnostic> Diagnostics { get; }

    public bool IsClosed => _retired || _disposed;

    public static string KeyOf(string rootPath) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));

    public static StringComparer KeyComparer { get; } = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public bool Matches(CodeGraphInput input)
    {
        if (!ProjectPaths.SequenceEqual(input.ProjectPaths.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal), StringComparer.Ordinal) || Properties.Count != input.Properties.Count)
            return false;

        foreach ((string name, string value) in input.Properties)
        {
            if (!Properties.TryGetValue(name, out string? kept) || !string.Equals(kept, value, StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    public void Retire()
    {
        _retired = true;
        DisposeIfRetired();
    }

    public void ReleaseGate()
    {
        Gate.Release();
        DisposeIfRetired();
    }

    private void DisposeIfRetired()
    {
        if (!_retired || !Gate.Wait(0))
            return;

        try
        {
            if (_disposed)
                return;

            _disposed = true;
            Timer?.Dispose();
            _workspace.Dispose();
        }
        finally
        {
            Gate.Release();
        }
    }
}
