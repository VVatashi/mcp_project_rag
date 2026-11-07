using ProjectRag.Core.Code.Chunks;

namespace ProjectRag.Core.Tests.Code;

public sealed class LineCodeChunkerTests
{
    private static readonly LineCodeChunker Chunker = new();

    [Theory]
    [InlineData("")]
    [InlineData("\n")]
    [InlineData("  \n\t\n   ")]
    public void Split_GivesNoChunksForABlankText(string content) =>
        Assert.Empty(Chunker.Split("a.txt", content, new CodeChunkingOptions()));

    [Fact]
    public void Split_ReportsTheChunkerAndItsHeading()
    {
        Assert.True(Chunker.Supports("anything"));
        Assert.Equal(2, Chunker.Version);
        CodeChunk chunk = Assert.Single(Chunker.Split("src/a.glsl", "void main() {}\n", new CodeChunkingOptions()));
        Assert.Equal("src/a.glsl", chunk.Heading);
    }

    [Fact]
    public void Split_PacksBlocksAndCutsTheEdges()
    {
        const string content = "\n\nfirst\nsecond\n\n\nthird\n\n";

        CodeChunk chunk = Assert.Single(Chunker.Split("a.txt", content, new CodeChunkingOptions()));

        Assert.Equal("first\nsecond\n\n\nthird", content[chunk.Start..chunk.End]);
        Assert.Equal((3, 7, 0), (chunk.LineStart, chunk.LineEnd, chunk.OverlapChars));
    }

    [Fact]
    public void Split_StartsANewChunkWhenTheNextBlockDoesNotFit()
    {
        const string content = "ABCDEFGHIJ\n\nabcdefghij\n\nKLMNOPQRST\n";

        IReadOnlyList<CodeChunk> chunks = Chunker.Split("a.txt", content, new CodeChunkingOptions { MaxChars = 24 });

        Assert.Equal(2, chunks.Count);
        Assert.Equal("ABCDEFGHIJ\n\nabcdefghij", content[chunks[0].Start..chunks[0].End]);
        Assert.Equal((1, 3), (chunks[0].LineStart, chunks[0].LineEnd));
        Assert.Equal("KLMNOPQRST", content[chunks[1].Start..chunks[1].End]);
        Assert.Equal((5, 5), (chunks[1].LineStart, chunks[1].LineEnd));
        Assert.Equal(3, Chunker.Split("a.txt", content, new CodeChunkingOptions { MaxChars = 21 }).Count);
    }

    [Fact]
    public void Split_SplitsALongBlockByLinesWithOverlap()
    {
        string content = string.Join('\n', Enumerable.Range(1, 10).Select(static i => $"line{i:D2}")) + "\n";

        IReadOnlyList<CodeChunk> chunks = Chunker.Split("a.txt", content, new CodeChunkingOptions { MaxChars = 32, OverlapLines = 1 });

        Assert.Equal([(1, 3), (3, 5), (5, 7), (7, 9), (9, 10)], chunks.Select(static chunk => (chunk.LineStart, chunk.LineEnd)));
        Assert.Equal([0, 6, 6, 6, 6], chunks.Select(static chunk => chunk.OverlapChars));
        Assert.Equal("line03\nline04\nline05", content[chunks[1].Start..chunks[1].End]);
    }

    [Fact]
    public void Split_ShrinksTheOverlapWhenTheNextLineNeedsTheRoom()
    {
        string content = string.Join('\n', new string('a', 8), new string('b', 8), new string('c', 8), new string('d', 10), new string('e', 8), new string('f', 8)) + "\n";

        IReadOnlyList<CodeChunk> chunks = Chunker.Split("a.txt", content, new CodeChunkingOptions { MaxChars = 26, OverlapLines = 2 });

        AssertInvariants(content, chunks, 26);
        Assert.Equal([(1, 3), (3, 4), (4, 5), (5, 6)], chunks.Select(static chunk => (chunk.LineStart, chunk.LineEnd)));
        Assert.Equal([0, 8, 10, 8], chunks.Select(static chunk => chunk.OverlapChars));
    }

    [Fact]
    public void Split_SplitsAHugeLineByCharacters()
    {
        string content = "head\n\n" + new string('x', 100) + "\n\ntail\n";

        IReadOnlyList<CodeChunk> chunks = Chunker.Split("a.txt", content, new CodeChunkingOptions { MaxChars = 30 });

        AssertInvariants(content, chunks, 30);
        Assert.Equal(["head", new string('x', 30), new string('x', 30), new string('x', 30), new string('x', 10) + "\n\ntail"], chunks.Select(chunk => content[chunk.Start..chunk.End]));
        Assert.Equal([1, 3, 3, 3, 3], chunks.Select(static chunk => chunk.LineStart));
        Assert.Equal([1, 3, 3, 3, 5], chunks.Select(static chunk => chunk.LineEnd));
    }

