using System.Text.RegularExpressions;

using ProjectRag.Core.Code.Graph;
using ProjectRag.Core.Sources;

namespace ProjectRag.Core.Tests.Code;

public sealed class CodeGraphUnitTests
{
    [Theory]
    [InlineData("SSync", new[] { "S", "Sync" })]
    [InlineData("SceneHost", new[] { "Scene", "Host" })]
    [InlineData("Vec3D", new[] { "Vec", "3", "D" })]
    [InlineData("Utf8Writer", new[] { "Utf", "8", "Writer" })]
    [InlineData("sSync", new[] { "s", "Sync" })]
    [InlineData("A12B", new[] { "A", "12", "B" })]
    [InlineData("Scene", new[] { "Scene" })]
    [InlineData("scene", new[] { "scene" })]
    [InlineData("Scene.Update", new string[0])]
    [InlineData("", new string[0])]
    public void Humps_SplitBeforeUppercaseLettersAndDigitRuns(string query, string[] parts) =>
        Assert.Equal(parts, CodeHumps.Split(query));

    [Fact]
    public void Humps_BuildACaseSensitivePatternThatStartsEachPartAWord()
    {
        Assert.Equal("^S[a-z0-9_]*Sync", CodeHumps.BuildPattern("SSync"));
        Assert.Equal("^Vec[a-z0-9_]*3[a-z0-9_]*D", CodeHumps.BuildPattern("Vec3D"));
        Assert.Null(CodeHumps.BuildPattern("Scene"));
        Assert.Null(CodeHumps.BuildPattern("Scene.Update"));

        Regex regex = new(CodeHumps.BuildPattern("SSync")!);
        Assert.Matches(regex, "SceneSynchronizer");
        Assert.Matches(regex, "SSynchronizer");
        Assert.DoesNotMatch(regex, "SceneSnapshot");
        Assert.DoesNotMatch(regex, "scenesynchronizer");
        Assert.DoesNotMatch(regex, "MySceneSynchronizer");
        Assert.DoesNotMatch(regex, "SceneResync");
    }

    [Fact]
    public void InputHash_DependsOnTheSourcesOfTheBuildOnly()
    {
        (string, string, string?)[] files = [("src/B.cs", "h2", "csharp"), ("src/A.cs", "h1", "csharp"), ("A.csproj", "h3", "msbuild"), ("README.md", "h4", "markdown"), ("data.json", "h5", "json")];
        Dictionary<string, string> properties = new() { ["Configuration"] = "Debug", ["A"] = "1" };
        string baseline = CodeGraphInputHash.Compute(1, properties, files);

        Assert.Equal(64, baseline.Length);
        Assert.Equal(baseline, CodeGraphInputHash.Compute(1, new Dictionary<string, string> { ["A"] = "1", ["Configuration"] = "Debug" }, files.Reverse()));
        Assert.Equal(baseline, CodeGraphInputHash.Compute(1, properties, [.. files.Select(static file => file.Item1 == "README.md" ? ("README.md", "changed", file.Item3) : file)]));
        Assert.NotEqual(baseline, CodeGraphInputHash.Compute(2, properties, files));
        Assert.NotEqual(baseline, CodeGraphInputHash.Compute(1, new Dictionary<string, string> { ["Configuration"] = "Release", ["A"] = "1" }, files));
        Assert.NotEqual(baseline, CodeGraphInputHash.Compute(1, properties, [.. files.Select(static file => file.Item1 == "src/A.cs" ? ("src/A.cs", "changed", file.Item3) : file)]));
        Assert.NotEqual(baseline, CodeGraphInputHash.Compute(1, properties, [.. files, ("src/C.cs", "h6", "csharp")]));
        Assert.NotEqual(baseline, CodeGraphInputHash.Compute(1, properties, [.. files, ("global.json", "h7", "json")]));
        Assert.NotEqual(baseline, CodeGraphInputHash.Compute(1, properties, [.. files, ("sub/nuget.config", "h8", "xml")]));
        Assert.NotEqual(baseline, CodeGraphInputHash.Compute(1, properties, [.. files, ("Directory.Packages.props", "h9", "msbuild")]));
    }

