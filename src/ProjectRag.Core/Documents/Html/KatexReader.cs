using System.Globalization;
using System.Text;

using AngleSharp.Dom;

namespace ProjectRag.Core.Documents.Html;

/// <summary>
/// Reads the LaTeX back from a formula that KaTeX drew on the server, as in the HTML of the Vulkan specification.
/// </summary>
/// <remarks>
/// The page keeps only the boxes of KaTeX, without the source of the formula. The boxes name their roles in their classes:
/// <c>mfrac</c>, <c>msupsub</c>, <c>sqrt</c>, <c>mtable</c> and others. A stack of boxes sets each box at a height, its <c>top</c>,
/// against a strut. A box above the strut is a numerator or a superscript, and a box below it is a denominator or a subscript.
/// The formula keeps its meaning for the search and for KaTeX, but not every space and size of the source.
/// </remarks>
internal static class KatexReader
{
    private static readonly Dictionary<string, string> Symbols = new(StringComparer.Ordinal)
    {
        ["−"] = "-",
        ["×"] = @"\times",
        ["÷"] = @"\div",
        ["⋅"] = @"\cdot",
        ["∗"] = "*",
        ["±"] = @"\pm",
        ["≤"] = @"\leq",
        ["≥"] = @"\geq",
        ["≠"] = @"\neq",
        ["≈"] = @"\approx",
        ["≡"] = @"\equiv",
        ["→"] = @"\rightarrow",
        ["←"] = @"\leftarrow",
        ["∞"] = @"\infty",
        ["…"] = @"\ldots",
        ["⋯"] = @"\cdots",
        ["∈"] = @"\in",
        ["∧"] = @"\land",
        ["∨"] = @"\lor",
        ["¬"] = @"\lnot",
        ["⌊"] = @"\lfloor",
        ["⌋"] = @"\rfloor",
        ["⌈"] = @"\lceil",
        ["⌉"] = @"\rceil",
        ["{"] = @"\{",
        ["}"] = @"\}",
        ["∣"] = "|",
        ["∥"] = @"\|",
        ["∑"] = @"\sum",
        ["∏"] = @"\prod",
        ["∫"] = @"\int",
        ["√"] = @"\surd",
        ["∘"] = @"\circ",
        ["⊕"] = @"\oplus",
        ["⊗"] = @"\otimes",
        ["α"] = @"\alpha",
        ["β"] = @"\beta",
        ["γ"] = @"\gamma",
        ["δ"] = @"\delta",
        ["ε"] = @"\epsilon",
        ["θ"] = @"\theta",
        ["λ"] = @"\lambda",
        ["μ"] = @"\mu",
        ["π"] = @"\pi",
        ["ρ"] = @"\rho",
        ["σ"] = @"\sigma",
        ["τ"] = @"\tau",
        ["φ"] = @"\phi",
        ["ω"] = @"\omega",
        ["Δ"] = @"\Delta",
    };

    // A delimiter of many pieces, such as a tall parenthesis or brace, shows its kind in any of its pieces.
    private static readonly Dictionary<char, string> Pieces = new()
    {
        ['⎛'] = "(",
        ['⎜'] = "(",
        ['⎝'] = "(",
        ['⎞'] = ")",
        ['⎟'] = ")",
        ['⎠'] = ")",
        ['⎡'] = "[",
        ['⎢'] = "[",
        ['⎣'] = "[",
        ['⎤'] = "]",
        ['⎥'] = "]",
        ['⎦'] = "]",
        ['⎧'] = @"\{",
        ['⎨'] = @"\{",
        ['⎩'] = @"\{",
        ['⎫'] = @"\}",
        ['⎬'] = @"\}",
        ['⎭'] = @"\}",
        ['∣'] = "|",
        ['⌈'] = @"\lceil",
        ['⌉'] = @"\rceil",
        ['⌊'] = @"\lfloor",
        ['⌋'] = @"\rfloor",
    };

    private static readonly Dictionary<char, string> Accents = new()
    {
        ['^'] = @"\hat",
        ['ˉ'] = @"\bar",
        ['~'] = @"\tilde",
        ['→'] = @"\vec",
        ['˙'] = @"\dot",
        ['¨'] = @"\ddot",
    };

    /// <summary>
    /// Reads a formula of KaTeX.
    /// </summary>
    /// <param name="katex">The element with the class <c>katex</c>.</param>
    /// <returns>The LaTeX of the formula.</returns>
    public static string Read(IElement katex)
    {
        // KaTeX may also write MathML with the source of the formula. That source is exact.
        if (katex.QuerySelector("annotation[encoding='application/x-tex']") is { } annotation)
            return annotation.TextContent.Trim();

        IElement root = katex.QuerySelector(".katex-html") ?? katex;
        StringBuilder builder = new();
        foreach (IElement child in root.Children.Where(static child => !child.ClassList.Contains("tag")))
            Write(child, builder);

        return Tidy(builder.ToString());
    }

