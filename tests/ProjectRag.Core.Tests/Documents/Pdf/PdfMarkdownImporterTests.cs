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

    [Fact]
    public void Convert_JoinsPseudocodeIntoOneBlockOfCode()
    {
        // The specification sets keywords in monospace, the command in the text font, the variables in math italics and the braces as math symbols.
        PageBuilder page = new(2);
        page.Text("It is equivalent to the following code, which binds each buffer of the array:", 72, 720);
        page.Text("for (i = 0; i < count; i++) {", 90, 700, 10, GlyphStyle.Mono, "Courier");
        double x = page.Text("if (", 100, 688, 10, GlyphStyle.Mono, "Courier");
        x = page.Text("buffers", x, 688, 10, MathItalic, "CMMI10");
        x = page.Text("== NULL)", x + 3, 688, 10, GlyphStyle.Mono, "Courier");
        page.Text("{", x + 3, 688, 10, GlyphStyle.Math, "CMSY10");
        x = page.Text("BindBufferBase", 110, 676);
        x = page.Text("(", x, 676, 10, GlyphStyle.Mono, "Courier");
        x = page.Text("target", x, 676, 10, MathItalic, "CMMI10");
        page.Text(", first + i, 0);", x, 676, 10, GlyphStyle.Mono, "Courier");
        x = page.Text("}", 100, 664, 10, GlyphStyle.Math, "CMSY10");
        x = page.Text("else", x + 3, 664, 10, GlyphStyle.Mono, "Courier");
        page.Text("{", x + 3, 664, 10, GlyphStyle.Math, "CMSY10");
        page.Text("}", 90, 652, 10, GlyphStyle.Math, "CMSY10");
        page.Text("except that the binding of the target stays the same as before the call.", 72, 630);

        string markdown = Convert(page);

        Assert.Contains("```\nfor (i = 0; i < count; i++) {\n  if (buffers == NULL) {\n    BindBufferBase(target, first + i, 0);\n  } else {\n}\n```", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("$", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Convert_ReadsALoopWithoutAMonospaceLineAsCode()
    {
        // No line of the loop is mostly monospace: the bound and the variables are math italics, and a comment is in the text font.
        PageBuilder page = new(2);
        page.Text("It is equivalent to the following loop over the names that the application passes:", 72, 720);
        double x = page.Text("for (int i = 0; i <", 90, 700, 10, GlyphStyle.Mono, "Courier");
        x = page.Text("count", x + 3, 700, 10, MathItalic, "CMMI10");
        x = page.Text("; i++)", x, 700, 10, GlyphStyle.Mono, "Courier");
        page.Text("{", x + 3, 700, 10, GlyphStyle.Math, "CMSY10");
        x = page.Text("Enable", 100, 688);
        x = page.Text("(", x, 688, 10, GlyphStyle.Mono, "Courier");
        x = page.Text("s", x, 688, 10, MathItalic, "CMMI10");
        x = page.Text("c", x, 686, 7, MathItalic, "CMMI7");
        page.Text(", i);", x, 688, 10, GlyphStyle.Mono, "Courier");
        page.Text("/* or */", 100, 676);
        page.Text("}", 90, 664, 10, GlyphStyle.Math, "CMSY10");
        page.Text("The loop stops at the end of the list of the names that the application passes.", 72, 640);

        string markdown = Convert(page);

        Assert.Contains("```\nfor (int i = 0; i < count; i++) {\n  Enable(s_c, i);\n  /* or */\n}\n```", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("$$", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Convert_WritesALoneClosingBraceAsCode()
    {
        // A table at the top of the page splits the end of a function from its listing on the page before.
        PageBuilder page = new(2);
        page.Text("}", 72, 700, 10, GlyphStyle.Math, "CMSY10");
        page.Text("Advanced blending equations are supported only for one color buffer.", 72, 676);

        string markdown = Convert(page);

        Assert.Contains("```\n}\n```", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("$$", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Convert_KeepsAPrototypeAtTheEndOfAPageApartFromTheNextPage()
    {
        PageBuilder first = new(1);
        first.Text("1 Introduction", 72, 700, 14, GlyphStyle.Bold, "Times-Bold");
        first.Text("The buffers are cleared with the command", 72, 670);
        first.Text("void Clear( bitfield buf );", 90, 100);
        PageBuilder second = new(2);
        second.Text("is zero or the bitwise OR of the values that name the buffers.", 72, 720);
        List<PdfBookmark> bookmarks = [new("1 Introduction", 0, 1, 718)];

        string markdown = PdfMarkdownImporter.Convert("Spec", [first.Build(), second.Build()], bookmarks).Content;

        Assert.Contains("\nis zero or the bitwise OR", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Convert_KeepsAWrappedAddressOutOfTheCode()
    {
        PageBuilder page = new(2);
        page.Text("int value = 1;", 90, 700, 10, GlyphStyle.Mono, "Courier");
        page.Text("//www.khronos.org/registry", 72, 688);

        string markdown = Convert(page);

        Assert.Contains("```\nint value = 1;\n```", markdown, StringComparison.Ordinal);
        Assert.Contains("\n//www.khronos.org/registry", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Convert_GoesOnWithTheArgumentsOfAWrappedCall()
    {
        PageBuilder page = new(2);
        page.Text("The command reads the name of a resource of the program, and it is equivalent to", 72, 720);
        double x = page.Text("GetProgramResourceName", 72, 708);
        x = page.Text("(", x, 708, 10, GlyphStyle.Mono, "Courier");
        x = page.Text("program", x, 708, 10, MathItalic, "CMMI10");
        page.Text(", UNIFORM,", x, 708, 10, GlyphStyle.Mono, "Courier");
        x = page.Text("index", 100, 696, 10, MathItalic, "CMMI10");
        x = page.Text(",", x, 696, 10, MathItalic, "CMMI10");
        x = page.Text("length", x + 3, 696, 10, MathItalic, "CMMI10");
        page.Text(");", x, 696, 10, GlyphStyle.Math, "CMR10");
        page.Text("The name ends with a null character in every case that the command handles.", 72, 676);

        string markdown = Convert(page);

        Assert.DoesNotContain("$$", markdown, StringComparison.Ordinal);
        Assert.Matches(@"GetProgramResourceName[^\n]*\\mathit\{length\}", markdown);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void Convert_TakesABoldLineOfATurnedPageForALabel(bool turned, bool heading)
    {
        PageBuilder page = new(2);
        page.Text("The diagram below shows the stages of the pipeline and the buffers between them.", 72, 720);
        page.Text("Buffer Binding", 72, 690, 10, GlyphStyle.Bold, "Times-Bold");
        page.Text("A buffer object holds the data of a stage, and a binding point names the buffer.", 72, 660);

        string markdown = Convert(page, turned);

        Assert.Equal(heading, markdown.Contains("## Buffer Binding", StringComparison.Ordinal));
    }

    [Fact]
    public void Convert_TakesANumberedBoldLineBelowTheOutlineForAHeading()
    {
        // The outline stops at the chapters, and the sections carry their numbers in bold.
        PageBuilder page = new(2);
        // A numbered line goes on from the heading "1 Introduction". A numbered label of a figure goes on from no heading.
        page.Text("1.10. Implicit Conversions", 72, 700, 13, GlyphStyle.Bold, "NotoSerif-Bold");
        page.Text("In some situations an expression and its type are converted to a different type.", 72, 680);
        page.Text("Errors", 72, 650, 10, GlyphStyle.Bold, "NotoSerif-Bold");
        page.Text("The command generates an error when the type of the expression is not valid.", 72, 630);
        page.Text("1.11. Explicit Conversions", 72, 600, 13, GlyphStyle.Bold, "NotoSerif-Bold");
        page.Text("A constructor converts the value of an expression to another type on request.", 72, 580);
        page.Text("7 Rasterization", 72, 550, 10, GlyphStyle.Bold, "NotoSerif-Bold");
        page.Text("The figure shows the stages of the pipeline from the vertices to the fragments.", 72, 530);
        PageBuilder first = new(1);
        first.Text("1 Introduction", 72, 700, 14, GlyphStyle.Bold, "Times-Bold");
        first.Text("The specification describes the commands of the library and their effects.", 72, 670);
        PageBuilder last = new(3);
        last.Text("2 Usage", 72, 700, 14, GlyphStyle.Bold, "Times-Bold");
        last.Text("The usage of the library follows the introduction to its commands.", 72, 670);
        List<PdfBookmark> bookmarks = [new("1 Introduction", 0, 1, 718), new("2 Usage", 0, 3, 718)];

        string markdown = PdfMarkdownImporter.Convert("Spec", [first.Build(), page.Build(), last.Build()], bookmarks).Content;

        Assert.Contains("\n## 1.10. Implicit Conversions", markdown, StringComparison.Ordinal);
        Assert.Contains("\n### Errors", markdown, StringComparison.Ordinal);
        Assert.Contains("\n## 1.11. Explicit Conversions", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("# 7 Rasterization", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Convert_EscapesTheDotOfANumberedItem()
    {
        PageBuilder page = new(2);
        page.Text("1. The first step reads the file.", 72, 700);

        Assert.Contains("\n1\\. The first step reads the file.", Convert(page), StringComparison.Ordinal);
    }

    [Fact]
    public void Convert_ReadsTheHeadingsFromTheFontsWhenTheOutlineNamesNoPage()
    {
        // A word processor leaves internal bookmarks such as "OLE_LINK6" without a page.
        PageBuilder page = new(1);
        page.Text("I.4 Conventions", 72, 700, 12, GlyphStyle.Bold, "Times-Bold");
        page.Text("The text of the conventions follows the heading of the section.", 72, 680);
        page.Text("I.4.1 Organization", 72, 650, 12, GlyphStyle.Bold, "Times-Bold");
        page.Text("The organization of the partitions follows the heading as well.", 72, 630);
        page.Text("The following text is informative", 72, 600, 12);

        string markdown = PdfMarkdownImporter.Convert("Spec", [page.Build()], [new("OLE_LINK6", 1, 0, 756)]).Content;

        Assert.Contains("# I.4 Conventions", markdown, StringComparison.Ordinal);
        Assert.Contains("# I.4.1 Organization", markdown, StringComparison.Ordinal);
        Assert.Matches(@"\n(#+) I\.4 Conventions[\s\S]*\n\1# I\.4\.1 Organization", markdown);
        Assert.DoesNotContain("# The following", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Convert_DropsTheLabelOfANote()
    {
        PageBuilder page = new(2);
        page.Text("For developers, ratification does not mean that an extension is better or more widely", 120, 700);
        page.Text("NOTE", 72, 694, 10, GlyphStyle.Bold, "Times-Bold");
        page.Text("supported than other ways of achieving that functionality in the application.", 120, 688);

        string markdown = Convert(page);

        Assert.DoesNotContain("NOTE", markdown, StringComparison.Ordinal);
        Assert.Contains("more widely supported than", markdown, StringComparison.Ordinal);
    }

    // A document of two pages: the first holds the heading of the outline, the second the page of a test.
    private static string Convert(PageBuilder page, bool turned = false)
    {
        PageBuilder first = new(1);
        first.Text("1 Introduction", 72, 700, 14, GlyphStyle.Bold, "Times-Bold");
        first.Text("The specification describes the commands of the library and their effects.", 72, 670);
        List<PdfBookmark> bookmarks = [new("1 Introduction", 0, 1, 718)];
        return PdfMarkdownImporter.Convert("Spec", [first.Build(), page.Build(turned)], bookmarks).Content;
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
