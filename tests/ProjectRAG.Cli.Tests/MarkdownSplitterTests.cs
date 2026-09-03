using System.Collections.Immutable;
using Microsoft.Extensions.Options;
using ProjectRAG.Cli;
using Tokenizers.DotNet;

namespace ProjectRAG.Cli.Tests;

/// <summary>Loads the tokenizer once for every test of the class. Loading it costs about a second.</summary>
public sealed class TokenizerFixture : IDisposable
{
    public Tokenizer Tokenizer { get; } = Qwen3EmbeddingProvider.CreateTokenizer(new EmbeddingOptions());

    public MarkdownSplitter CreateSplitter(int maxTokens = 512) => new(Tokenizer, Options.Create(new ChunkOptions { MaxTokens = maxTokens }));

    public void Dispose() => Tokenizer.Dispose();
}

/// <summary>The classes that share a heavy resource. The native tokenizer crashes when two threads initialize it at once,
/// and two migration runs against one database race each other, so these classes run one after the other.</summary>
[CollectionDefinition(IntegrationCollection.Name)]
public sealed class IntegrationCollection : ICollectionFixture<TokenizerFixture>, ICollectionFixture<EmbeddingFixture>, ICollectionFixture<DatabaseFixture>
{
    public const string Name = "integration";
}

[Collection(IntegrationCollection.Name)]
public sealed class MarkdownSplitterTests(TokenizerFixture fixture)
{
    private const string Document = """
        # Graphics

        The engine draws through one interface.

        ## Vulkan

        Vulkan leaks into the engine.

        Command buffers belong to the backend.

        ## Cooking

        Borscht needs beetroot.
        """;

    [Theory]
    [InlineData("notes.md", true)]
    [InlineData("NOTES.MD", true)]
    [InlineData("notes.markdown", true)]
    [InlineData("notes.txt", false)]
    [InlineData("notes", false)]
    public void CanSplitAcceptsMarkdownOnly(string path, bool expected)
    {
        IDocumentSplitter splitter = fixture.CreateSplitter();
        Assert.Equal(expected, splitter.CanSplit(path));
    }

    [Fact]
    public void SplitKeepsHeadingPathAndSource()
    {
        ImmutableArray<Chunk> chunks = fixture.CreateSplitter().Split("kb/graphics.md", Document);

        Assert.Equal(3, chunks.Length);
        Assert.All(chunks, chunk => Assert.Equal("kb/graphics.md", chunk.Source));
        Assert.Equal(["Graphics"], chunks[0].HeadingPath.ToArray());
        Assert.Equal(["Graphics", "Vulkan"], chunks[1].HeadingPath.ToArray());
        Assert.Equal(["Graphics", "Cooking"], chunks[2].HeadingPath.ToArray());
    }

    [Fact]
    public void SplitJoinsNeighbourLeavesOfOneHeading()
    {
        ImmutableArray<Chunk> chunks = fixture.CreateSplitter().Split("kb/graphics.md", Document);

        Assert.Contains("Vulkan leaks into the engine.", chunks[1].Text);
        Assert.Contains("Command buffers belong to the backend.", chunks[1].Text);
    }

    [Fact]
    public void SplitReportsTheFirstLineOfEveryChunk()
    {
        ImmutableArray<Chunk> chunks = fixture.CreateSplitter().Split("kb/graphics.md", Document);

        Assert.Equal(3, chunks[0].Line);
        Assert.Equal(7, chunks[1].Line);
        Assert.Equal(13, chunks[2].Line);
    }

    [Fact]
    public void SplitReportsTheLastLineOfEveryChunk()
    {
        ImmutableArray<Chunk> chunks = fixture.CreateSplitter().Split("kb/graphics.md", Document);

        Assert.Equal(3, chunks[0].EndLine);
        Assert.Equal(9, chunks[1].EndLine);
        Assert.Equal(13, chunks[2].EndLine);
    }

    [Fact]
    public void SplitCountsTokensOfTheRenderedText()
    {
        ImmutableArray<Chunk> chunks = fixture.CreateSplitter().Split("kb/graphics.md", Document);

        Assert.All(chunks, chunk => Assert.Equal(fixture.Tokenizer.Encode(chunk.Render()).Length, chunk.TokenCount));
    }

    [Fact]
    public void RenderPrependsTheHeadingPath()
    {
        ImmutableArray<Chunk> chunks = fixture.CreateSplitter().Split("kb/graphics.md", Document);

        Assert.StartsWith("# Graphics\n## Vulkan\n", chunks[1].Render(), StringComparison.Ordinal);
    }

    [Fact]
    public void SplitDropsHeadingsWithoutContent()
    {
        ImmutableArray<Chunk> chunks = fixture.CreateSplitter().Split("kb/empty.md", "# Empty\n\n## Also empty\n");

        Assert.Empty(chunks);
    }

    [Fact]
    public void SplitCutsOneHeadingWhenItsLeavesDoNotFit()
    {
        ImmutableArray<Chunk> chunks = fixture.CreateSplitter(maxTokens: 12).Split("kb/graphics.md", Document);

        Assert.Equal(4, chunks.Length);
        Assert.Equal(["Graphics", "Vulkan"], chunks[1].HeadingPath.ToArray());
        Assert.Equal(["Graphics", "Vulkan"], chunks[2].HeadingPath.ToArray());
    }

    [Fact]
    public void SplitCutsALeafThatDoesNotFitOnItsOwn()
    {
        const int maxTokens = 16;
        string block = string.Join('\n', Enumerable.Range(0, 40).Select(index => $"line number {index} of one long block"));

        ImmutableArray<Chunk> chunks = fixture.CreateSplitter(maxTokens).Split("kb/long.md", $"# Long\n\n{block}\n");

        Assert.True(chunks.Length > 1);
        Assert.All(chunks, chunk => Assert.True(fixture.Tokenizer.Encode(chunk.Text).Length <= maxTokens, $"a chunk holds {fixture.Tokenizer.Encode(chunk.Text).Length} tokens"));
        Assert.Equal(3, chunks[0].Line);
        Assert.Equal(42, chunks[^1].EndLine);
    }

    [Fact]
    public void SplitCutsALineThatDoesNotFitOnItsOwn()
    {
        const int maxTokens = 16;
        string line = string.Join(' ', Enumerable.Range(0, 200).Select(index => $"word{index}"));

        ImmutableArray<Chunk> chunks = fixture.CreateSplitter(maxTokens).Split("kb/long.md", $"# Long\n\n{line}\n");

        Assert.True(chunks.Length > 1);
        Assert.All(chunks, chunk => Assert.True(fixture.Tokenizer.Encode(chunk.Text).Length <= maxTokens, $"a chunk holds {fixture.Tokenizer.Encode(chunk.Text).Length} tokens"));
        Assert.All(chunks, chunk => Assert.Equal(3, chunk.Line));
    }

    [Fact]
    public async Task SplitAsyncReadsTheFile()
    {
        string path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.md");
        await File.WriteAllTextAsync(path, Document);
        try
        {
            IDocumentSplitter splitter = fixture.CreateSplitter();
            ImmutableArray<Chunk> chunks = await splitter.SplitAsync(path);

            Assert.Equal(3, chunks.Length);
            Assert.All(chunks, chunk => Assert.Equal(path, chunk.Source));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
