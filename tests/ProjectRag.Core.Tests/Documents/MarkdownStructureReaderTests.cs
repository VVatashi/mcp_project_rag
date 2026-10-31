using ProjectRag.Core.Documents;

namespace ProjectRag.Core.Tests.Documents;

public sealed class MarkdownStructureReaderTests
{
    [Fact]
    public void Read_BuildsTheTreeOfSections()
    {
        DocumentStructure structure = Read("# Guide\n\nIntro.\n\n## 2 Install\n\nText.\n\n### 2.1 Linux\n\nApt.\n\n## 3 Use\n\nCall.\n");

        Assert.Equal(["Guide", "Guide", "Guide > 2 Install", "Guide > 2 Install > 2.1 Linux", "Guide > 3 Use"], structure.Sections.Select(static section => section.Path));
        DocumentSection linux = structure.Sections[3];
        Assert.Equal("2.1", linux.Number);
        Assert.Equal(2, linux.Parent);
        Assert.Equal([0, 1, 2, 3, 2], structure.Sections.Select(static section => section.Level));
        // A section ends where the next section of its level starts, and its subsections lie inside it.
        Assert.Equal(structure.Sections[4].Start, structure.Sections[2].End);
        Assert.True(structure.Sections[2].End >= linux.End);
    }

    [Fact]
    public void Read_ResolvesLinksAndMentions()
    {
        const string content = "# Doc\n\nSee [usage](../guide/b.md#usage) and [intro](#intro), and section 2.1 for details.\n\n## Intro\n\nText.\n\n## 2.1 Details\n\nMore. Table 4 shows it.\n\n<!-- rag:anchor table-4 -->\n| a | b |\n| --- | --- |\n| 1 | 2 |\n";
        DocumentStructure structure = MarkdownStructureReader.Read(new DocumentText("Doc", content), "docs/a.md");

        DocumentLink external = Assert.Single(structure.Links, static link => link.TargetPath is not null);
        Assert.Equal("guide/b.md", external.TargetPath);
        Assert.Equal("usage", external.TargetAnchor);
        Assert.Equal("usage", external.Label);
        Assert.Contains(structure.Links, static link => link.Kind == LinkKind.Link && link.TargetPath is null && link.TargetAnchor == "intro");
        Assert.Contains(structure.Links, static link => link.Kind == LinkKind.Mention && link.Label == "section 2.1" && link.TargetAnchor == "21-details");
        Assert.Contains(structure.Links, static link => link.Kind == LinkKind.Mention && link.Label == "Table 4" && link.TargetAnchor == "table-4");
        Assert.True(structure.Anchors["table-4"] > structure.Anchors["21-details"]);
    }

    [Fact]
    public void Read_HidesTheMarksAndReadsThePages()
    {
        const string content = "<!-- rag:page 7 -->\n\nFirst page text that goes on <!-- rag:page 8 --> on the next page.\n\n<!-- rag:page 9 -->\n\nLast.\n";
        DocumentStructure structure = MarkdownStructureReader.Read(new DocumentText("Doc", content), "a.pdf");

        Assert.Equal([7, 8, 9], structure.Pages.Select(static page => page.Page));
        Assert.Equal(3, structure.Hidden.Count);
        Assert.Equal(7, structure.PageAt(content.IndexOf("First", StringComparison.Ordinal)));
        Assert.Equal(8, structure.PageAt(content.IndexOf("next", StringComparison.Ordinal)));
        Assert.Equal(9, structure.PageAt(content.IndexOf("Last", StringComparison.Ordinal)));
    }

    [Fact]
    public void Read_KindsOfBlocks()
    {
        DocumentStructure structure = Read("Text.\n\n```cs\nint x;\n```\n\n$$\nx^2\n$$\n\n| a |\n| --- |\n| 1 |\n\n- one\n- two\n\n> quote\n\nNote[^1].\n\n[^1]: The footnote.\n");

        Assert.Equal(
            [BlockKind.Paragraph, BlockKind.Code, BlockKind.Math, BlockKind.Table, BlockKind.ListItem, BlockKind.ListItem, BlockKind.Quote, BlockKind.Paragraph, BlockKind.Footnote],
            structure.Blocks.Select(static block => block.Kind));
        Assert.Equal([.. structure.Blocks.Select(static block => block.Start).Order()], structure.Blocks.Select(static block => block.Start));
    }

    [Fact]
    public void Read_ResolvesALinkFromTheRootOfTheSource()
    {
        DocumentStructure structure = MarkdownStructureReader.Read(new DocumentText("Doc", "See [x](/docs/a.md#b).\n"), "dir/doc.md");

        DocumentLink link = Assert.Single(structure.Links);
        Assert.Equal("docs/a.md", link.TargetPath);
    }

    [Fact]
    public void Read_TakesALetterAsANumberOnlyBeforeADigit()
    {
        DocumentStructure structure = Read("# Guide\n\n## A Brief History\n\nText.\n\n## A.1 Extensions\n\nText.\n");

        Assert.Null(structure.Sections[2].Number);
        Assert.Equal("A.1", structure.Sections[3].Number);
    }

    [Fact]
    public void Read_SkipsTheMentionOfACaption()
    {
        DocumentStructure structure = Read("<!-- rag:anchor table-4 -->\n| a |\n| --- |\n| 1 |\n\nTable 4: The caption.\n");

        Assert.Empty(structure.Links);
    }

    [Fact]
    public void FindTitle_PrefersTheFrontMatter()
    {
        Assert.Equal("From YAML", MarkdownStructureReader.FindTitle("---\ntitle: \"From YAML\"\n---\n# From heading\n"));
        Assert.Equal("From heading", MarkdownStructureReader.FindTitle("Intro\n\n# From heading #\n"));
        Assert.Null(MarkdownStructureReader.FindTitle("```\n# in code\n```\n## Second level\n"));
    }

    private static DocumentStructure Read(string content) => MarkdownStructureReader.Read(new DocumentText("Guide", content), "guide.md");
}
