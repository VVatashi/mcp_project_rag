using ProjectRag.Core.Documents;

namespace ProjectRag.Core.Tests.Documents;

public sealed class MarkdownChunkerTests
{
    [Fact]
    public void Split_KeepsThePathOfTheSection()
    {
        DocumentText document = new("Guide", "# Guide\n\nIntro.\n\n## Install\n\nRun the setup.\n\n### Linux\n\nUse apt.\n\n## Use\n\nCall the API.\n");

        IReadOnlyList<TextChunk> chunks = new MarkdownChunker(500).Split(document);

        Assert.Equal(["Guide", "Guide > Install", "Guide > Install > Linux", "Guide > Use"], chunks.Select(static chunk => chunk.Heading));
        Assert.Equal(["Intro.", "Run the setup.", "Use apt.", "Call the API."], chunks.Select(static chunk => chunk.Body));
        Assert.Equal([0, 1, 2, 3], chunks.Select(static chunk => chunk.Ordinal));
    }

    [Fact]
    public void Split_PointsIntoTheContent()
    {
        DocumentText document = new("Notes", "Line one.\n\n## Part\n\nLine two\nand three.\n");

        IReadOnlyList<TextChunk> chunks = new MarkdownChunker(500).Split(document);

        Assert.All(chunks, chunk => Assert.Equal(chunk.Body, document.Content[chunk.Start..chunk.End]));
    }

    [Fact]
    public void Split_IgnoresAHeadingInACodeBlock()
    {
        DocumentText document = new("Shell", "## Build\n\n```sh\n# not a heading\nmake\n```\n\nDone.\n");

        IReadOnlyList<TextChunk> chunks = new MarkdownChunker(500).Split(document);

        TextChunk chunk = Assert.Single(chunks);
        Assert.Equal("Shell > Build", chunk.Heading);
        Assert.Contains("# not a heading", chunk.Body, StringComparison.Ordinal);
        Assert.EndsWith("Done.", chunk.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void Split_ReadsSetextHeadings()
    {
        DocumentText document = new("Readme", "Readme\n======\n\nText.\n\nUsage\n-----\n\nMore text.\n");

        IReadOnlyList<TextChunk> chunks = new MarkdownChunker(500).Split(document);

        Assert.Equal(["Readme", "Readme > Usage"], chunks.Select(static chunk => chunk.Heading));
    }

    [Fact]
    public void Split_PacksBlocksUpToTheLimit()
    {
        string paragraph = new('a', 60);
        string content = string.Join("\n\n", Enumerable.Repeat(paragraph, 5));

        IReadOnlyList<TextChunk> chunks = new MarkdownChunker(130).Split(new DocumentText("Doc", content));

        // Two paragraphs and the blank line between them hold 122 characters. A third does not fit.
        Assert.Equal([122, 122, 60], chunks.Select(static chunk => chunk.Body.Length));
    }

    [Fact]
    public void Split_CutsALongLineAtASpace()
    {
        string content = string.Join(' ', Enumerable.Repeat("word", 100));

        IReadOnlyList<TextChunk> chunks = new MarkdownChunker(100).Split(new DocumentText("Doc", content));

        Assert.True(chunks.Count > 1);
        Assert.All(chunks, static chunk => Assert.InRange(chunk.Body.Length, 1, 100));
        Assert.All(chunks, static chunk => Assert.DoesNotContain(chunk.Body.Split(' '), static word => word is not "word"));
    }

    [Fact]
    public void Split_GivesNoChunksForAnEmptyDocument()
    {
        Assert.Empty(new MarkdownChunker(100).Split(new DocumentText("Empty", "\n\n## Heading only\n\n")));
    }

    [Fact]
    public void FindTitle_PrefersTheFrontMatter()
    {
        Assert.Equal("From YAML", MarkdownChunker.FindTitle("---\ntitle: \"From YAML\"\n---\n# From heading\n"));
        Assert.Equal("From heading", MarkdownChunker.FindTitle("Intro\n\n# From heading #\n"));
        Assert.Null(MarkdownChunker.FindTitle("```\n# in code\n```\n## Second level\n"));
    }

    [Fact]
    public void Split_SkipsTheFrontMatter()
    {
        DocumentText document = new("Doc", "---\ntitle: Doc\ntags: [a]\n---\nBody.\n");

        TextChunk chunk = Assert.Single(new MarkdownChunker(100).Split(document));

        Assert.Equal("Body.", chunk.Body);
    }
}
