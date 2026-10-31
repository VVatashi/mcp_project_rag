using ProjectRag.Core.Documents.Pdf;

namespace ProjectRag.Core.Tests.Documents.Pdf;

public sealed class MathWriterTests
{
    [Fact]
    public void Write_MapsSymbolsAndScripts()
    {
        Item x = Glyph("x", 0, GlyphStyle.Math | GlyphStyle.Italic);
        x.Sub.Add(Glyph("i", 5, GlyphStyle.Math | GlyphStyle.Italic, 7));
        x.Sup.Add(Glyph("2", 5, GlyphStyle.Math, 7));
        List<Item> items = [x, Glyph("≤", 10, GlyphStyle.Math), Glyph("∂", 20, GlyphStyle.Math | GlyphStyle.Italic), Glyph("y", 25, GlyphStyle.Math | GlyphStyle.Italic)];

        Assert.Equal("x_{i}^{2}\\le\\partial y", MathWriter.Write(items));
    }

    [Fact]
    public void Write_NamesOperatorsAndWords()
    {
        Assert.Equal("\\max", MathWriter.Write(Word("max", 0, GlyphStyle.Math)));
        Assert.Equal("\\mathrm{floor}", MathWriter.Write(Word("floor", 0, GlyphStyle.Math)));
        Assert.Equal("\\mathit{wrap}", MathWriter.Write(Word("wrap", 0, GlyphStyle.Math | GlyphStyle.Italic)));
        Assert.Equal("\\bmod", MathWriter.Write(Word("mod", 0, GlyphStyle.Math)));
        Assert.Equal("\\text{for}", MathWriter.Write(Word("for", 0, GlyphStyle.None)));
    }

    [Fact]
    public void Write_JoinsTheSlashOfNegation()
    {
        List<Item> items = [Glyph("M", 0, GlyphStyle.Math | GlyphStyle.Italic), Glyph("\u0338", 6, GlyphStyle.Math), Glyph("=", 6, GlyphStyle.Math), Glyph("0", 12, GlyphStyle.Math)];

        Assert.Equal("M\\ne 0", MathWriter.Write(items));
    }

    [Fact]
    public void Write_KeepsPrimesAndDropsPrivateCharacters()
    {
        Item u = Glyph("u", 0, GlyphStyle.Math | GlyphStyle.Italic);
        u.Sup.Add(Glyph("′", 5, GlyphStyle.Math, 7));

        Assert.Equal("u'+", MathWriter.Write([u, Glyph("", 6, GlyphStyle.MathExtension), Glyph("+", 12, GlyphStyle.Math)]));
    }

    [Theory]
    [InlineData("\\frac{a}{b}", true)]
    [InlineData("\\{x\\}", true)]
    [InlineData("\\frac{a}{b", false)]
    [InlineData("\\begin{cases} a \\end{cases}", true)]
    [InlineData("\\begin{cases} a", false)]
    public void IsBalanced_ChecksBracesAndEnvironments(string latex, bool expected) => Assert.Equal(expected, MathWriter.IsBalanced(latex));

    [Theory]
    [InlineData("w \u0338= 0", "w }\\ne\\text{ 0")]
    [InlineData("x \u0338∈ S", "x }\\notin\\text{ S")]
    [InlineData("50% & more", "50\\% \\& more")]
    public void EscapeText_SetsANegationAsMath(string text, string expected) => Assert.Equal(expected, MathWriter.EscapeText(text));

    private static Item Glyph(string text, double left, GlyphStyle style, double size = 10) => new()
    {
        Text = text,
        Left = left,
        Right = left + (size * 0.5),
        Baseline = 0,
        Top = size * 0.75,
        Bottom = -size * 0.25,
        Size = size,
        Style = style,
    };

    private static List<Item> Word(string word, double left, GlyphStyle style) => [.. word.Select((c, i) => Glyph(c.ToString(), left + (i * 5), style))];
}
