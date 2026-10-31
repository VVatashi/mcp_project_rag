using ProjectRag.Core.Documents;
using ProjectRag.Core.Documents.Pdf;

namespace ProjectRag.Core.Tests.Documents.Pdf;

public sealed class PdfMarkdownImporterTests
{
    private const GlyphStyle MathItalic = GlyphStyle.Math | GlyphStyle.Italic;

    [Fact]
    public void Convert_WritesTheStructureOfThePages()
    {
        string markdown = Convert();

        Assert.Contains("<!-- rag:page 1 -->", markdown, StringComparison.Ordinal);
        Assert.Contains("# 1 Introduction", markdown, StringComparison.Ordinal);
        Assert.Contains("## 2.1 Usage", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("Spec 1.0", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Convert_JoinsAHyphenatedWordThatTheDocumentKnows()
    {
        string markdown = Convert();

        Assert.Contains("The system reads the document and writes a summary.", markdown, StringComparison.Ordinal);
        // The two lines of the indented paragraphs stay two paragraphs.
        Assert.Contains("\n\nEach document has a title.", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Convert_WritesCodeAndFormulas()
    {
        string markdown = Convert();

        Assert.Contains("Call `glDraw` now.", markdown, StringComparison.Ordinal);
        Assert.Contains("Then the value $x_{i}$ grows.", markdown, StringComparison.Ordinal);
        Assert.Contains("```\nint x = 1;\n  return x;\n```", markdown, StringComparison.Ordinal);
        Assert.Contains("$$\ny=\\frac{a}{b}\n$$", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Convert_TurnsALinkIntoAnAnchor()
    {
        string markdown = Convert();

        Assert.Contains("See [2.1](#p2-0) below.", markdown, StringComparison.Ordinal);
        Assert.Contains("<!-- rag:anchor p2-0 -->\n## 2.1 Usage", markdown, StringComparison.Ordinal);
        DocumentStructure structure = MarkdownStructureReader.Read(new DocumentText("Spec", markdown), "spec.pdf");
        DocumentLink link = Assert.Single(structure.Links, static link => link.Kind == LinkKind.Link);
        Assert.Equal(structure.Sections.Single(static section => section.Number == "2.1").Start, structure.Anchors[link.TargetAnchor!]);
    }

    private static string Convert()
    {
        List<PdfPageModel> pages = [];
        PageBuilder first = new(1);
        first.Text("1 Introduction", 72, 700, 14, GlyphStyle.Bold, "Times-Bold");
        first.Text("The system reads the docu-", 89, 670);
        double x = first.Text("ment and writes a summary. See", 72, 658);
        double linkLeft = x + 3;
        double linkRight = first.Text("2.1", linkLeft, 658);
        first.Text("below.", linkRight + 3, 658);
        first.Link(linkLeft - 1, 655, linkRight + 1, 667, 2, 715);
        first.Text("Each document has a title.", 89, 640);
        x = first.Text("Call", 89, 620);
        x = first.Text("glDraw", x + 3, 620, 10, GlyphStyle.Mono, "Courier");
        first.Text("now.", x + 3, 620);
        x = first.Text("Then the value", 89, 600);
        x = first.Text("x", x + 3, 600, 10, MathItalic, "CMMI10");
        x = first.Text("i", x, 598, 7, MathItalic, "CMMI7");
        first.Text("grows.", x + 3, 600);
        pages.Add(Footer(first, 1));

        PageBuilder second = new(2);
        second.Text("2.1 Usage", 72, 700, 12, GlyphStyle.Bold, "Times-Bold");
        second.Text("int x = 1;", 90, 670, 10, GlyphStyle.Mono, "Courier");
        second.Text("return x;", 100, 658, 10, GlyphStyle.Mono, "Courier");
        second.Text("y", 270, 597.5, 10, MathItalic, "CMMI10");
        second.Text("=", 280, 597.5, 10, GlyphStyle.Math, "CMR10");
        second.Text("a", 290.5, 603, 10, MathItalic, "CMMI10");
        second.Text("b", 290.5, 592.5, 10, MathItalic, "CMMI10");
        second.Rule(290, 296, 600);
        second.Text("The end of the usage.", 89, 560);
        pages.Add(Footer(second, 2));

        for (int number = 3; number <= 4; number++)
        {
            PageBuilder page = new(number);
            page.Text("A page of plain text.", 89, 700);
            pages.Add(Footer(page, number));
        }

        List<PdfBookmark> bookmarks = [new("1 Introduction", 0, 1, 718), new("2.1 Usage", 1, 2, 715)];
        return PdfMarkdownImporter.Convert("Spec", pages, bookmarks).Content;
    }

    private static PdfPageModel Footer(PageBuilder page, int number)
    {
        page.Text($"Spec 1.0 - page {number}", 250, 40);
        return page.Build();
    }
}
