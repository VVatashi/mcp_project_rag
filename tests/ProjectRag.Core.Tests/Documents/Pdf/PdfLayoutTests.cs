using ProjectRag.Core.Documents.Pdf;

using UglyToad.PdfPig.Core;

namespace ProjectRag.Core.Tests.Documents.Pdf;

public sealed class PdfLayoutTests
{
    private const GlyphStyle MathItalic = GlyphStyle.Math | GlyphStyle.Italic;

    [Fact]
    public void BuildLines_KeepsTheBarsOfTwoLinesApart()
    {
        // Each bar has two pieces that touch. The bars of the two lines stand one above the other with a gap.
        PageBuilder page = new(1);
        foreach (double baseline in (double[])[700, 680])
        {
            page.Glyph("∣", 90, 93, baseline + 5, 10, GlyphStyle.MathExtension, "CMEX10", baseline + 5.2, baseline - 1.8);
            page.Glyph("∣", 90, 93, baseline - 1.5, 10, GlyphStyle.MathExtension, "CMEX10", baseline - 1.3, baseline - 8.3);
            page.Text("a", 95, baseline, 10, MathItalic, "CMMI10");
        }

        List<Line> lines = PdfLayout.BuildLines(page.Build(), static glyph => glyph.IsMath);

        List<Item> bars = [.. lines.SelectMany(static line => line.Items).Where(static item => item.Text == "∣")];
        Assert.Equal(2, bars.Count);
        Assert.All(bars, static bar => Assert.InRange(bar.Top - bar.Bottom, 13, 14));
    }

    [Fact]
    public void BuildLines_JoinsTheSignAndTheRuleOfARoot()
    {
        PageBuilder page = new(1);
        page.Glyph("√", 100, 109, 690, 10, GlyphStyle.Math, "CMSY10", 700.4, 689.5);
        page.Text("2", 109.5, 691, 10, GlyphStyle.Math, "CMR10");
        page.Rule(109.3, 115, 700.2);
        page.Text("x", 120, 691, 10, MathItalic, "CMMI10");

        Line line = Assert.Single(PdfLayout.BuildLines(page.Build(), static glyph => glyph.IsMath));

        Assert.Equal("\\sqrt{2}x", MathWriter.Write(line.Items));
    }

    [Fact]
    public void BuildLines_GivesAScriptToATallParenthesis()
    {
        // The square of a large parenthesis sits far above the baseline, next to the top of the parenthesis.
        PageBuilder page = new(1);
        page.Glyph("(", 100, 108, 712, 10, GlyphStyle.MathExtension, "CMEX10", 712, 686);
        page.Text("a", 110, 696, 10, MathItalic, "CMMI10");
        page.Glyph(")", 116, 124, 712, 10, GlyphStyle.MathExtension, "CMEX10", 712, 686);
        page.Text("2", 124.5, 708, 7, GlyphStyle.Math, "CMR7");

        Line line = Assert.Single(PdfLayout.BuildLines(page.Build(), static glyph => glyph.IsMath));

        Assert.Equal("(a)^{2}", MathWriter.Write(line.Items));
    }

    [Fact]
    public void BuildLines_GivesAScriptToALargerItemInARowOfSmallCapitals()
    {
        // A table of states sets the names in small capitals and the type Z⁺ in the size of the text.
        PageBuilder page = new(1);
        page.Text("DRAW BINDING", 40, 700, 6);
        page.Text("Z", 100, 700, 10, MathItalic, "CMMI10");
        page.Text("+", 106, 703.6, 7, GlyphStyle.Math, "CMR7");
        page.Text("GetIntegerv", 120, 700, 6);

        Line line = Assert.Single(PdfLayout.BuildLines(page.Build(), static glyph => glyph.IsMath));

        Item z = Assert.Single(line.Items, static item => item.Text == "Z");
        Assert.Equal("+", Assert.Single(z.Sup).Text);
    }

    [Theory]
    [InlineData(90, 100, 200, 200)]
    [InlineData(270, 100, 200, 592)]
    [InlineData(180, 100, 200, 512)]
    public void PageFrame_TurnsTheLinesFromLeftToRight(int direction, double x, double y, double expectedX)
    {
        PageFrame frame = new(direction, 612, 792);

        PdfPoint start = frame.Turn(new PdfPoint(x, y));
        // One step along the line of the direction moves right in the turned frame.
        double radians = direction * Math.PI / 180;
        PdfPoint next = frame.Turn(new PdfPoint(x + Math.Cos(radians), y + Math.Sin(radians)));

        Assert.Equal(expectedX, start.X, 6);
        Assert.Equal(1, next.X - start.X, 6);
        Assert.Equal(0, next.Y - start.Y, 6);
        Assert.Equal(direction is 90 or 270 ? 792 : 612, frame.Width);
    }
}
