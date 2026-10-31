using System.Text;

namespace ProjectRag.Core.Documents.Pdf;

/// <summary>
/// Writes the items of a formula as LaTeX that KaTeX and MathJax render.
/// </summary>
/// <remarks>
/// <para>
/// A symbol becomes its command, for example "≤" becomes <c>\le</c>. A run of upright letters of a math font is an operator name or a word:
/// <c>max</c> becomes <c>\max</c>, and another word becomes <c>\mathrm{…}</c>. A run of italic letters becomes <c>\mathit{…}</c>, so <c>wrap</c> keeps the spacing of a name.
/// A word of the text font inside a formula becomes <c>\text{…}</c>. A script follows its base as <c>_{…}</c> or <c>^{…}</c>, and primes become <c>'</c>.
/// </para>
/// <para>
/// A large delimiter becomes the plain delimiter, without <c>\left</c> and <c>\right</c>. TeX may stack only one side of a pair, and a lone <c>\left</c> is an error.
/// </para>
/// </remarks>
internal static class MathWriter
{
    private static readonly Dictionary<string, string> Symbols = new(StringComparer.Ordinal)
    {
        ["α"] = "\\alpha",
        ["β"] = "\\beta",
        ["γ"] = "\\gamma",
        ["δ"] = "\\delta",
        ["ε"] = "\\epsilon",
        ["ϵ"] = "\\epsilon",
        ["ζ"] = "\\zeta",
        ["η"] = "\\eta",
        ["θ"] = "\\theta",
        ["ϑ"] = "\\vartheta",
        ["ι"] = "\\iota",
        ["κ"] = "\\kappa",
        ["λ"] = "\\lambda",
        ["μ"] = "\\mu",
        ["ν"] = "\\nu",
        ["ξ"] = "\\xi",
        ["π"] = "\\pi",
        ["ρ"] = "\\rho",
        ["σ"] = "\\sigma",
        ["τ"] = "\\tau",
        ["υ"] = "\\upsilon",
        ["φ"] = "\\phi",
        ["ϕ"] = "\\phi",
        ["χ"] = "\\chi",
        ["ψ"] = "\\psi",
        ["ω"] = "\\omega",
        ["Γ"] = "\\Gamma",
        ["Δ"] = "\\Delta",
        ["Θ"] = "\\Theta",
        ["Λ"] = "\\Lambda",
        ["Ξ"] = "\\Xi",
        ["Π"] = "\\Pi",
        ["Σ"] = "\\Sigma",
        ["Υ"] = "\\Upsilon",
        ["Φ"] = "\\Phi",
        ["Ψ"] = "\\Psi",
        ["Ω"] = "\\Omega",
        ["≤"] = "\\le",
        ["≥"] = "\\ge",
        ["≠"] = "\\ne",
        ["≈"] = "\\approx",
        ["≡"] = "\\equiv",
        ["∼"] = "\\sim",
        ["≃"] = "\\simeq",
        ["∝"] = "\\propto",
        ["∈"] = "\\in",
        ["∉"] = "\\notin",
        ["⊂"] = "\\subset",
        ["⊆"] = "\\subseteq",
        ["⊃"] = "\\supset",
        ["⊇"] = "\\supseteq",
        ["∪"] = "\\cup",
        ["∩"] = "\\cap",
        ["∞"] = "\\infty",
        ["∂"] = "\\partial",
        ["∇"] = "\\nabla",
        ["∑"] = "\\sum",
        ["∏"] = "\\prod",
        ["∫"] = "\\int",
        ["√"] = "\\surd",
        ["±"] = "\\pm",
        ["∓"] = "\\mp",
        ["×"] = "\\times",
        ["÷"] = "\\div",
        ["·"] = "\\cdot",
        ["⋅"] = "\\cdot",
        ["∗"] = "\\ast",
        ["◦"] = "\\circ",
        ["∘"] = "\\circ",
        ["→"] = "\\to",
        ["←"] = "\\leftarrow",
        ["↔"] = "\\leftrightarrow",
        ["⇒"] = "\\Rightarrow",
        ["⇐"] = "\\Leftarrow",
        ["⇔"] = "\\Leftrightarrow",
        ["↦"] = "\\mapsto",
        ["↕"] = "\\updownarrow",
        ["⇕"] = "\\Updownarrow",
        ["⌊"] = "\\lfloor",
        ["⌋"] = "\\rfloor",
        ["⌈"] = "\\lceil",
        ["⌉"] = "\\rceil",
        ["⟨"] = "\\langle",
        ["⟩"] = "\\rangle",
        ["{"] = "\\{",
        ["}"] = "\\}",
        ["‖"] = "\\|",
        ["∣"] = "|",
        ["−"] = "-",
        ["–"] = "-",
        ["…"] = "\\ldots",
        ["⋯"] = "\\cdots",
        ["⋮"] = "\\vdots",
        ["¬"] = "\\neg",
        ["∧"] = "\\wedge",
        ["∨"] = "\\vee",
        ["∀"] = "\\forall",
        ["∃"] = "\\exists",
        ["∅"] = "\\emptyset",
        ["ℓ"] = "\\ell",
        ["§"] = "\\S",
        ["†"] = "\\dagger",
        ["‡"] = "\\ddagger",
        ["⊕"] = "\\oplus",
        ["⊗"] = "\\otimes",
        ["⊙"] = "\\odot",
        ["⊔"] = "\\sqcup",
        ["⊓"] = "\\sqcap",
        ["⊑"] = "\\sqsubseteq",
        ["⊒"] = "\\sqsupseteq",
        ["≀"] = "\\wr",
        ["⨿"] = "\\amalg",
        ["\\"] = "\\backslash",
        ["•"] = "\\bullet",
        ["∋"] = "\\ni",
        ["△"] = "\\triangle",
        ["▽"] = "\\triangledown",
        ["∠"] = "\\angle",
        ["⊥"] = "\\perp",
        ["∥"] = "\\parallel",
        ["%"] = "\\%",
        ["#"] = "\\#",
        ["&"] = "\\&",
        ["_"] = "\\_",
        ["$"] = "\\$",
        ["^"] = "\\hat{}",
        ["~"] = "\\sim",
        ["′"] = "'",
        ["″"] = "''",
    };

