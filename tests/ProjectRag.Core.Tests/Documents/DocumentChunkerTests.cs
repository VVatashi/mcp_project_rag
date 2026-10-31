using System.Text.RegularExpressions;

using ProjectRag.Core.Documents;

namespace ProjectRag.Core.Tests.Documents;

public sealed partial class DocumentChunkerTests
{
    [Fact]
    public void Split_KeepsThePathOfTheSection()
    {
        IReadOnlyList<TextChunk> chunks = Split("# Guide\n\nIntro.\n\n## Install\n\nRun the setup.\n\n### Linux\n\nUse apt.\n\n## Use\n\nCall the API.\n");

        Assert.Equal(["Guide", "Guide > Install", "Guide > Install > Linux", "Guide > Use"], chunks.Select(static chunk => chunk.Heading));
        Assert.Equal(["Intro.", "Run the setup.", "Use apt.", "Call the API."], chunks.Select(static chunk => chunk.Body));
        Assert.Equal([0, 1, 2, 3], chunks.Select(static chunk => chunk.Ordinal));
        Assert.Equal([1, 2, 3, 4], chunks.Select(static chunk => chunk.Section));
    }

    [Fact]
    public void Split_KeepsCodeWholeUpToTheLargerLimit()
    {
        string code = "```\n" + string.Join('\n', Enumerable.Range(0, 30).Select(static i => $"line {i:D2} of the code")) + "\n```";
        IReadOnlyList<TextChunk> chunks = Split($"Intro text.\n\n{code}\n", maxChars: 300, atomicMaxChars: 1000);

        TextChunk chunk = Assert.Single(chunks, static chunk => chunk.Body.Contains("line 00", StringComparison.Ordinal));
        Assert.Contains("line 29", chunk.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void Split_SplitsLongCodeWithFencesAndOverlap()
    {
        string code = "```cs\n" + string.Join('\n', Enumerable.Range(0, 60).Select(static i => $"line {i:D2} of the code")) + "\n```";
        IReadOnlyList<TextChunk> chunks = Split(code, maxChars: 200, atomicMaxChars: 400, overlapChars: 60);

        Assert.True(chunks.Count > 2);
        Assert.All(chunks, static chunk => Assert.StartsWith("```cs", chunk.Body, StringComparison.Ordinal));
        Assert.All(chunks, static chunk => Assert.EndsWith("```", chunk.Body, StringComparison.Ordinal));
        Assert.All(chunks.Skip(1), static chunk => Assert.True(chunk.OverlapChars > 0));
        // Every line of the code lands in a chunk, and the next chunk repeats the last line of the previous one.
        for (int i = 0; i < 60; i++)
            Assert.Contains(chunks, chunk => chunk.Body.Contains($"line {i:D2} ", StringComparison.Ordinal));

        string lastLine = chunks[0].Body.Split('\n')[^2];
        Assert.Contains(lastLine, chunks[1].Body, StringComparison.Ordinal);
    }

    [Fact]
    public void Split_RepeatsTheHeaderOfALongTable()
    {
        string table = "| Name | Value |\n| --- | --- |\n" + string.Join('\n', Enumerable.Range(0, 40).Select(static i => $"| name{i:D2} | value {i:D2} |"));
        IReadOnlyList<TextChunk> chunks = Split(table, maxChars: 200, atomicMaxChars: 300, overlapChars: 0);

        Assert.True(chunks.Count > 2);
        Assert.All(chunks, static chunk => Assert.StartsWith("| Name | Value |\n| --- | --- |", chunk.Body, StringComparison.Ordinal));
    }

    [Fact]
    public void Split_CutsAParagraphBetweenSentences()
    {
        string paragraph = string.Join(' ', Enumerable.Range(0, 40).Select(static i => $"Sentence number {i} says something, e.g. a fact about `code_{i}` and $x_{i}$."));
        IReadOnlyList<TextChunk> chunks = Split(paragraph, maxChars: 300, overlapChars: 120);

        Assert.True(chunks.Count > 3);
        foreach (TextChunk chunk in chunks)
        {
            Assert.StartsWith("Sentence number", chunk.Body, StringComparison.Ordinal);
            Assert.EndsWith(".", chunk.Body, StringComparison.Ordinal);
            Assert.True(chunk.Body.Length <= 300);
            // No cut inside an abbreviation, a code span or a formula.
            Assert.DoesNotMatch(@"e\.g\.$", chunk.Body);
            Assert.Equal(0, chunk.Body.Count(static c => c == '`') % 2);
            Assert.Equal(0, chunk.Body.Count(static c => c == '$') % 2);
        }

        // The next chunk starts with the last sentence of the previous one.
        string lastSentence = SentenceRegex().Matches(chunks[0].Body)[^1].Value;
        Assert.StartsWith(lastSentence, chunks[1].Body, StringComparison.Ordinal);
        Assert.True(chunks[1].OverlapChars > 0);
    }

    [Fact]
    public void Split_KeepsACaptionAndALeadInWithTheirBlocks()
    {
        string filler = string.Join(' ', Enumerable.Repeat("Some text before the table.", 8));
        const string body = "Let the value be such that:\n\n$$\nx = \\frac{a}{b}\n$$\n\n| a | b |\n| --- | --- |\n| 1 | 2 |\n\nTable 8.20: The caption of the table.\n";
        IReadOnlyList<TextChunk> chunks = Split($"{filler}\n\n{body}", maxChars: 200, atomicMaxChars: 400);

        TextChunk formula = Assert.Single(chunks, static chunk => chunk.Body.Contains("\\frac", StringComparison.Ordinal));
        Assert.Contains("Let the value be such that:", formula.Body, StringComparison.Ordinal);
        TextChunk table = Assert.Single(chunks, static chunk => chunk.Body.Contains("| 1 | 2 |", StringComparison.Ordinal));
        Assert.Contains("Table 8.20:", table.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void Split_HidesThePageMarksAndKeepsThePages()
    {
        const string content = "<!-- rag:page 3 -->\n\n# Title\n\nText on three that goes on <!-- rag:page 4 --> on four.\n\n<!-- rag:anchor p4-1 -->\nMore on four.\n";
        IReadOnlyList<TextChunk> chunks = Split(content);

        TextChunk chunk = Assert.Single(chunks);
        Assert.DoesNotContain("rag:", chunk.Body, StringComparison.Ordinal);
        Assert.Contains("goes on on four", chunk.Body.Replace("  ", " ", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Equal(3, chunk.PageStart);
        Assert.Equal(4, chunk.PageEnd);
    }

    [Fact]
    public void Split_PutsAFootnoteIntoItsSection()
    {
        IReadOnlyList<TextChunk> chunks = Split("# A\n\nText[^1].\n\n[^1]: The note of A.\n\n# B\n\nOther text.\n");

        // The first heading of level 1 is the title, so the second one sits below it.
        Assert.Equal(["A", "A > B"], chunks.Select(static chunk => chunk.Heading));
        Assert.Contains("The note of A.", chunks[0].Body, StringComparison.Ordinal);
    }

    [Fact]
    public void Split_KeepsCodeInsideAListItemFenced()
    {
        string code = string.Join('\n', Enumerable.Range(0, 60).Select(static i => $"   echo step {i:D2}; run --flag value"));
        IReadOnlyList<TextChunk> chunks = Split($"1. Install the tools:\n\n   ```bash\n{code}\n   ```\n\n2. Run them.\n", maxChars: 300, atomicMaxChars: 600, overlapChars: 80);

        Assert.True(chunks.Count > 2);
        Assert.All(chunks, static chunk => Assert.Equal(0, CountOf(chunk.Body, "```") % 2));
        Assert.StartsWith("1. Install the tools:", chunks[0].Body, StringComparison.Ordinal);
        Assert.All(chunks, static chunk => Assert.True(chunk.Body.Length <= 600, $"{chunk.Body.Length} characters"));
    }

    [Fact]
    public void Split_CutsATextWithoutSpacesAtTheLimitAndKeepsSurrogatePairs()
    {
        string text = string.Concat(Enumerable.Repeat("知識庫😀", 400));
        IReadOnlyList<TextChunk> chunks = Split(text, maxChars: 301, overlapChars: 50);

        Assert.True(chunks.Count > 3);
        foreach (TextChunk chunk in chunks)
        {
            Assert.True(chunk.Body.Length <= 301);
            Assert.False(char.IsLowSurrogate(chunk.Body[0]));
            Assert.False(char.IsHighSurrogate(chunk.Body[^1]));
        }

        // Without spaces there is no place for an overlap, so the chunks follow each other.
        Assert.Equal(text.Length, chunks.Sum(static chunk => chunk.Body.Length));
    }

    [Fact]
    public void Split_GivesEveryPieceOfCodeNewLines()
    {
        string code = "```\nshort line\n" + new string('x', 280) + "\n" + string.Join('\n', Enumerable.Range(0, 20).Select(static i => $"line {i:D2}")) + "\n" + new string('y', 900) + "\n```";
        IReadOnlyList<TextChunk> chunks = Split(code, maxChars: 200, atomicMaxChars: 300, overlapChars: 60);

        Assert.All(chunks, static chunk => Assert.True(chunk.Body.Length <= 300 + 10, $"{chunk.Body.Length} characters"));
        Assert.All(chunks, static chunk => Assert.NotEqual("```\n```", chunk.Body));
        // No chunk only repeats the previous one.
        for (int i = 1; i < chunks.Count; i++)
            Assert.True(chunks[i].End > chunks[i - 1].End, $"chunk {i} adds nothing");
    }

    [Fact]
    public void Split_GivesNoChunksForAnEmptyDocument() => Assert.Empty(Split("\n\n## Heading only\n\n"));

    private static IReadOnlyList<TextChunk> Split(string content, int maxChars = 1800, int atomicMaxChars = 3000, int overlapChars = 300)
    {
        DocumentStructure structure = MarkdownStructureReader.Read(new DocumentText(MarkdownStructureReader.FindTitle(content) ?? "Doc", content), "doc.md");
        return new DocumentChunker(new ChunkingOptions { MaxChars = maxChars, AtomicMaxChars = atomicMaxChars, OverlapChars = overlapChars }).Split(structure);
    }

    private static int CountOf(string text, string value) => (text.Length - text.Replace(value, string.Empty, StringComparison.Ordinal).Length) / value.Length;

    [GeneratedRegex(@"Sentence number \d+[^.]*e\.g\.[^.]*\.")]
    private static partial Regex SentenceRegex();
}
