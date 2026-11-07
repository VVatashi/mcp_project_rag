using ProjectRag.Core.Code.Graph;

namespace ProjectRag.Code.Tests;

/// <summary>
/// Writes the sample code base into its own temporary folder and restores it once. A test edits the files and resets them with <see cref="ResetAsync"/>.
/// </summary>
public sealed class EditableSampleFixture : IAsyncLifetime
{
    private string? _skipReason;

    public string Root { get; } = Directory.CreateTempSubdirectory("project-rag-code-edit-").FullName;

    public IReadOnlySet<string> KnownFiles { get; } = SampleCodeBase.Files.Keys.ToHashSet(StringComparer.Ordinal);

    public async ValueTask InitializeAsync()
    {
        await SampleFixture.WriteSampleAsync(Root, null);
        MsBuildRegistration.Register(null);
        string? restoreFailure = await SampleFixture.RestoreAsync(Root);
        if (restoreFailure is not null)
            _skipReason = "The restore of the sample code base failed: " + restoreFailure;
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

    public Task ResetAsync(IReadOnlyDictionary<string, string>? baseline = null) => SampleFixture.WriteSampleAsync(Root, baseline);

    public Task WriteAsync(string path, string text) => File.WriteAllTextAsync(Path.Combine(Root, path), text.ReplaceLineEndings("\n") + "\n", TestContext.Current.CancellationToken);

    public CodeGraphInput Input(IReadOnlyDictionary<string, string>? properties = null) => new(
        Root,
        [SampleCodeBase.LibProject, SampleCodeBase.AppProject],
        [SampleCodeBase.Solution],
        KnownFiles,
        properties ?? new Dictionary<string, string>());
}
