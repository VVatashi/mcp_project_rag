using ProjectRag.Core.Code;
using ProjectRag.Core.Code.Chunks;
using ProjectRag.Core.Indexing;

namespace ProjectRag.Core.Tests.Code;

public sealed class CodeDataFilesTests
{
    private static readonly string Numbers = string.Join(", ", Enumerable.Range(1000, 400));

    [Theory]
    [InlineData("json")]
    [InlineData("xml")]
    [InlineData("yaml")]
    [InlineData("toml")]
    [InlineData("text")]
    public void IsData_IsTrueForALongLineOfADataLanguage(string language) =>
        Assert.True(CodeDataFiles.IsData(language, "first\n" + Numbers + "\nlast\n", new CodeOptions()));

    [Fact]
    public void IsData_IsFalseForTheSameContentInShortLines()
    {
        string content = string.Join('\n', Enumerable.Range(1000, 400).Select(static number => $"  {number},")) + "\n";

        Assert.False(CodeDataFiles.IsData("json", content, new CodeOptions()));
        Assert.False(CodeDataFiles.IsData("json", string.Empty, new CodeOptions()));
    }

    [Theory]
    [InlineData("csharp")]
    [InlineData("markdown")]
    [InlineData("glsl")]
    [InlineData("msbuild")]
    public void IsData_IsFalseForACodeLanguage(string language) =>
        Assert.False(CodeDataFiles.IsData(language, Numbers, new CodeOptions()));

    [Fact]
    public void IsData_ComparesTheLongestLineWithTheOption()
    {
        string line = new('a', 50);

        Assert.False(CodeDataFiles.IsData("json", line, new CodeOptions { DataLineChars = 50 }));
        Assert.True(CodeDataFiles.IsData("json", line, new CodeOptions { DataLineChars = 49 }));
        Assert.True(CodeDataFiles.IsData("json", "x\r\n" + line + "\r\ny", new CodeOptions { DataLineChars = 49 }));
        Assert.False(CodeDataFiles.IsData("json", "x\r\n" + line + "\r\ny", new CodeOptions { DataLineChars = 50 }));
    }

    [Fact]
    public void CodeFileVersion_ChangesWithTheLineLengthOfDataFiles()
    {
        CodeChunkingOptions chunking = new();
        LineCodeChunker chunker = new();

        Assert.Equal("code:2;2400;400;2;1000;LineCodeChunker:2", Indexer.CodeFileVersion(chunking, chunker, new CodeOptions().DataLineChars));
        Assert.NotEqual(Indexer.CodeFileVersion(chunking, chunker, 1000), Indexer.CodeFileVersion(chunking, chunker, 500));
    }
}