    private static readonly HashSet<string> Operators = new(StringComparer.Ordinal)
    {
        "max", "min", "log", "ln", "lg", "exp", "sin", "cos", "tan", "cot", "sec", "csc", "arcsin", "arccos", "arctan", "sinh", "cosh", "tanh",
        "lim", "sup", "inf", "det", "dim", "arg", "deg", "gcd", "ker", "Pr", "mod",
    };

    /// <summary>
    /// Writes rows of items, such as the numerator of a fraction, as one formula. Rows join with a space.
    /// </summary>
    public static string WriteRows(IEnumerable<(List<Item> Items, double Baseline, double Size)> rows) =>
        string.Join(' ', rows.Select(static row => Write(row.Items)).Where(static text => text.Length > 0));

    /// <summary>
    /// Writes the items of one row as a formula.
    /// </summary>
    /// <param name="items">The items from left to right.</param>
    /// <returns>The LaTeX.</returns>
    public static string Write(IReadOnlyList<Item> items)
    {
        StringBuilder builder = new();
        int i = 0;
        while (i < items.Count)
        {
            Item item = items[i];
            // A wide gap inside a formula separates two parts, such as two conditions.
            if (i > 0 && item.Left - items[i - 1].Right > item.Size * 1.2)
                Append(builder, "\\quad ");

            // TeX sets "≠" as the slash of negation over "=". The slash alone negates the next relation.
            if (item.Text == "\u0338")
            {
                string? next = i + 1 < items.Count ? items[i + 1].Text : null;
                Append(builder, next switch { "=" => "\\ne ", "∈" => "\\notin ", "≡" => "\\not\\equiv ", _ => "\\not " });
                i += next is "=" or "∈" or "≡" ? 2 : 1;
                continue;
            }

            if (item.IsLatex)
            {
                // A part with scripts of its own, such as a sum with its limits, takes more scripts only inside braces.
                Append(builder, item.Sub.Count > 0 || item.Sup.Count > 0 ? $"{{{item.Text}}}" : item.Text);
                AppendScripts(builder, item);
                i++;
                continue;
            }

            // A run of letters of one style: a word of the text, an operator name or a name of several letters.
            if (IsLetter(item))
            {
                int end = i + 1;
                while (end < items.Count && IsLetter(items[end]) && SameStyle(items[end], item) && items[end - 1].Sub.Count == 0 && items[end - 1].Sup.Count == 0
                    && !PdfLayout.NeedsSpace(items[end - 1], items[end]))
                    end++;

                string word = string.Concat(items.Skip(i).Take(end - i).Select(static run => run.Text));
                Append(builder, Word(word, item));
                AppendScripts(builder, items[end - 1]);
                i = end;
                continue;
            }

            if (!item.IsMath && !item.Text.Any(char.IsLetterOrDigit) && item.Text is not ("(" or ")" or "[" or "]" or "=" or "+" or "," or "." or "/" or "<" or ">" or "|" or ":" or ";" or "!" or "*"))
            {
                Append(builder, $"\\text{{{EscapeText(item.Text)}}}");
                AppendScripts(builder, item);
                i++;
                continue;
            }

            Append(builder, Symbol(item.Text));
            AppendScripts(builder, item);
            i++;
        }

        return builder.ToString().Trim();
    }

    /// <summary>
    /// Writes the text of the text font inside a formula as <c>\text{…}</c>, with the spaces of the gaps.
    /// </summary>
    public static string Text(IReadOnlyList<Item> items)
    {
        // A raised number after a word, such as the mark of a footnote, leaves the text as a superscript of the formula.
        StringBuilder builder = new();
        List<Item> run = [];
        foreach (Item item in items)
        {
            run.Add(new Item { Text = item.Text, Left = item.Left, Right = item.Right, Baseline = item.Baseline, Top = item.Top, Bottom = item.Bottom, Size = item.Size, Style = item.Style, Font = item.Font });
            if (item.Sup.Count == 0 && item.Sub.Count == 0)
                continue;

            builder.Append("\\text{").Append(EscapeText(PdfLayout.PlainText(run))).Append('}');
            AppendScripts(builder, item);
            run.Clear();
        }

        if (run.Count > 0)
            builder.Append("\\text{").Append(EscapeText(PdfLayout.PlainText(run))).Append('}');

        return builder.ToString();
    }