    [Fact]
    public void Split_CountsADigitAsThreeCharacters()
    {
        string content = string.Concat(Enumerable.Repeat("1234567890", 300)) + "\n";

        IReadOnlyList<CodeChunk> chunks = Chunker.Split("a.json", content, new CodeChunkingOptions { MaxChars = 900 });

        AssertInvariants(content, chunks, 900);
        Assert.Equal(Enumerable.Repeat(300, 10), chunks.Select(static chunk => chunk.End - chunk.Start));
    }

    [Fact]
    public void Split_KeepsADigitBlockAndALongDigitLineWithinTheWeightedLimit()
    {
        string content = "1234567890\n1234567890\n1234567890\n\n" + new string('7', 100) + "\nabc 12 def\n";

        IReadOnlyList<CodeChunk> chunks = Chunker.Split("a.txt", content, new CodeChunkingOptions { MaxChars = 60, OverlapLines = 1 });

        AssertInvariants(content, chunks, 60);
        Assert.True(chunks.Count > 4);
    }

    [Fact]
    public void Split_KeepsASurrogatePairWhole()
    {
        string content = string.Concat(Enumerable.Repeat("😀", 20));

        IReadOnlyList<CodeChunk> chunks = Chunker.Split("a.txt", content, new CodeChunkingOptions { MaxChars = 5 });

        Assert.All(chunks, chunk =>
        {
            Assert.False(char.IsLowSurrogate(content[chunk.Start]));
            Assert.False(char.IsHighSurrogate(content[chunk.End - 1]));
        });
        Assert.Equal(content, string.Concat(chunks.Select(chunk => content[chunk.Start..chunk.End])));
    }

    [Theory]
    [InlineData(40, 2)]
    [InlineData(80, 0)]
    [InlineData(120, 3)]
    [InlineData(2400, 2)]
    public void Split_KeepsTheInvariantsOnMixedText(int maxChars, int overlapLines)
    {
        string content = string.Join('\n', Enumerable.Range(1, 120).Select(static i => (i % 11) switch
        {
            0 => string.Empty,
            3 => "    " + new string('y', 70),
            7 => "   ",
            _ => $"    var item{i} = Compute({i});",
        })) + "\n";

        IReadOnlyList<CodeChunk> chunks = Chunker.Split("src/a.cs", content, new CodeChunkingOptions { MaxChars = maxChars, OverlapLines = overlapLines });

        AssertInvariants(content, chunks, maxChars);
    }

    // The ranges follow the text in order, stay within the limit, and keep the line numbers of the file. Every non-blank line is in a chunk.
    private static void AssertInvariants(string content, IReadOnlyList<CodeChunk> chunks, int maxChars)
    {
        int covered = 0;
        for (int i = 0; i < chunks.Count; i++)
        {
            CodeChunk chunk = chunks[i];
            Assert.InRange(chunk.End - chunk.Start, 1, maxChars);
            Assert.InRange(content[chunk.Start..chunk.End].Sum(static c => char.IsAsciiDigit(c) ? 3 : 1), 1, maxChars);
            Assert.True(chunk.Start >= 0 && chunk.End <= content.Length);
            Assert.Equal(1 + content[..chunk.Start].Count(static c => c == '\n'), chunk.LineStart);
            Assert.Equal(1 + content[..(chunk.End - 1)].Count(static c => c == '\n'), chunk.LineEnd);
            Assert.NotEqual('\n', content[chunk.End - 1]);
            if (i > 0)
            {
                Assert.True(chunk.End > chunks[i - 1].End);
                Assert.True(chunk.Start > chunks[i - 1].Start);
                int overlap = Math.Max(0, chunks[i - 1].End - chunk.Start);
                Assert.True(chunk.OverlapChars <= overlap);
            }

            covered = Math.Max(covered, chunk.End);
        }

        string[] lines = content.Split('\n');
        int offset = 0;
        foreach (string line in lines)
        {
            if (line.Trim().Length > 0)
                Assert.Contains(chunks, chunk => chunk.Start < offset + line.Length && chunk.End > offset);

            offset += line.Length + 1;
        }
    }
}
