using Microsoft.Extensions.Options;

using ProjectRag.Core.Code.Graph;

namespace ProjectRag.Code.Tests;

public sealed class IncrementalExtractionTests(EditableSampleFixture fixture) : IClassFixture<EditableSampleFixture>
{
    private const string Circle = "src/Lib/Circle.cs";
    private const string Scene = "src/Lib/Scene.cs";
    private const string SceneExtra = "src/Lib/Scene.Extra.cs";
    private const string Shared = "src/Lib/Shared.cs";
    private const string Consumer = "src/App/Consumer.cs";

    [Fact]
    public async Task MethodBody_WithAnotherCall()
    {
        using UpdateScenario scenario = await UpdateScenario.StartAsync(fixture);

        await scenario.ApplyAsync(Edit(Scene, "        _counter.Increment();\n        return circle;", "        _counter.Increment();\n        _counter.Increment();\n        Total();\n        return circle;"));
    }

    [Fact]
    public async Task RenamedMethod_UsedFromAnotherFileAndProject()
    {
        using UpdateScenario scenario = await UpdateScenario.StartAsync(fixture);

        await scenario.ApplyAsync(Edit("src/Lib/Extensions.cs", "Doubled", "Twice").Concat(Edit(Consumer, "Doubled", "Twice")).ToDictionary());
    }

    [Fact]
    public async Task AddedMethod()
    {
        using UpdateScenario scenario = await UpdateScenario.StartAsync(fixture);

        await scenario.ApplyAsync(Edit(Circle, "    public override double Area() => Math.PI * Radius * Radius;", "    public override double Area() => Math.PI * Radius * Radius;\n\n    public double Perimeter() => 2 * Math.PI * Radius;"));
    }

