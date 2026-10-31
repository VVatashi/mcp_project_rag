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
    /// </summary>
    /// <returns>The right edge of the text.</returns>
    public double Text(string text, double x, double baseline, double size = 10, GlyphStyle style = GlyphStyle.None, string font = "Times-Roman")
    {
        foreach (char c in text)
        {
            if (c == ' ')
            {
                x += size * 0.3;
                continue;
            }

            double advance = size * 0.5;
            _glyphs.Add(new Glyph(c.ToString(), x, x + advance, baseline, size, style, font));
            x += advance;
        }

        return x;
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

    public PdfPageModel Build() => new(number, width, height, _glyphs, _rules, _links);
}