    private static void Write(IElement element, StringBuilder builder)
    {
        ITokenList classes = element.ClassList;
        if (classes.Contains("strut") || classes.Contains("pstrut") || classes.Contains("mspace") || classes.Contains("nulldelimiter")
            || classes.Contains("vlist-s") || classes.Contains("frac-line") || classes.Contains("hide-tail") || element.LocalName == "svg")
            return;

        if (classes.Contains("mfrac"))
        {
            List<(double Shift, IElement Box)> boxes = Stack(element);
            IElement? numerator = boxes.Where(static box => box.Shift > 0).Select(static box => box.Box).LastOrDefault();
            IElement? denominator = boxes.Where(static box => box.Shift < 0).Select(static box => box.Box).FirstOrDefault();
            builder.Append(@"\frac{").Append(Inner(numerator)).Append("}{").Append(Inner(denominator)).Append('}');
            return;
        }

        if (classes.Contains("msupsub"))
        {
            foreach ((double shift, IElement box) in Stack(element).OrderBy(static box => box.Shift))
                builder.Append(shift > 0 ? "^{" : "_{").Append(Inner(box)).Append('}');

            return;
        }

        if (classes.Contains("op-limits"))
        {
            List<(double Shift, IElement Box)> boxes = Stack(element);
            IElement? op = boxes.OrderBy(static box => Math.Abs(box.Shift)).Select(static box => box.Box).FirstOrDefault();
            builder.Append(Inner(op));
            foreach ((double shift, IElement box) in boxes.Where(box => box.Box != op).OrderBy(static box => box.Shift))
                builder.Append(shift > 0 ? "^{" : "_{").Append(Inner(box)).Append('}');

            return;
        }

        if (classes.Contains("sqrt"))
        {
            builder.Append(@"\sqrt");
            if (element.Children.FirstOrDefault(static child => child.ClassList.Contains("root")) is { } index)
                builder.Append('[').Append(Inner(index)).Append(']');

            IElement? body = element.QuerySelector(".svg-align") ?? Stack(element).Select(static box => box.Box).FirstOrDefault();
            builder.Append('{').Append(Inner(body)).Append('}');
            return;
        }

        if (classes.Contains("mtable"))
        {
            Table(element, builder);
            return;
        }

        if (classes.Contains("accent"))
        {
            List<(double Shift, IElement Box)> boxes = Stack(element);
            IElement? mark = element.QuerySelector(".accent-body");
            char sign = mark?.TextContent.Trim() is [char only] ? only : '^';
            IElement? body = boxes.Select(static box => box.Box).FirstOrDefault(box => mark is null || !box.Contains(mark));
            builder.Append(Accents.GetValueOrDefault(sign, @"\hat")).Append('{').Append(Inner(body)).Append('}');
            return;
        }

        if (classes.Contains("overline") || classes.Contains("underline"))
        {
            List<(double Shift, IElement Box)> boxes = Stack(element);
            IElement? body = boxes.Select(static box => box.Box).FirstOrDefault(static box => !box.ClassList.Contains("overline-line") && !box.ClassList.Contains("underline-line"));
            builder.Append(classes.Contains("overline") ? @"\overline{" : @"\underline{").Append(Inner(body)).Append('}');
            return;
        }

        if (classes.Contains("delimsizing") && classes.Contains("mult"))
        {
            string? delimiter = element.TextContent.Where(Pieces.ContainsKey).Select(static piece => Pieces[piece]).FirstOrDefault();
            builder.Append(delimiter ?? string.Empty).Append(' ');
            return;
        }

        // A text inside a formula, such as \text{otherwise}, and a word of an operator, such as \max.
        if (classes.Contains("text") && classes.Contains("mord") && element.Children.Length > 0)
        {
            builder.Append(@"\text{").Append(TextOf(element)).Append("} ");
            return;
        }

        if (classes.Contains("mop") && element.Children.Length == 0 && element.TextContent.Trim() is { Length: > 1 } word && word.All(char.IsLetter))
        {
            builder.Append(@"\operatorname{").Append(word).Append("} ");
            return;
        }

        if (element.Children.Length == 0)
        {
            Leaf(element, builder);
            return;
        }

        string? font = classes.Contains("mathbf") ? @"\mathbf{" : classes.Contains("mathrm") ? @"\mathrm{" : null;
        if (font is not null)
            builder.Append(font);

        foreach (IElement child in element.Children)
            Write(child, builder);

        if (font is not null)
            builder.Append('}');
    }

