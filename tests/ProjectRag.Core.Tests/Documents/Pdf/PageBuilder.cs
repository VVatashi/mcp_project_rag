using ProjectRag.Core.Documents.Pdf;

namespace ProjectRag.Core.Tests.Documents.Pdf;

/// <summary>
/// Builds a synthetic page for the layout: glyphs of a fixed width, rules and links, without a PDF file.
/// </summary>
internal sealed class PageBuilder(int number, double width = 612, double height = 792)
{
    private readonly List<Glyph> _glyphs = [];
    private readonly List<Rule> _rules = [];
    private readonly List<PageLink> _links = [];

    /// <summary>
    /// Writes a text at a place. A space advances by a third of the size, a glyph by half the size.
    /// With <paramref name="spaceBefore"/>, the first glyph carries a space that the file writes before it.
    /// </summary>
    /// <returns>The right edge of the text.</returns>
    public double Text(string text, double x, double baseline, double size = 10, GlyphStyle style = GlyphStyle.None, string font = "Times-Roman", bool spaceBefore = false)
    {
        foreach (char c in text)
        {
            if (c == ' ')
            {
                x += size * 0.3;
                continue;
            }

            double advance = size * 0.5;
            _glyphs.Add(new Glyph(c.ToString(), x, x + advance, baseline, size, style, font, SpaceBefore: spaceBefore));
            spaceBefore = false;
            x += advance;
        }

        return x;
    }

    /// <summary>
    /// Adds one glyph with its outline, such as a piece of a large delimiter, whose baseline lies at its top.
    /// </summary>
    public PageBuilder Glyph(string text, double left, double right, double baseline, double size, GlyphStyle style, string font, double boxTop, double boxBottom)
    {
        _glyphs.Add(new Glyph(text, left, right, baseline, size, style, font, boxTop, boxBottom));
        return this;
    }

    public PageBuilder Rule(double left, double right, double y)
    {
        _rules.Add(new Rule(left, right, y));
        return this;
    }

    public PageBuilder Link(double left, double bottom, double right, double top, int page, double? targetTop)
    {
        _links.Add(new PageLink(left, bottom, right, top, page, targetTop));
        return this;
    }

    public PdfPageModel Build(bool turned = false) => new(number, width, height, _glyphs, _rules, _links, turned);
}
