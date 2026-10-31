using System.Text;

using UglyToad.PdfPig;
using UglyToad.PdfPig.Actions;
using UglyToad.PdfPig.Annotations;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Graphics;
using UglyToad.PdfPig.Outline;

namespace ProjectRag.Core.Documents.Pdf;

/// <summary>
/// Turns the pages of PdfPig into <see cref="PdfPageModel"/>: the glyphs with their styles, the thin rules and the internal links.
/// </summary>
/// <remarks>
/// <para>
/// The style of a glyph comes from the name of its font. TeX writes formulas in the fonts of Computer Modern: CMMI, CMSY, CMR and CMEX.
/// CMR also sets the digits of formulas, so a CMR glyph counts as math only near other math glyphs, see <see cref="PdfLayout"/>.
/// </para>
/// <para>
/// A font of TeX without a map to Unicode gives the raw codes. PdfPig then reads the floor bracket ⌊ of CMSY as "b".
/// The map <see cref="CmsyLetters"/> restores these symbols. A capital of CMSY is a calligraphic letter.
/// </para>
/// </remarks>
internal static class PdfPigSource
{
    // The symbols of the font CMSY (the TeX encoding OMS) at the codes of the small Latin letters.
    private static readonly Dictionary<char, string> CmsyLetters = new()
    {
        ['b'] = "⌊",
        ['c'] = "⌋",
        ['d'] = "⌈",
        ['e'] = "⌉",
        ['f'] = "{",
        ['g'] = "}",
        ['h'] = "⟨",
        ['i'] = "⟩",
        ['j'] = "|",
        ['k'] = "‖",
        ['l'] = "↕",
        ['m'] = "⇕",
        ['n'] = "\\",
        ['o'] = "≀",
        ['p'] = "√",
        ['q'] = "⨿",
        ['r'] = "∇",
        ['s'] = "∫",
        ['t'] = "⊔",
        ['u'] = "⊓",
        ['v'] = "⊑",
        ['w'] = "⊒",
        ['x'] = "§",
        ['y'] = "†",
        ['z'] = "‡",
        // The digits of CMSY: the prime, infinity, element, owner, triangles, the slash of negation, the bar of a map, the quantifiers.
        ['0'] = "′",
        ['1'] = "∞",
        ['2'] = "∈",
        ['3'] = "∋",
        ['4'] = "△",
        ['5'] = "▽",
        ['6'] = "\u0338",
        ['7'] = "↦",
        ['8'] = "∀",
        ['9'] = "∃",
    };

    // The pieces of the large delimiters of CMEX at the codes of the font Symbol in the private area of Unicode.
    // An extender of a brace stays apart, see PdfLayout.Extender: it joins the brace above it.
    private static readonly Dictionary<char, string> CmexPieces = new()
    {
        ['\uF8EB'] = "(",
        ['\uF8EC'] = "(",
        ['\uF8ED'] = "(",
        ['\uF8EE'] = "[",
        ['\uF8EF'] = "[",
        ['\uF8F0'] = "[",
        ['\uF8F1'] = "{",
        ['\uF8F2'] = "{",
        ['\uF8F3'] = "{",
        ['\uF8F4'] = PdfLayout.Extender,
        ['\uF8F6'] = ")",
        ['\uF8F7'] = ")",
        ['\uF8F8'] = ")",
        ['\uF8F9'] = "]",
        ['\uF8FA'] = "]",
        ['\uF8FB'] = "]",
        ['\uF8FC'] = "}",
        ['\uF8FD'] = "}",
        ['\uF8FE'] = "}",
    };

    /// <summary>
    /// Reads the pages of a document.
    /// </summary>
    /// <param name="pdf">The document.</param>
    /// <returns>The pages in their order.</returns>
    public static List<PdfPageModel> ReadPages(PdfDocument pdf)
    {
        List<PdfPageModel> pages = [];
        foreach (Page page in pdf.GetPages())
            pages.Add(ReadPage(page));

        return pages;
    }

