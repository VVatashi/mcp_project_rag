using ProjectRag.Core.Code;
using ProjectRag.Core.Code.Graph;

namespace ProjectRag.Core.Tests.Code;

public sealed class CodeSearchFormatterTests
{
    private static readonly SymbolMatch Symbol = new(
        new SymbolSummary(1, 1, "demo", 1, "Demo.Core", "class", "SceneHost", "Demo.SceneHost", "T:Demo.SceneHost", "public sealed class SceneHost", "public", "Runs a scene. More text.", 5, "src/SceneHost.cs", 4),
        CodeMatchKinds.Exact);

    [Fact]
    public void Format_WritesTheThreeSectionsWithNumberedLines()
    {
        string body = string.Join('\n', Enumerable.Range(10, 14).Select(static i => $"    line {i}"));
        CodeChunkHit hit = new()
        {
            Source = "demo",
            Path = "src/SceneHost.cs",
            Heading = "src/SceneHost.cs > Demo.SceneHost > public void Update()",
            LineStart = 10,
            LineEnd = 23,
            Text = body,
            Score = 0.01234567,
        };
        CodeMatch match = new(5, 1, "demo", "src/Other.cs", "csharp", 7, 9, 9, "var h = new SceneHost();", 0);
        CodeSearchResult result = new("SceneHost", [Symbol], [match], [hit], "hybrid", false, ["The reranker did not work: down"], 3);

        string text = CodeSearchFormatter.Format(result, prefixSource: false);

        string[] lines = text.Split(Environment.NewLine);
        Assert.Equal("Symbols:", lines[0]);
        Assert.Equal("class Demo.SceneHost — public sealed class SceneHost — Demo.Core — src/SceneHost.cs:4", lines[1]);
        Assert.Equal("  Runs a scene.", lines[2]);
        Assert.Contains("Exact matches:\nsrc/Other.cs\n  7: var h = new SceneHost();\n".ReplaceLineEndings(), text, StringComparison.Ordinal);
        Assert.Contains("Similar code:\nsrc/SceneHost.cs:10-23  Demo.SceneHost > public void Update()  (0.0123)\n  10|     line 10\n".ReplaceLineEndings(), text, StringComparison.Ordinal);
        Assert.Contains("  21|     line 21\n  …\n".ReplaceLineEndings(), text, StringComparison.Ordinal);
        Assert.DoesNotContain("line 22", text, StringComparison.Ordinal);
        Assert.EndsWith("Note: The reranker did not work: down\n".ReplaceLineEndings(), text, StringComparison.Ordinal);
    }

    [Fact]
    public void Format_OmitsEmptySectionsAndPrefixesTheSource()
    {
        CodeChunkHit hit = new() { Source = "demo", Path = "a.glsl", Heading = "a.glsl", LineStart = 1, LineEnd = 1, Text = "void main() {}", Score = 1 };

        string text = CodeSearchFormatter.Format(new CodeSearchResult("main", [], [], [hit], "text", false, [], 1), prefixSource: true);

        Assert.DoesNotContain("Symbols:", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Exact matches:", text, StringComparison.Ordinal);
        Assert.Equal("Similar code:\ndemo:a.glsl:1-1  (1)\n  1| void main() {}\n".ReplaceLineEndings(), text);
        Assert.Equal("No results.\n".ReplaceLineEndings(), CodeSearchFormatter.Format(new CodeSearchResult("x", [], [], [], "text", false, [], 1), prefixSource: false));
    }
}
