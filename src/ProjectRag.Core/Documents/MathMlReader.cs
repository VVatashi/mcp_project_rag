using System.Text;
using System.Xml.Linq;

using ProjectRag.Core.Documents.Pdf;

namespace ProjectRag.Core.Documents;

/// <summary>
/// Turns a formula of presentation MathML into LaTeX, as in the reference pages of OpenGL.
/// </summary>
/// <remarks>
/// The tokens <c>mi</c>, <c>mn</c>, <c>mo</c> and <c>mtext</c> become letters, numbers, signs and text.
/// The layouts <c>msub</c>, <c>msup</c>, <c>mfrac</c>, <c>msqrt</c>, <c>mfenced</c>, <c>mover</c>, <c>munder</c> and <c>mtable</c> become their commands.
/// Any other element writes its children. The signs share their commands with the formulas of a PDF file.
/// </remarks>
internal static class MathMlReader
{
    private static readonly Dictionary<string, string> Accents = new(StringComparer.Ordinal)
    {
        ["¯"] = @"\overline",
        ["‾"] = @"\overline",
        ["^"] = @"\hat",
        ["ˆ"] = @"\hat",
        ["~"] = @"\tilde",
        ["˜"] = @"\tilde",
        ["→"] = @"\vec",
        ["˙"] = @"\dot",
        ["¨"] = @"\ddot",
    };

