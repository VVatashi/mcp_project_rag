using ProjectRag.Core.Sources;

namespace ProjectRag.Core.Tests.Sources;

public sealed class SourceTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("project-rag-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Scan_AppliesThePatterns()
    {
        Write("README.md");
        Write("docs/Guide.MD");
        Write("docs/image.png");
        Write("node_modules/lib/README.md");
        Source source = new() { Name = "test", RootPath = _root, IncludeGlobs = ["**/*.md"], ExcludeGlobs = SourceInput.DefaultExcludeGlobs };

        Assert.Equal(["README.md", "docs/Guide.MD"], SourceScanner.Scan(source));
    }

    [Fact]
    public void Scan_ThrowsForAMissingFolder()
    {
        Source source = new() { Name = "test", RootPath = Path.Combine(_root, "missing"), IncludeGlobs = ["**/*"] };

        Assert.Throws<DirectoryNotFoundException>(() => SourceScanner.Scan(source));
    }

    [Theory]
    [InlineData("axion", true)]
    [InlineData("my-project.v2", true)]
    [InlineData("Axion", false)]
    [InlineData("-axion", false)]
    [InlineData("", false)]
    public void Validate_ChecksTheName(string name, bool isValid)
    {
        SourceInput input = new() { Name = name, RootPath = _root };

        Assert.Equal(isValid, !input.Validate().ContainsKey(nameof(SourceInput.Name)));
    }

    [Fact]
    public void Validate_ChecksTheFolder()
    {
        Assert.Contains(nameof(SourceInput.RootPath), new SourceInput { Name = "a", RootPath = "relative/path" }.Validate().Keys);
        Assert.Contains(nameof(SourceInput.RootPath), new SourceInput { Name = "a", RootPath = Path.Combine(_root, "missing") }.Validate().Keys);
    }

    [Fact]
    public void Includes_FallsBackToTheDefaults()
    {
        SourceInput input = new() { IncludeGlobs = [] };

        Assert.Same(SourceInput.DefaultIncludeGlobs, input.Includes());
        Assert.Same(SourceInput.DefaultExcludeGlobs, input.Excludes());
    }

    private void Write(string path)
    {
        string full = Path.Combine(_root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, "text");
    }
}