    /// <summary>
    /// Reads the bookmarks of a document in the order of the outline.
    /// </summary>
    /// <param name="pdf">The document.</param>
    /// <returns>The bookmarks with a page. An empty list when the document has no outline.</returns>
    public static List<PdfBookmark> ReadBookmarks(PdfDocument pdf)
    {
        List<PdfBookmark> bookmarks = [];
        if (!pdf.TryGetBookmarks(out Bookmarks? outline))
            return bookmarks;

        foreach (BookmarkNode node in outline.GetNodes())
        {
            if (node is DocumentBookmarkNode { Title: { Length: > 0 } title } document)
            {
                double? top = document.Destination.Coordinates.Top;
                bookmarks.Add(new PdfBookmark(Normalize(title), node.Level, document.PageNumber, top));
            }
        }

        return bookmarks;
    }

    /// <summary>
    /// Normalizes a text of a PDF file: the form NFKC turns ligatures such as "ﬁ" into letters, and runs of white space become one space.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <returns>The normalized text.</returns>
    public static string Normalize(string text) =>
        string.Join(' ', TextCleanup.Clean(text.Normalize(NormalizationForm.FormKC)).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static PdfPageModel ReadPage(Page page)
    {
        // A page may hold its text turned by a quarter, such as a wide table, and turned lines in the margin, such as a running header.
        // The text of the main direction turns into lines from left to right, and the other text goes away.
        List<Letter> letters = [.. page.Letters.Where(static letter => !string.IsNullOrWhiteSpace(letter.Value) && letter.PointSize > 0)];
        int direction = letters.GroupBy(Direction).OrderByDescending(static group => group.Count()).Select(static group => group.Key).FirstOrDefault();
        PageFrame frame = new(direction, page.Width, page.Height);
        List<Glyph> glyphs = [];
        foreach (Letter letter in letters)
        {
            if (Direction(letter) != direction)
                continue;

            GlyphStyle style = Classify(letter.FontName ?? string.Empty);
            string text = letter.Value;
            if ((style & GlyphStyle.Math) != 0 && IsCmsy(letter.FontName) && text.Length == 1 && CmsyLetters.TryGetValue(text[0], out string? symbol))
                text = symbol;
            else if ((style & GlyphStyle.MathExtension) != 0 && text.Length == 1 && CmexPieces.TryGetValue(text[0], out string? piece))
                text = piece;
            else if ((style & GlyphStyle.Math) != 0 && IsCmsy(letter.FontName) && text.Length == 1 && text[0] is >= 'A' and <= 'Z')
                text = $"\\mathcal{{{text}}}";
            else
                text = TextCleanup.Clean(text.Normalize(NormalizationForm.FormKC));

            if (text.Length == 0)
                continue;

            PdfRectangle box = frame.Turn(letter.BoundingBox);
            PdfPoint start = frame.Turn(letter.StartBaseLine);
            double right = Math.Max(start.X + 0.1, Math.Max(frame.Turn(letter.EndBaseLine).X, box.Right));
            glyphs.Add(new Glyph(text, start.X, right, start.Y, letter.PointSize, style, FontName(letter.FontName ?? string.Empty), box.Top, box.Bottom));
        }

        List<Rule> rules = [];
        foreach (PdfPath path in page.Paths)
        {
            if (path.GetBoundingRectangle() is { } bounds && frame.Turn(bounds) is var box && box.Height <= 1.5 && box.Width >= 2 && (path.IsStroked || path.IsFilled))
                rules.Add(new Rule(box.Left, box.Right, (box.Top + box.Bottom) / 2));
        }

        List<PageLink> links = [];
        foreach (Annotation annotation in page.GetAnnotations())
        {
            if (annotation.Action is GoToAction { Destination: { } destination } && destination.PageNumber > 0)
            {
                PdfRectangle area = frame.Turn(annotation.Rectangle);
                links.Add(new PageLink(area.Left, area.Bottom, area.Right, area.Top, destination.PageNumber, destination.Coordinates.Top));
            }
        }

        return new PdfPageModel(page.Number, frame.Width, frame.Height, glyphs, rules, links, direction != 0);
    }

    // The direction of the baseline of a letter in degrees, to a quarter turn: 0 for a line from left to right, 90 for a line from the bottom up.
    private static int Direction(Letter letter)
    {
        double degrees = Math.Atan2(letter.EndBaseLine.Y - letter.StartBaseLine.Y, letter.EndBaseLine.X - letter.StartBaseLine.X) * 180 / Math.PI;
        return ((int)Math.Round(degrees / 90) * 90 + 360) % 360;
    }

    public static string FontName(string font) => font.Contains('+', StringComparison.Ordinal) ? font[(font.IndexOf('+', StringComparison.Ordinal) + 1)..] : font;

    private static bool IsCmsy(string? font) => font is not null && (font.Contains("+CMSY", StringComparison.Ordinal) || font.Contains("+CMBSY", StringComparison.Ordinal));

    private static GlyphStyle Classify(string font)
    {
        string name = FontName(font);
        GlyphStyle style = GlyphStyle.None;
        if (name.StartsWith("CMEX", StringComparison.Ordinal))
            return GlyphStyle.MathExtension;

        if (name.StartsWith("CM", StringComparison.Ordinal) || name.StartsWith("MSBM", StringComparison.Ordinal) || name.StartsWith("MSAM", StringComparison.Ordinal)
            || name.StartsWith("wasy", StringComparison.Ordinal) || name.StartsWith("rsfs", StringComparison.Ordinal) || name.Contains("Math", StringComparison.Ordinal))
        {
            style |= GlyphStyle.Math;
            // The typewriter fonts of Computer Modern set code, not formulas. CMMI sets the italic variables.
            if (name.StartsWith("CMTT", StringComparison.Ordinal))
                style = GlyphStyle.Mono;
            else if (name.StartsWith("CMMI", StringComparison.Ordinal) || name.StartsWith("CMTI", StringComparison.Ordinal))
                style |= GlyphStyle.Italic;
        }

        if (name.Contains("Mono", StringComparison.OrdinalIgnoreCase) || name.Contains("MonL", StringComparison.Ordinal) || name.Contains("Courier", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Consol", StringComparison.OrdinalIgnoreCase) || name.Contains("Code", StringComparison.Ordinal) || name.Contains("mn-", StringComparison.Ordinal)
            || name.EndsWith("mn", StringComparison.Ordinal) || name.Contains("Menlo", StringComparison.Ordinal) || name.Contains("Typewriter", StringComparison.OrdinalIgnoreCase))
            style |= GlyphStyle.Mono;

        if (name.Contains("Bold", StringComparison.OrdinalIgnoreCase) || name.Contains("-Medi", StringComparison.Ordinal) || name.Contains("Demi", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Black", StringComparison.Ordinal) || name.StartsWith("CMB", StringComparison.Ordinal))
            style |= GlyphStyle.Bold;

        if (name.Contains("Ital", StringComparison.OrdinalIgnoreCase) || name.Contains("Oblique", StringComparison.OrdinalIgnoreCase) || name.Contains("Slant", StringComparison.Ordinal))
            style |= GlyphStyle.Italic;

        return style;
    }
}

/// <summary>
/// Turns the points of a page so the lines of one direction run from left to right, with the y axis up as in PDF.
/// </summary>
/// <param name="Direction">The direction of the lines in degrees: 0, 90, 180 or 270.</param>
/// <param name="PageWidth">The width of the page.</param>
/// <param name="PageHeight">The height of the page.</param>
internal readonly record struct PageFrame(int Direction, double PageWidth, double PageHeight)
{
    /// <summary>Gets the width of the turned page.</summary>
    public double Width => Direction is 90 or 270 ? PageHeight : PageWidth;

    /// <summary>Gets the height of the turned page.</summary>
    public double Height => Direction is 90 or 270 ? PageWidth : PageHeight;

    public PdfPoint Turn(PdfPoint point) => Direction switch
    {
        90 => new PdfPoint(point.Y, PageWidth - point.X),
        180 => new PdfPoint(PageWidth - point.X, PageHeight - point.Y),
        270 => new PdfPoint(PageHeight - point.Y, point.X),
        _ => point,
    };

    public PdfRectangle Turn(PdfRectangle rectangle)
    {
        if (Direction == 0)
            return rectangle;

        PdfPoint[] corners = [Turn(rectangle.BottomLeft), Turn(rectangle.BottomRight), Turn(rectangle.TopLeft), Turn(rectangle.TopRight)];
        return new PdfRectangle(corners.Min(static point => point.X), corners.Min(static point => point.Y), corners.Max(static point => point.X), corners.Max(static point => point.Y));
    }
}