    /// <summary>
    /// Reads a formula.
    /// </summary>
    /// <param name="math">The element <c>math</c>, or any element of MathML.</param>
    /// <returns>The LaTeX of the formula, without the dollars.</returns>
    public static string Read(XElement math)
    {
        ArgumentNullException.ThrowIfNull(math);
        return string.Join(' ', Write(math).Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static string Write(XElement element)
    {
        List<XElement> children = [.. element.Elements()];
        switch (element.Name.LocalName)
        {
            case "mi":
                return Identifier(element);
            case "mn":
                return Text(element);
            case "mo":
                return Operator(Text(element));
            case "mtext" or "ms":
                return Text(element) is { Length: > 0 } text ? $@"\text{{{MathWriter.EscapeText(text)}}}" : string.Empty;
            case "mspace":
                return @"\;";
            case "msub" when children.Count == 2:
                return $"{Base(children[0])}_{{{Write(children[1])}}}";
            case "msup" when children.Count == 2:
                return $"{Base(children[0])}^{{{Write(children[1])}}}";
            case "msubsup" when children.Count == 3:
                return $"{Base(children[0])}_{{{Write(children[1])}}}^{{{Write(children[2])}}}";
            case "mfrac" when children.Count == 2:
                return $@"\frac{{{Write(children[0])}}}{{{Write(children[1])}}}";
            case "msqrt":
                return $@"\sqrt{{{Row(children)}}}";
            case "mroot" when children.Count == 2:
                return $@"\sqrt[{Write(children[1])}]{{{Write(children[0])}}}";
            case "mfenced":
                return Fenced(element, children);
            case "mover" when children.Count == 2:
                return Over(children[0], children[1]);
            case "munder" when children.Count == 2:
                return Under(children[0], children[1]);
            case "munderover" when children.Count == 3:
                return $"{Base(children[0])}_{{{Write(children[1])}}}^{{{Write(children[2])}}}";
            case "mtable":
                return Table(children);
            case "mphantom" or "annotation" or "annotation-xml" or "none" or "mprescripts":
                return string.Empty;
            case "semantics":
                return children.Count > 0 ? Write(children[0]) : string.Empty;
            default:
                return Row(children);
        }
    }

    // The children of a row stand side by side. A space keeps a command apart from the next letter.
    private static string Row(IEnumerable<XElement> children) => string.Join(' ', children.Select(Write).Where(static part => part.Length > 0));

    // A base of more than one sign needs braces, so a script belongs to all of it.
    private static string Base(XElement element)
    {
        string latex = Write(element);
        bool single = latex.Length == 1 || (latex.StartsWith('\\') && latex.Skip(1).All(char.IsLetter));
        return single ? latex : $"{{{latex}}}";
    }

    private static string Identifier(XElement element)
    {
        string text = Text(element);
        if (text.Length == 0)
            return string.Empty;

        // Some pages set words in an identifier: "for a 1D texture". An identifier with spaces is text.
        if (text.Contains(' ', StringComparison.Ordinal))
            return $@"\text{{{MathWriter.EscapeText(text)}}}";

        // An identifier with a Greek letter, such as δs, writes each letter apart: \delta s.
        if (text.Any(static c => MathWriter.Symbol(c.ToString()) != c.ToString()))
            return string.Join(' ', text.Select(static c => MathWriter.Symbol(c.ToString())).Where(static part => part.Length > 0));

        // An identifier with a digit, such as s1, stays as it is.
        if (!text.All(char.IsLetter))
            return text;

        // An identifier of more than one letter is upright unless the page asks for italics. One letter is italic unless the page asks for upright.
        string? variant = element.Attribute("mathvariant")?.Value;
        if (text.Length == 1 && variant == "normal")
            return $@"\mathrm{{{text}}}";

        return MathWriter.MathWord(text, variant is "italic" || (variant is null && text.Length == 1));
    }

    private static string Operator(string text)
    {
        if (text.Length == 0)
            return string.Empty;

        // A word such as mod or max is a named operator. A backslash is a sign, not a command.
        if (text == "\\")
            return @"\backslash";

        return text.Length > 1 && text.All(char.IsLetter) ? MathWriter.MathWord(text, italic: false) : MathWriter.Symbol(text);
    }

    // The fences default to parentheses, and the separators default to commas. The last separator repeats.
    private static string Fenced(XElement element, List<XElement> children)
    {
        string open = Fence(element.Attribute("open")?.Value ?? "(");
        string close = Fence(element.Attribute("close")?.Value ?? ")");
        string separators = string.Concat((element.Attribute("separators")?.Value ?? ",").Where(static c => !char.IsWhiteSpace(c)));
        StringBuilder builder = new StringBuilder(open).Append(' ');
        for (int i = 0; i < children.Count; i++)
        {
            if (i > 0 && separators.Length > 0)
                builder.Append(Operator(separators[Math.Min(i - 1, separators.Length - 1)].ToString())).Append(' ');

            builder.Append(Write(children[i]));
        }

        return builder.Append(' ').Append(close).ToString();
    }

    private static string Fence(string text) => text.Trim() switch
    {
        "" => string.Empty,
        "{" => @"\{",
        "}" => @"\}",
        string sign => MathWriter.Symbol(sign),
    };

    // A mark over the base is an accent. Any other box over the base is a limit.
    private static string Over(XElement body, XElement mark)
    {
        string sign = Text(mark);
        return mark.Name.LocalName == "mo" && Accents.TryGetValue(sign, out string? accent)
            ? $"{accent}{{{Write(body)}}}"
            : $"{Base(body)}^{{{Write(mark)}}}";
    }

    // A mark under the base is an accent. Any other box under the base is a limit.
    private static string Under(XElement body, XElement mark)
    {
        string sign = Text(mark);
        if (mark.Name.LocalName == "mo" && sign is "_" or "‾" or "¯")
            return $@"\underline{{{Write(body)}}}";

        if (mark.Name.LocalName == "mo" && sign == "⏟")
            return $@"\underbrace{{{Write(body)}}}";

        return $"{Base(body)}_{{{Write(mark)}}}";
    }

    // A row with a label holds the label first. The label is not a column of the formula.
    private static string Table(List<XElement> rows)
    {
        List<List<string>> cells = [.. rows.Select(static row => row.Name.LocalName switch
        {
            "mtr" => row.Elements().Select(Write).ToList(),
            "mlabeledtr" => row.Elements().Skip(1).Select(Write).ToList(),
            _ => [Write(row)],
        })];
        int columns = cells.Count == 0 ? 0 : cells.Max(static row => row.Count);
        return $@"\begin{{array}}{{{new string('c', Math.Max(1, columns))}}}{string.Join(@" \\ ", cells.Select(static row => string.Join(" & ", row)))}\end{{array}}";
    }

    private static string Text(XElement element) => string.Join(' ', element.Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