    [Fact]
    public async Task RemovedMethod()
    {
        using UpdateScenario scenario = await UpdateScenario.StartAsync(fixture);

        CodeGraphPatch patch = await scenario.ApplyAsync(Edit("src/Lib/Registry.cs", "\n\n    public void Add(T item) => _items.Add(item);", string.Empty));

        Assert.DoesNotContain(scenario.Graph.Symbols, symbol => symbol.DocId.StartsWith("M:Lib.Registry`1.Add", StringComparison.Ordinal));
        Assert.DoesNotContain(patch.Graph.Symbols, symbol => symbol.DocId.StartsWith("M:Lib.Registry`1.Add", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ChangedBaseClass()
    {
        using UpdateScenario scenario = await UpdateScenario.StartAsync(fixture);

        CodeGraphPatch patch = await scenario.ApplyAsync(Edit("src/Lib/ShapeBase.cs", "ShapeBase : IShape", "ShapeBase : Counter, IShape"));

        GraphSymbol counter = Assert.Single(patch.Graph.Symbols, symbol => symbol.DocId == "T:Lib.Counter");
        Assert.Contains(patch.Graph.Symbols.ToList().IndexOf(counter), patch.Stubs);
    }

    [Fact]
    public async Task AddedInterfaceImplementation()
    {
        using UpdateScenario scenario = await UpdateScenario.StartAsync(fixture);

        CodeGraphPatch patch = await scenario.ApplyAsync(Edit("src/Lib/Kinds.cs", "public struct Pair\n{\n    public int A;\n\n    public int B;", "public struct Pair : IShape\n{\n    public int A;\n\n    public int B;\n\n    public double Area() => A * B;"));

        Assert.Contains(patch.Graph.MemberLinks, link => link.Kind == "implement" && patch.Graph.Symbols[link.Member].DocId == "M:Lib.Pair.Area" && patch.Graph.Symbols[link.Target].DocId == "M:Lib.IShape.Area");
    }

    [Fact]
    public async Task LinkedFile_CompiledIntoTwoProjects()
    {
        using UpdateScenario scenario = await UpdateScenario.StartAsync(fixture);

        await scenario.ApplyAsync(Edit(Shared, "    public void Increment() => Value++;", "    public void Increment() => Value += 1;\n\n    public void Reset() => Value = 0;"));
    }

    [Fact]
    public async Task CompilerError_ThenFixed()
    {
        using UpdateScenario scenario = await UpdateScenario.StartAsync(fixture);

        CodeGraphPatch broken = await scenario.ApplyAsync(Edit(Circle, "    public override double Area() => Math.PI * Radius * Radius;", "    public override double Area() => Math.PI * Radius * Radius;\n\n    public int Broken() => \"text\";"));

        Assert.Equal(1, scenario.Graph.Projects[1].ErrorCount);
        Assert.Contains(scenario.Graph.Diagnostics, diagnostic => diagnostic.Project == 1 && diagnostic.Severity == "error" && diagnostic.Path == Circle);
        Assert.Equal([1], broken.CheckedProjects);

        await scenario.ApplyAsync(new Dictionary<string, string> { [Circle] = UpdateScenario.Original(Circle) });

        Assert.Equal(0, scenario.Graph.Projects[1].ErrorCount);
        Assert.DoesNotContain(scenario.Graph.Diagnostics, diagnostic => diagnostic.Severity == "error");
    }

    [Fact]
    public async Task PartialType_EditOfOnePart()
    {
        Dictionary<string, string> baseline = Edit(Scene, "public partial class Scene", "public sealed partial class Scene");
        using UpdateScenario scenario = await UpdateScenario.StartAsync(fixture, baseline);

        CodeGraphPatch patch = await scenario.ApplyAsync(Edit(SceneExtra, "    public int Count => _items.Count;", "    public int Count => _items.Count;\n\n    public int Twice => Count * 2;", scenario));

        Assert.DoesNotContain(Scene, patch.Files);
        GraphSymbol scene = Assert.Single(patch.Graph.Symbols, symbol => symbol.DocId == "T:Lib.Scene");
        Assert.Equal(["sealed", "partial"], scene.Modifiers);

        await scenario.ApplyAsync(Edit(Scene, "    private readonly Counter _counter = new Counter();", "    private readonly Counter _counter = new Counter();\n\n    private int _version;", scenario));
    }

    [Fact]
    public async Task SeveralStepsInARow()
    {
        using UpdateScenario scenario = await UpdateScenario.StartAsync(fixture);

        await scenario.ApplyAsync(Edit(Scene, "        _counter.Increment();\n        return circle;", "        return circle;"));
        await scenario.ApplyAsync(Edit(Circle, "    public override double Area() => Math.PI * Radius * Radius;", "    public override double Area() => Math.PI * Radius;", scenario));
        await scenario.ApplyAsync(new Dictionary<string, string> { [Scene] = UpdateScenario.Original(Scene), [Circle] = UpdateScenario.Original(Circle) });
    }

    [Fact]
    public async Task UpdateWithoutChangesGivesAnEmptyPatch()
    {
        fixture.RequireRestore();
        await fixture.ResetAsync();
        using RoslynCodeGraphExtractor extractor = Extractor();
        CodeGraphInput input = fixture.Input();
        await extractor.ExtractAsync(input, TestContext.Current.CancellationToken);

        CodeGraphPatch? patch = await extractor.UpdateAsync(new CodeGraphUpdate(input, [], []), TestContext.Current.CancellationToken);

        Assert.NotNull(patch);
        Assert.Empty(patch.Graph.Symbols);
        Assert.Empty(patch.CheckedProjects);
        Assert.Equal(2, patch.Graph.Projects.Count);
    }

    [Fact]
    public async Task Update_ReturnsNullWithoutAKeptWorkspace()
    {
        fixture.RequireRestore();
        await fixture.ResetAsync();
        using RoslynCodeGraphExtractor extractor = Extractor();

        Assert.Null(await extractor.UpdateAsync(Update(fixture.Input()), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Update_ReturnsNullAfterRelease()
    {
        fixture.RequireRestore();
        await fixture.ResetAsync();
        using RoslynCodeGraphExtractor extractor = Extractor();
        CodeGraphInput input = fixture.Input();
        await extractor.ExtractAsync(input, TestContext.Current.CancellationToken);
        Assert.NotNull(await extractor.UpdateAsync(Update(input), TestContext.Current.CancellationToken));

        extractor.Release(input.RootPath);

        Assert.Null(await extractor.UpdateAsync(Update(input), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Update_ReturnsNullForOtherPropertiesOrProjects()
    {
        fixture.RequireRestore();
        await fixture.ResetAsync();
        using RoslynCodeGraphExtractor extractor = Extractor();
        await extractor.ExtractAsync(fixture.Input(), TestContext.Current.CancellationToken);

        Assert.Null(await extractor.UpdateAsync(Update(fixture.Input(new Dictionary<string, string> { ["SampleFlag"] = "true" })), TestContext.Current.CancellationToken));
        Assert.Null(await extractor.UpdateAsync(Update(fixture.Input() with { ProjectPaths = [SampleCodeBase.LibProject] }), TestContext.Current.CancellationToken));
        Assert.NotNull(await extractor.UpdateAsync(Update(fixture.Input() with { ProjectPaths = [SampleCodeBase.AppProject, SampleCodeBase.LibProject] }), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Update_ReturnsNullWhenKeepingIsOff()
    {
        fixture.RequireRestore();
        await fixture.ResetAsync();
        using RoslynCodeGraphExtractor extractor = new(Options.Create(new CodeGraphOptions { KeepWorkspaceMinutes = 0 }));
        await extractor.ExtractAsync(fixture.Input(), TestContext.Current.CancellationToken);

        Assert.Null(await extractor.UpdateAsync(Update(fixture.Input()), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Update_ReturnsNullAfterTheExpiry()
    {
        fixture.RequireRestore();
        await fixture.ResetAsync();
        ManualTimeProvider time = new();
        using RoslynCodeGraphExtractor extractor = new(Options.Create(new CodeGraphOptions { KeepWorkspaceMinutes = 1 }), time);
        CodeGraphInput input = fixture.Input();
        await extractor.ExtractAsync(input, TestContext.Current.CancellationToken);

        time.Advance(TimeSpan.FromSeconds(40));
        Assert.NotNull(await extractor.UpdateAsync(Update(input), TestContext.Current.CancellationToken));
        time.Advance(TimeSpan.FromSeconds(40));
        Assert.NotNull(await extractor.UpdateAsync(Update(input), TestContext.Current.CancellationToken));

        time.Advance(TimeSpan.FromSeconds(61));

        Assert.Null(await extractor.UpdateAsync(Update(input), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FullBuild_OfAnotherRootRetiresTheLeastRecentlyUsedWorkspace()
    {
        fixture.RequireRestore();
        await fixture.ResetAsync();
        string second = Directory.CreateTempSubdirectory("project-rag-code-second-").FullName;
        try
        {
            await SampleFixture.WriteSampleAsync(second, null);
            string? failure = await SampleFixture.RestoreAsync(second);
            Assert.Null(failure);
            using RoslynCodeGraphExtractor extractor = new(Options.Create(new CodeGraphOptions { MaxDegreeOfParallelism = 2, MaxKeptWorkspaces = 1 }));
            CodeGraphInput first = fixture.Input();
            CodeGraphInput other = first with { RootPath = second };
            await extractor.ExtractAsync(first, TestContext.Current.CancellationToken);
            await extractor.ExtractAsync(other, TestContext.Current.CancellationToken);

            Assert.Null(await extractor.UpdateAsync(Update(first), TestContext.Current.CancellationToken));
            Assert.NotNull(await extractor.UpdateAsync(Update(other), TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(second, recursive: true);
        }
    }

    [Fact]
    public async Task Update_ReturnsNullWhenNoWorkspaceMayBeKept()
    {
        fixture.RequireRestore();
        await fixture.ResetAsync();
        using RoslynCodeGraphExtractor extractor = new(Options.Create(new CodeGraphOptions { MaxKeptWorkspaces = 0 }));
        await extractor.ExtractAsync(fixture.Input(), TestContext.Current.CancellationToken);

        Assert.Null(await extractor.UpdateAsync(Update(fixture.Input()), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void FailedAnalysis_CountsAsAnError()
    {
        ProjectResult result = GraphBuilder.FailedResult(3, new InvalidOperationException("boom"));

        Assert.True(result.ErrorCount >= 1);
        GraphDiagnostic diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(3, diagnostic.Project);
        Assert.Equal("error", diagnostic.Severity);
        Assert.Contains("boom", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FullBuild_ReplacesTheKeptWorkspace()
    {
        fixture.RequireRestore();
        await fixture.ResetAsync();
        using RoslynCodeGraphExtractor extractor = Extractor();
        CodeGraphInput input = fixture.Input();
        await extractor.ExtractAsync(input, TestContext.Current.CancellationToken);
        await fixture.WriteAsync(Circle, UpdateScenario.Original(Circle).Replace("Math.PI * Radius * Radius", "Math.PI * Radius", StringComparison.Ordinal));
        await extractor.ExtractAsync(input, TestContext.Current.CancellationToken);

        CodeGraphPatch? patch = await extractor.UpdateAsync(new CodeGraphUpdate(input, [Circle], [Circle]), TestContext.Current.CancellationToken);

        Assert.NotNull(patch);
        Assert.Contains(patch.Graph.Symbols, symbol => symbol.DocId == "M:Lib.Circle.Area");
    }

    private static RoslynCodeGraphExtractor Extractor() => new(Options.Create(new CodeGraphOptions { MaxDegreeOfParallelism = 2 }));

    private static CodeGraphUpdate Update(CodeGraphInput input) => new(input, [], []);

    private static Dictionary<string, string> Edit(string path, string from, string to, UpdateScenario? scenario = null)
    {
        string text = scenario?.Text(path) ?? UpdateScenario.Original(path);
        Assert.Contains(from, text, StringComparison.Ordinal);
        return new Dictionary<string, string>(StringComparer.Ordinal) { [path] = text.Replace(from, to, StringComparison.Ordinal) };
    }
}
