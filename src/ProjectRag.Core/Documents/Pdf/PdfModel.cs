namespace ProjectRag.Core.Documents.Pdf;

/// <summary>
/// The style of a glyph, from its font.
/// </summary>
[Flags]
internal enum GlyphStyle
{
    /// <summary>A regular glyph of the text.</summary>
    None = 0,

    /// <summary>A bold font.</summary>
    Bold = 1,

    /// <summary>An italic font.</summary>
    Italic = 2,

    /// <summary>A monospace font: code and names of the API.</summary>
    Mono = 4,

    /// <summary>A math font of TeX: CMMI, CMSY, CMR inside formulas, MSBM.</summary>
    Math = 8,

    /// <summary>A piece of a large delimiter of TeX (CMEX).</summary>
    MathExtension = 16,
}

/// <summary>
/// A glyph of a page. The coordinates of PDF grow upward and to the right, in points.
/// </summary>
/// <param name="Text">The text of the glyph.</param>
/// <param name="Left">The left edge.</param>
/// <param name="Right">The right edge.</param>
/// <param name="Baseline">The baseline.</param>
/// <param name="Size">The size of the font.</param>
/// <param name="Style">The style.</param>
/// <param name="Font">The name of the font without the prefix of a subset, for example <c>CMMI10</c>.</param>
/// <param name="BoxTop">The top of the outline of the glyph, or <see langword="null"/> to take it from the size.</param>
/// <param name="BoxBottom">The bottom of the outline of the glyph, or <see langword="null"/> to take it from the size.</param>
internal sealed record Glyph(string Text, double Left, double Right, double Baseline, double Size, GlyphStyle Style, string Font = "", double? BoxTop = null, double? BoxBottom = null)
{
    /// <summary>
    /// Gets the top of the glyph. A large delimiter of TeX and the sign of a root use their outlines: their baseline lies at the top, not at the bottom.
    /// Other glyphs use the baseline and three quarters of the size, so the letters of a line agree.
    /// </summary>
    public double Top => HasOutline && BoxTop is { } top ? top : Baseline + (Size * 0.75);

    /// <summary>Gets the bottom of the glyph, like <see cref="Top"/>.</summary>
    public double Bottom => HasOutline && BoxBottom is { } bottom ? bottom : Baseline - (Size * 0.25);

    /// <summary>Gets a value that tells whether the glyph belongs to a formula.</summary>
    public bool IsMath => (Style & (GlyphStyle.Math | GlyphStyle.MathExtension)) != 0;

    private bool HasOutline => (Style & GlyphStyle.MathExtension) != 0 || Text == "√";
}

/// <summary>
/// A thin horizontal line of a page: a rule of a table or the bar of a fraction.
/// </summary>
/// <param name="Left">The left end.</param>
/// <param name="Right">The right end.</param>
/// <param name="Y">The height.</param>
internal sealed record Rule(double Left, double Right, double Y)
{
    /// <summary>Gets the length.</summary>
    public double Width => Right - Left;
}

/// <summary>
/// A link annotation of a page that points at a place of the document.
/// </summary>
/// <param name="Left">The left edge of the area.</param>
/// <param name="Bottom">The bottom edge.</param>
/// <param name="Right">The right edge.</param>
/// <param name="Top">The top edge.</param>
/// <param name="TargetPage">The page of the target.</param>
/// <param name="TargetTop">The height of the target on its page, or <see langword="null"/> for the top of the page.</param>
internal sealed record PageLink(double Left, double Bottom, double Right, double Top, int TargetPage, double? TargetTop);

/// <summary>
/// A page in the form that the layout analysis reads.
/// </summary>
/// <param name="Number">The number of the page, from 1.</param>
/// <param name="Width">The width.</param>
/// <param name="Height">The height.</param>
/// <param name="Glyphs">The glyphs that are not white space.</param>
/// <param name="Rules">The thin horizontal lines.</param>
/// <param name="Links">The links to places of the document.</param>
/// <param name="Turned">A value that tells whether the text of the page runs in another direction than the page, as in a turned figure.</param>
internal sealed record PdfPageModel(int Number, double Width, double Height, IReadOnlyList<Glyph> Glyphs, IReadOnlyList<Rule> Rules, IReadOnlyList<PageLink> Links, bool Turned = false);

/// <summary>
/// A bookmark of the outline of a PDF file.
/// </summary>
/// <param name="Title">The title.</param>
/// <param name="Level">The level, from 0.</param>
/// <param name="Page">The page.</param>
/// <param name="Top">The height of the target on its page, or <see langword="null"/>.</param>
internal sealed record PdfBookmark(string Title, int Level, int Page, double? Top);
