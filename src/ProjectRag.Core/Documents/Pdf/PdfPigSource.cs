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
        List<Glyph> glyphs = [];
        foreach (Letter letter in page.Letters)
        {
            if (string.IsNullOrWhiteSpace(letter.Value) || letter.PointSize <= 0)
                continue;

            GlyphStyle style = Classify(letter.FontName ?? string.Empty);
            string text = letter.Value;
            if ((style & GlyphStyle.Math) != 0 && IsCmsy(letter.FontName) && text.Length == 1 && CmsyLetters.TryGetValue(text[0], out string? symbol))
                text = symbol;
            else if ((style & GlyphStyle.Math) != 0 && IsCmsy(letter.FontName) && text.Length == 1 && text[0] is >= 'A' and <= 'Z')
                text = $"\\mathcal{{{text}}}";
            else
                text = TextCleanup.Clean(text.Normalize(NormalizationForm.FormKC));

            if (text.Length == 0)
                continue;

            PdfRectangle box = letter.BoundingBox;
            double left = letter.StartBaseLine.X;
            double right = Math.Max(left + 0.1, Math.Max(letter.EndBaseLine.X, box.Right));
            glyphs.Add(new Glyph(text, left, right, letter.StartBaseLine.Y, letter.PointSize, style, FontName(letter.FontName ?? string.Empty), box.Top, box.Bottom));
        }

        List<Rule> rules = [];
        foreach (PdfPath path in page.Paths)
        {
            if (path.GetBoundingRectangle() is { } box && box.Height <= 1.5 && box.Width >= 2 && (path.IsStroked || path.IsFilled))
                rules.Add(new Rule(box.Left, box.Right, (box.Top + box.Bottom) / 2));
        }

        List<PageLink> links = [];
        foreach (Annotation annotation in page.GetAnnotations())
        {
            if (annotation.Action is GoToAction { Destination: { } destination } && destination.PageNumber > 0)
            {
                PdfRectangle area = annotation.Rectangle;
                links.Add(new PageLink(area.Left, area.Bottom, area.Right, area.Top, destination.PageNumber, destination.Coordinates.Top));
            }
        }

        return new PdfPageModel(page.Number, page.Width, page.Height, glyphs, rules, links);
    }

    /// <summary>
    /// Gets the name of a font without the prefix of a subset: "ZBLUAG+NimbusMonL-Regu" becomes "NimbusMonL-Regu".
    /// </summary>
    /// <param name="font">The name of the font.</param>
    /// <returns>The name without the prefix.</returns>
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