    /// <summary>
    /// Tells whether LaTeX has balanced braces and environments, so a renderer reads it without an error.
    /// </summary>
    /// <param name="latex">The LaTeX.</param>
    /// <returns><see langword="true"/> when every brace and every environment closes.</returns>
    public static bool IsBalanced(string latex)
    {
        ArgumentNullException.ThrowIfNull(latex);
        int depth = 0;
        for (int i = 0; i < latex.Length; i++)
        {
            if (latex[i] == '\\')
            {
                i++;
                continue;
            }

            depth += latex[i] switch { '{' => 1, '}' => -1, _ => 0 };
            if (depth < 0)
                return false;
        }

        int begins = CountOf(latex, "\\begin{");
        return depth == 0 && begins == CountOf(latex, "\\end{");
    }

    /// <summary>
    /// Escapes the special characters of LaTeX in a text for <c>\text{…}</c>.
    /// </summary>
    public static string EscapeText(string text)
    {
        StringBuilder builder = new(text.Length);
        foreach (char c in text)
        {
            if (char.GetUnicodeCategory(c) is System.Globalization.UnicodeCategory.PrivateUse or System.Globalization.UnicodeCategory.Format or System.Globalization.UnicodeCategory.Control)
                continue;

            builder.Append(c switch
            {
                '\\' => "\\textbackslash{}",
                '{' or '}' or '#' or '$' or '%' or '&' or '_' => $"\\{c}",
                '^' => "\\textasciicircum{}",
                '~' => "\\textasciitilde{}",
                _ => c.ToString(),
            });
        }

        return builder.ToString();
    }

    private static int CountOf(string text, string value)
    {
        int count = 0;
        for (int index = text.IndexOf(value, StringComparison.Ordinal); index >= 0; index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
            count++;

        return count;
    }

    private static bool IsLetter(Item item) => item.Text.Length > 0 && item.Text.All(char.IsLetter) && !Symbols.ContainsKey(item.Text);

    private static bool SameStyle(Item a, Item b) => a.IsMath == b.IsMath && (a.Style & GlyphStyle.Italic) == (b.Style & GlyphStyle.Italic) && Math.Abs(a.Size - b.Size) < 0.5;

    private static string Word(string word, Item style)
    {
        if (!style.IsMath)
            return $"\\text{{{EscapeText(word)}}}";

        if (word.Length == 1)
            return word;

        if ((style.Style & GlyphStyle.Italic) != 0)
            return $"\\mathit{{{word}}}";

        // \mod takes an argument in KaTeX. The binary operator is \bmod.
        if (word == "mod")
            return "\\bmod";

        return Operators.Contains(word) ? $"\\{word}" : $"\\mathrm{{{word}}}";
    }

    private static string Symbol(string text)
    {
        if (text.StartsWith('\\'))
            return text;

        if (Symbols.TryGetValue(text, out string? command))
            return command;

        // A glyph of a TeX font without a map to Unicode may come as a character of the private use area. KaTeX rejects it, and it carries no meaning.
        StringBuilder builder = new();
        foreach (char c in text)
        {
            if (char.GetUnicodeCategory(c) is System.Globalization.UnicodeCategory.PrivateUse or System.Globalization.UnicodeCategory.Format
                or System.Globalization.UnicodeCategory.Control or System.Globalization.UnicodeCategory.NonSpacingMark or System.Globalization.UnicodeCategory.OtherNotAssigned)
                continue;

            builder.Append(Symbols.TryGetValue(c.ToString(), out string? part) ? part : c.ToString());
        }

        return builder.ToString();
    }

    private static void AppendScripts(StringBuilder builder, Item item)
    {
        if (item.Sub.Count > 0)
            builder.Append("_{").Append(Write(item.Sub)).Append('}');

        if (item.Sup.Count > 0)
        {
            // Primes stay primes: x' is the TeX of x′.
            if (item.Sup.All(static script => script.Text is "′" or "″"))
                builder.Append(string.Concat(item.Sup.Select(static script => script.Text == "″" ? "''" : "'")));
            else
                builder.Append("^{").Append(Write(item.Sup)).Append('}');
        }
    }

    // A command that ends with a letter needs a space before the next letter, so "\le x" does not become "\lex".
    private static void Append(StringBuilder builder, string text)
    {
        if (text.Length == 0)
            return;

        if (builder.Length > 0 && char.IsLetter(text[0]) && EndsWithCommand(builder))
            builder.Append(' ');

        builder.Append(text);
    }

    private static bool EndsWithCommand(StringBuilder builder)
    {
        int i = builder.Length - 1;
        if (i < 0 || !char.IsLetter(builder[i]))
            return false;

        while (i >= 0 && char.IsLetter(builder[i]))
            i--;

        return i >= 0 && builder[i] == '\\';
    }
}