    private static void Leaf(IElement element, StringBuilder builder)
    {
        // KaTeX pads some boxes with a space of no width.
        string text = Visible(element.TextContent);
        if (text.Length == 0)
            return;

        ITokenList classes = element.ClassList;
        string? font = classes.Contains("mathbf") ? @"\mathbf{" : classes.Contains("mathrm") && text.Length > 1 ? @"\mathrm{" : null;
        if (font is not null)
            builder.Append(font);

        foreach (Rune rune in text.EnumerateRunes())
        {
            string sign = rune.ToString();
            if (Symbols.TryGetValue(sign, out string? command))
                builder.Append(command).Append(command[0] == '\\' && char.IsLetter(command[^1]) ? " " : string.Empty);
            else if (sign is "#" or "$" or "%" or "&" or "_")
                builder.Append('\\').Append(sign);
            else
                builder.Append(sign);
        }

        if (font is not null)
            builder.Append('}');
    }

    // The rows of an array are boxes of a stack in each column, from the top down. The columns stand side by side.
    private static void Table(IElement table, StringBuilder builder)
    {
        List<IElement> columns = [.. table.Children.Where(static child => child.ClassName?.Contains("col-align-", StringComparison.Ordinal) == true)];
        List<List<string>> cells = [.. columns.Select(static column => Stack(column).OrderByDescending(static box => box.Shift).Select(static box => Inner(box.Box)).ToList())];
        int rows = cells.Count == 0 ? 0 : cells.Max(static column => column.Count);
        string align = string.Concat(columns.Select(static column => column.ClassName!.Contains("col-align-l", StringComparison.Ordinal) ? 'l' : column.ClassName.Contains("col-align-r", StringComparison.Ordinal) ? 'r' : 'c'));
        builder.Append(@"\begin{array}{").Append(align).Append('}');
        for (int row = 0; row < rows; row++)
        {
            if (row > 0)
                builder.Append(@" \\ ");

            builder.AppendJoin(" & ", cells.Select(column => row < column.Count ? column[row] : string.Empty));
        }

        builder.Append(@"\end{array}");
    }

    // The boxes of the first stack inside an element, each with its shift up from the strut. A positive shift lies above the strut.
    private static List<(double Shift, IElement Box)> Stack(IElement element)
    {
        IElement? list = element.QuerySelector(".vlist-r > .vlist");
        if (list is null)
            return [];

        List<(double Shift, IElement Box)> boxes = [];
        foreach (IElement child in list.Children)
        {
            IElement? strut = child.Children.FirstOrDefault(static item => item.ClassList.Contains("pstrut"));
            IElement? box = child.Children.LastOrDefault(static item => !item.ClassList.Contains("pstrut"));
            if (box is null || Em(child.GetAttribute("style"), "top") is not { } top)
                continue;

            double height = Em(strut?.GetAttribute("style"), "height") ?? 0;
            boxes.Add((-top - height, box));
        }

        return boxes;
    }

    private static double? Em(string? style, string property)
    {
        if (style is null)
            return null;

        foreach (string part in style.Split(';'))
        {
            string[] pair = part.Split(':', 2);
            if (pair.Length == 2 && pair[0].Trim() == property && pair[1].Trim() is var value && value.EndsWith("em", StringComparison.Ordinal)
                && double.TryParse(value[..^2], NumberStyles.Float, CultureInfo.InvariantCulture, out double em))
                return em;
        }

        return null;
    }

    private static string Inner(IElement? element)
    {
        if (element is null)
            return string.Empty;

        StringBuilder builder = new();
        Write(element, builder);
        return Tidy(builder.ToString());
    }

    // The signs that LaTeX reads as commands in its text mode get an escape.
    private static string TextOf(IElement element)
    {
        StringBuilder builder = new();
        foreach (char c in Visible(element.TextContent).Trim())
            builder.Append(c is '#' or '$' or '%' or '&' or '_' or '{' or '}' ? $"\\{c}" : c.ToString());

        return builder.ToString();
    }

    // KaTeX pads boxes with a space of no width and with signs of the private area of Unicode, such as U+E020. They carry no text.
    private static string Visible(string text) =>
        text.Any(static c => c == '​' || char.GetUnicodeCategory(c) == UnicodeCategory.PrivateUse)
            ? string.Concat(text.Where(static c => c != '​' && char.GetUnicodeCategory(c) != UnicodeCategory.PrivateUse))
            : text;

    private static string Tidy(string latex) => string.Join(' ', latex.Split(' ', StringSplitOptions.RemoveEmptyEntries));
}
