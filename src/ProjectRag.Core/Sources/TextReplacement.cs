using System.Text.RegularExpressions;

namespace ProjectRag.Core.Sources;

/// <summary>
/// A fix of the text of some files of a source: a regular expression and its replacement in the Markdown of each matching file.
/// </summary>
/// <remarks>
/// A published PDF file may hold errors of its own. The Metal specification shows <c>sparse_color&lt;Tv&gt;</c> as <c>sparse_color-Tv</c>,
/// so no analysis of its glyphs can restore the brackets. A replacement fixes the text after the import, before the chunks.
/// </remarks>
public sealed class TextReplacement
{
    /// <summary>The longest time of one replacement in one file.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Gets or sets the pattern of the paths relative to the folder of the source, as in the patterns of the files.
    /// </summary>
    public string Pattern { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the regular expression of .NET to find, for example <c>sparse_color-(Tv?)\b</c>.
    /// </summary>
    public string Find { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the replacement, with <c>$1</c> for a group, for example <c>sparse_color&lt;$1&gt;</c>.
    /// </summary>
    public string Replace { get; set; } = string.Empty;

    /// <summary>
    /// Applies the replacements to a text in their order.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <param name="replacements">The replacements.</param>
    /// <returns>The changed text.</returns>
    /// <exception cref="RegexMatchTimeoutException">A replacement took longer than <see cref="Timeout"/>.</exception>
    public static string ApplyAll(string text, IEnumerable<TextReplacement> replacements)
    {
        ArgumentNullException.ThrowIfNull(replacements);
        foreach (TextReplacement replacement in replacements)
            text = Regex.Replace(text, replacement.Find, replacement.Replace, RegexOptions.Multiline | RegexOptions.CultureInvariant, Timeout);

        return text;
    }

    /// <summary>
    /// Checks whether the expression is a valid regular expression of .NET.
    /// </summary>
    /// <returns><see langword="true"/> for a valid expression.</returns>
    public bool IsValid()
    {
        if (string.IsNullOrEmpty(Find))
            return false;

        try
        {
            _ = new Regex(Find, RegexOptions.Multiline | RegexOptions.CultureInvariant, Timeout);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
