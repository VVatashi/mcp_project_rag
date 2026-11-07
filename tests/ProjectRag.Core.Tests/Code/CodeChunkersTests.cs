using ProjectRag.Core.Code;
using ProjectRag.Core.Code.Chunks;

namespace ProjectRag.Core.Tests.Code;

public sealed class CodeChunkersTests
{
    [Fact]
    public void For_PicksTheFirstChunkerThatSupportsTheLanguage()
    {
        FakeChunker csharpFirst = new(1, "csharp");
        FakeChunker csharpSecond = new(2, "csharp", "python");
        CodeChunkers chunkers = new([csharpFirst, csharpSecond]);

        Assert.Same(csharpFirst, chunkers.For("csharp"));
        Assert.Same(csharpSecond, chunkers.For("python"));
    }

    [Fact]
    public void For_FallsBackToTheChunkerOfLines()
    {
        CodeChunkers chunkers = new([new FakeChunker(1, "csharp")]);

        Assert.IsType<LineCodeChunker>(chunkers.For("glsl"));
        Assert.Same(chunkers.Fallback, chunkers.For("glsl"));
        Assert.IsType<LineCodeChunker>(new CodeChunkers([]).For("csharp"));
    }

    [Fact]
    public void Options_FingerprintTheLimits()
    {
        Assert.Equal("2400;400;2", new CodeChunkingOptions().Fingerprint());
        Assert.Equal("CodeChunking", CodeChunkingOptions.SectionName);
    }

    [Fact]
    public void ChunkKind_SeparatesDataFilesFromCode()
    {
        Assert.Equal("config", CodeLanguages.ChunkKind("json"));
        Assert.Equal("config", CodeLanguages.ChunkKind("yaml"));
        Assert.Equal("config", CodeLanguages.ChunkKind("msbuild"));
        Assert.Equal("config", CodeLanguages.ChunkKind("solution"));
        Assert.Equal("code", CodeLanguages.ChunkKind("csharp"));
        Assert.Equal("code", CodeLanguages.ChunkKind("glsl"));
        Assert.Equal("code", CodeLanguages.ChunkKind("text"));
    }

    private sealed class FakeChunker(int version, params string[] languages) : ICodeChunker
    {
        public int Version { get; } = version;

        public bool Supports(string language) => languages.Contains(language);

        public IReadOnlyList<CodeChunk> Split(string path, string content, CodeChunkingOptions options) => [];
    }
}
