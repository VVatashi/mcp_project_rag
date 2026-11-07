using Microsoft.Extensions.Options;

using ProjectRag.Core.Code.Graph;

namespace ProjectRag.Code.Tests;

/// <summary>
/// A full build, then edits that go through <see cref="RoslynCodeGraphExtractor.UpdateAsync"/>. Each step compares the merged patch with a fresh full build.
/// </summary>
internal sealed class UpdateScenario : IDisposable
{
    private readonly EditableSampleFixture _fixture;
    private readonly RoslynCodeGraphExtractor _extractor = new(Options.Create(new CodeGraphOptions { MaxDegreeOfParallelism = 2 }));
    private readonly Dictionary<string, string> _texts = new(StringComparer.Ordinal);
    private readonly CodeGraphInput _input;

    private CodeGraph _graph = null!;

    private UpdateScenario(EditableSampleFixture fixture)
    {
        _fixture = fixture;
        _input = fixture.Input();
        foreach ((string path, string text) in SampleCodeBase.Files)
            _texts[path] = text.ReplaceLineEndings("\n");
    }

    public CodeGraph Graph => _graph;

    public static string Original(string path) => SampleCodeBase.Files[path].ReplaceLineEndings("\n");

    public static async Task<UpdateScenario> StartAsync(EditableSampleFixture fixture, IReadOnlyDictionary<string, string>? baseline = null)
    {
        fixture.RequireRestore();
        await fixture.ResetAsync(baseline);
        UpdateScenario scenario = new(fixture);
        if (baseline is not null)
        {
            foreach ((string path, string text) in baseline)
                scenario._texts[path] = text.ReplaceLineEndings("\n");
        }

        scenario._graph = await scenario._extractor.ExtractAsync(scenario._input, TestContext.Current.CancellationToken);
        return scenario;
    }

    public string Text(string path) => _texts[path];

    public async Task<CodeGraphPatch> ApplyAsync(IReadOnlyDictionary<string, string> edits)
    {
        foreach ((string path, string text) in edits)
        {
            _texts[path] = text;
            await _fixture.WriteAsync(path, text);
        }

        List<string> changed = [.. edits.Keys.Order(StringComparer.Ordinal)];
        List<string> files = [.. changed.Concat(FilesThatReference(changed)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

        CodeGraphPatch? patch = await _extractor.UpdateAsync(new CodeGraphUpdate(_input, changed, files), TestContext.Current.CancellationToken);
        Assert.NotNull(patch);
        AssertShape(patch, _graph);

        CodeGraph merged = PatchMerger.Apply(_graph, patch);
        using RoslynCodeGraphExtractor fresh = new(Options.Create(new CodeGraphOptions { MaxDegreeOfParallelism = 2, KeepWorkspaceMinutes = 0 }));
        CodeGraph expected = await fresh.ExtractAsync(_input, TestContext.Current.CancellationToken);
        NormalizedGraph.AssertEqual(NormalizedGraph.Of(expected), NormalizedGraph.Of(merged));

        _graph = merged;
        return patch;
    }

    public void Dispose() => _extractor.Dispose();

    private IEnumerable<string> FilesThatReference(IReadOnlyCollection<string> changed)
    {
        HashSet<int> symbols = [.. _graph.Declarations.Where(declaration => changed.Contains(declaration.Path)).Select(declaration => declaration.Symbol)];
        return _graph.References.Where(reference => symbols.Contains(reference.Symbol)).Select(reference => reference.Path);
    }

    private static void AssertShape(CodeGraphPatch patch, CodeGraph old)
    {
        CodeGraph graph = patch.Graph;
        HashSet<string> files = [.. patch.Files];

        Assert.Equal(old.Projects.Select(project => project.Path), graph.Projects.Select(project => project.Path));
        Assert.Empty(graph.FileProjects);
        Assert.Empty(graph.Solutions);
        Assert.NotEmpty(patch.CheckedProjects);
        Assert.All(graph.Declarations, declaration => Assert.Contains(declaration.Path, files));
        Assert.All(graph.References, reference => Assert.Contains(reference.Path, files));
        Assert.All(graph.TypeEdges, edge => Assert.Contains(edge.Path, files));
        Assert.All(graph.ExternalEdges, edge => Assert.Contains(edge.Path, files));
        Assert.All(graph.MemberLinks, link => Assert.Contains(link.Path, files));
        Assert.All(patch.Stubs, stub => Assert.DoesNotContain(graph.Declarations, declaration => declaration.Symbol == stub));
        Assert.All(graph.Declarations, declaration => Assert.DoesNotContain(declaration.Symbol, patch.Stubs));
        Assert.All(graph.Diagnostics, diagnostic => Assert.True(diagnostic.Project is { } project && patch.CheckedProjects.Contains(project)));
    }
}