    [Theory]
    [InlineData("Updates the scene. It runs once.", "Updates the scene.")]
    [InlineData("Updates\n   the scene.\nMore.", "Updates the scene.")]
    [InlineData("Version 1.2 is used", "Version 1.2 is used")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void FirstSentence_CutsTheSummary(string? summary, string expected) =>
        Assert.Equal(expected, CodeGraphFormatter.FirstSentence(summary));

    [Fact]
    public void Symbols_WriteALineForEachMatchWithItsSummary()
    {
        SymbolSummary host = Summary(1, "class", "SceneHost", "Demo.SceneHost", "public sealed class SceneHost", "Runs a scene. More text.", "src/SceneHost.cs", 12);
        SymbolSummary update = Summary(2, "method", "Update", "Demo.SceneHost.Update", "public void Update(double time)", null, null, null);

        string text = CodeGraphFormatter.Symbols([new SymbolMatch(host, CodeMatchKinds.Exact), new SymbolMatch(update, CodeMatchKinds.Fuzzy)]).Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.Equal(
            "class Demo.SceneHost — public sealed class SceneHost — Demo.Core — src/SceneHost.cs:12\n  Runs a scene.\nmethod Demo.SceneHost.Update — public void Update(double time) — Demo.Core — -\n",
            text);
        Assert.Equal("No symbols match.", CodeGraphFormatter.Symbols([]));
    }

    [Fact]
    public void References_GroupTheLinesByFile()
    {
        SymbolSummary caller = Summary(3, "method", "Run", "Demo.SceneSynchronizer.Run", "public void Run()", null, "src/A.cs", 5);
        CodeReferencePage page = new(
            5,
            [
                new CodeReference(1, "src/A.cs", 7, 24, 9, "create", "        var copy = new SceneHost();", 0, caller),
                new CodeReference(1, "src/A.cs", 8, 14, 6, "call", "        host.Update(1.0);", 0, caller),
                new CodeReference(2, "src/B.cs", 3, 400, 9, "type", "  long text SceneHost", 300, null),
            ]);

        string text = CodeGraphFormatter.References(page, 3).Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.Equal(
            "src/A.cs\n  7:24 [create] var copy = new SceneHost();   (in Demo.SceneSynchronizer.Run)\n  8:14 [call] host.Update(1.0);   (in Demo.SceneSynchronizer.Run)\nsrc/B.cs\n  3:400 [type] …long text SceneHost\nShowing 3 of 5 references (limit 3). Narrow the request with kind or path.\n",
            text);
        Assert.Equal("No references.", CodeGraphFormatter.References(new CodeReferencePage(0, []), 10));
    }

    [Fact]
    public void Ambiguous_ListsAtMostTenCandidatesWithTheirIds()
    {
        CodeSymbolGroup[] groups = [.. Enumerable.Range(1, 12).Select(i => new CodeSymbolGroup([Summary(i, "method", "Update", $"Demo.T{i}.Update", "void Update()", null, "src/A.cs", i)]))];

        string text = CodeGraphFormatter.Ambiguous("Update", groups);

        Assert.Contains("12 symbols match", text, StringComparison.Ordinal);
        Assert.Contains("M:Demo.T1.Update", text, StringComparison.Ordinal);
        Assert.Contains("M:Demo.T10.Update", text, StringComparison.Ordinal);
        Assert.DoesNotContain("M:Demo.T11.Update", text, StringComparison.Ordinal);
        Assert.Contains("and 2 more.", text, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildProperties_AreForCodeSourcesWithValidNames()
    {
        string root = Directory.CreateTempSubdirectory("project-rag-").FullName;
        try
        {
            SourceInput valid = new() { Name = "demo", Kind = SourceKind.Code, RootPath = root, BuildProperties = new Dictionary<string, string> { ["Configuration"] = "Release", ["_X.y-z1"] = string.Empty } };
            Assert.Empty(valid.Validate());
            Assert.Empty(new SourceInput { Name = "demo", Kind = SourceKind.Code, RootPath = root }.Validate());

            SourceInput wrongName = new() { Name = "demo", Kind = SourceKind.Code, RootPath = root, BuildProperties = new Dictionary<string, string> { ["1Bad"] = "x" } };
            Assert.Contains(nameof(SourceInput.BuildProperties), wrongName.Validate().Keys);
            SourceInput space = new() { Name = "demo", Kind = SourceKind.Code, RootPath = root, BuildProperties = new Dictionary<string, string> { ["A B"] = "x" } };
            Assert.Contains(nameof(SourceInput.BuildProperties), space.Validate().Keys);

            SourceInput docs = new() { Name = "demo", RootPath = root, BuildProperties = new Dictionary<string, string> { ["Configuration"] = "Release" } };
            Assert.Contains(nameof(SourceInput.BuildProperties), docs.Validate().Keys);
            Assert.Empty(new SourceInput { Name = "demo", RootPath = root, BuildProperties = new Dictionary<string, string>() }.Validate());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static SymbolSummary Summary(int id, string kind, string name, string fullName, string signature, string? summary, string? path, int? line) =>
        new(id, 1, "demo", 1, "Demo.Core", kind, name, fullName, (CodeSymbolKinds.IsType(kind) ? "T:" : "M:") + fullName, signature, "public", summary, path is null ? null : 1, path, line);
}
