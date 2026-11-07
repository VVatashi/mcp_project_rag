using System.Diagnostics;

using Microsoft.Extensions.Options;

using ProjectRag.Core.Code.Graph;

namespace ProjectRag.Code.Tests;

/// <summary>
/// Writes the sample code base into a temporary folder, restores it once and extracts its graph.
/// </summary>
public sealed class SampleFixture : IAsyncLifetime
{
    private string? _skipReason;

    public string Root { get; } = Directory.CreateTempSubdirectory("project-rag-code-").FullName;

    public CodeGraph Graph { get; private set; } = null!;

    public CodeGraph FlaggedGraph { get; private set; } = null!;

    public IReadOnlySet<string> KnownFiles { get; private set; } = new HashSet<string>();

    public async ValueTask InitializeAsync()
    {
        await WriteSampleAsync(Root, null);

        KnownFiles = SampleCodeBase.Files.Keys.ToHashSet(StringComparer.Ordinal);
        MsBuildRegistration.Register(null);

        string? restoreFailure = await RestoreAsync(Root);
        if (restoreFailure is not null)
        {
            _skipReason = "The restore of the sample code base failed: " + restoreFailure;
            return;
        }

        Graph = await ExtractAsync(Input(new Dictionary<string, string>()), maxDegreeOfParallelism: 4);
        FlaggedGraph = await ExtractAsync(Input(new Dictionary<string, string> { ["SampleFlag"] = "true" }), maxDegreeOfParallelism: 2);
    }

    public ValueTask DisposeAsync()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }

        return ValueTask.CompletedTask;
    }

    public void RequireRestore()
    {
        if (_skipReason is not null)
            Assert.Skip(_skipReason);
    }

    public CodeGraphInput Input(IReadOnlyDictionary<string, string> properties, IReadOnlySet<string>? known = null, IReadOnlyList<string>? projects = null, IReadOnlyList<string>? solutions = null) => new(
        Root,
        projects ?? [SampleCodeBase.LibProject, SampleCodeBase.AppProject],
        solutions ?? [SampleCodeBase.Solution],
        known ?? KnownFiles,
        properties);

    public static Task<CodeGraph> ExtractAsync(CodeGraphInput input, int maxDegreeOfParallelism = 2)
    {
        RoslynCodeGraphExtractor extractor = new(Options.Create(new CodeGraphOptions { MaxDegreeOfParallelism = maxDegreeOfParallelism }));
        return extractor.ExtractAsync(input, TestContext.Current.CancellationToken);
    }

    public static (int Line, int Column) Find(string path, string needle, int occurrence = 1, int offset = 0)
    {
        string text = SampleCodeBase.Files[path].ReplaceLineEndings("\n");
        int index = -1;
        for (int i = 0; i < occurrence; i++)
        {
            index = text.IndexOf(needle, index + 1, StringComparison.Ordinal);
            Assert.True(index >= 0, $"'{needle}' occurrence {occurrence} is not in {path}.");
        }

        index += offset;
        int line = text.AsSpan(0, index).Count('\n') + 1;
        int column = index - (text.LastIndexOf('\n', index - 1 < 0 ? 0 : index - 1) + 1) + 1;
        return (line, column);
    }

    /// <summary>
    /// Writes the files of the sample code base into a folder, with the given texts in place of some of them.
    /// </summary>
    public static async Task WriteSampleAsync(string root, IReadOnlyDictionary<string, string>? replacements)
    {
        foreach ((string path, string original) in SampleCodeBase.Files)
        {
            string text = replacements is not null && replacements.TryGetValue(path, out string? replacement) ? replacement : original;
            string full = Path.Combine(root, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            await File.WriteAllTextAsync(full, text.ReplaceLineEndings("\n") + "\n", TestContext.Current.CancellationToken);
        }
    }

    public static async Task<string?> RestoreAsync(string root)
    {
        ProcessStartInfo start = new("dotnet") { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add("restore");
        start.ArgumentList.Add(SampleCodeBase.Solution);
        start.ArgumentList.Add("--nologo");
        using Process process = Process.Start(start)!;
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        return process.ExitCode == 0 ? null : (await output + await error).Trim();
    }
}
