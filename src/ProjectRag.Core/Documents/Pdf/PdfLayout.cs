namespace ProjectRag.Core.Documents.Pdf;

/// <summary>
/// A unit of a line: a glyph, or a formula part that the layout built from several glyphs, such as a fraction.
/// </summary>
internal sealed class Item
{
    /// <summary>Gets or sets the text: the glyph, or LaTeX when <see cref="IsLatex"/> is set.</summary>
    public required string Text { get; set; }

    /// <summary>Gets a value that tells whether <see cref="Text"/> holds LaTeX.</summary>
    public bool IsLatex { get; init; }

    /// <summary>Gets or sets the left edge.</summary>
    public double Left { get; set; }

    /// <summary>Gets or sets the right edge.</summary>
    public double Right { get; set; }

    /// <summary>Gets or sets the baseline.</summary>
    public double Baseline { get; set; }

    /// <summary>Gets or sets the top.</summary>
    public double Top { get; set; }

    /// <summary>Gets or sets the bottom.</summary>
    public double Bottom { get; set; }

    /// <summary>Gets the size of the font.</summary>
    public double Size { get; init; }

    /// <summary>Gets the style.</summary>
    public GlyphStyle Style { get; init; }

    /// <summary>Gets the name of the font.</summary>
    public string Font { get; init; } = string.Empty;

    /// <summary>Gets the subscript, or an empty list.</summary>
    public List<Item> Sub { get; } = [];

    /// <summary>Gets the superscript, or an empty list.</summary>
    public List<Item> Sup { get; } = [];

    /// <summary>Gets a value that tells whether the item belongs to a formula.</summary>
    public bool IsMath => IsLatex || (Style & (GlyphStyle.Math | GlyphStyle.MathExtension)) != 0;

    /// <summary>Gets a value that tells whether the item is a large delimiter over several lines.</summary>
    public bool IsTall => Top - Bottom > Size * 1.6;

    /// <summary>Gets a value that tells whether the layout may put the item into the nearest line, whatever its baseline.</summary>
    public bool IsFloating { get; init; }

    public static Item From(Glyph glyph) => new()
    {
        Text = glyph.Text,
        Left = glyph.Left,
        Right = glyph.Right,
        Baseline = glyph.Baseline,
        Top = glyph.Top,
        Bottom = glyph.Bottom,
        Size = glyph.Size,
        Style = glyph.Style,
        Font = glyph.Font,
    };
}

/// <summary>
/// The kind of a line.
/// </summary>
internal enum LineKind
{
    /// <summary>Text, with inline formulas and code.</summary>
    Text,

    /// <summary>A line of code.</summary>
    Code,

    /// <summary>A line of a display formula.</summary>
    Math,
}

/// <summary>
/// A line of a page: items on one baseline, with their subscripts and superscripts.
/// </summary>
internal sealed class Line
{
    public required int Page { get; init; }

    public required List<Item> Items { get; init; }

    public double Baseline { get; set; }

    public double Size { get; set; }

    public double Left => Items[0].Left;

    public double Right => Items.Max(static item => item.Right);

    public double Top => Items.Max(static item => item.Top);

    public double Bottom => Items.Min(static item => item.Bottom);

    public LineKind Kind { get; set; }

    /// <summary>Gets or sets the level of the heading of the line, or 0 for no heading.</summary>
    public int HeadingLevel { get; set; }

    /// <summary>Gets or sets the title of a heading line from the bookmark.</summary>
    public string? HeadingTitle { get; set; }

    /// <summary>Gets or sets a value that tells whether the line lies inside a table.</summary>
    public bool InTable { get; set; }

    /// <summary>Gets or sets a value that tells whether the line is a footnote.</summary>
    public bool IsFootnote { get; set; }

    /// <summary>Gets the plain text of the line for comparisons: the glyphs with spaces at the gaps.</summary>
    public string PlainText => PdfLayout.PlainText(Items);
}

/// <summary>
/// The geometry of a page: formula parts from several glyphs, lines with their scripts, and the kind of each line.
/// </summary>
/// <remarks>
/// <para>
/// TeX sets a fraction as a numerator above a thin rule and a denominator below it. The layout finds such a rule with glyphs of a formula
/// above and below it, and it replaces them with one item <c>\frac{…}{…}</c>. Short rules go first, so an inner fraction becomes a part of an outer one.
/// A large delimiter of TeX is a stack of CMEX pieces. The layout joins the stack into one tall item.
/// </para>
/// <para>
/// A line is a group of items with one baseline. A smaller item that sits a little higher or lower than the item on its left is a superscript or a subscript of that item.
/// TeX also puts a script of a formula at its own baseline, so a smaller line next to a line of normal size gives its items to that line as scripts.
/// </para>
/// </remarks>
internal static class PdfLayout
{
    /// <summary>
    /// Builds the lines of a page.
    /// </summary>
    /// <param name="page">The page.</param>
    /// <param name="mathFonts">The fonts that count as math. CMR sets the body of a document in Computer Modern, so it counts as math only in a document with another body font.</param>
    /// <returns>The lines from the top of the page down.</returns>
    public static List<Line> BuildLines(PdfPageModel page, Func<Glyph, bool> mathFonts)
    {
        List<Item> items = [.. page.Glyphs.Select(glyph => mathFonts(glyph) ? Item.From(glyph) : FromText(glyph))];
        JoinDelimiters(items);
        JoinFractions(items, page.Rules);
        List<Line> lines = [.. BuildRows(items).Select(row => new Line { Page = page.Number, Items = row.Items, Baseline = row.Baseline, Size = row.Size })];
        foreach (Line line in lines)
            line.Kind = Classify(line);

        return lines;
    }

    /// <summary>
    /// Groups items into rows and attaches the scripts. The layout also uses it for the numerator and the denominator of a fraction.
    /// </summary>
    /// <param name="items">The items.</param>
    /// <returns>The rows from the top down, each with its items from left to right.</returns>
    public static List<(List<Item> Items, double Baseline, double Size)> BuildRows(List<Item> items)
    {
        List<(List<Item> Items, double Baseline, double Size)> rows = [];
        foreach (Item item in items.Where(static item => !item.IsFloating).OrderByDescending(static item => item.Baseline).ThenBy(static item => item.Left))
        {
            int index = rows.FindIndex(row => Math.Abs(row.Baseline - item.Baseline) <= 0.6);
            if (index < 0)
                rows.Add(([item], item.Baseline, item.Size));
            else
                rows[index].Items.Add(item);
        }

        rows = [.. rows.Select(static row => (row.Items, row.Baseline, Size: Median(row.Items.Select(static item => item.Size))))];
        AttachScriptRows(rows);
        foreach (Item item in items.Where(static item => item.IsFloating))
        {
            int index = rows.Count == 0 ? -1 : rows.Select((row, i) => (Distance: Math.Abs(row.Baseline - item.Baseline), Index: i)).MinBy(static pair => pair.Distance).Index;
            // The outline of a large delimiter is rough, so it may sit farther from its line than a fraction.
            double reach = (item.Style & GlyphStyle.MathExtension) != 0 ? item.Size * 2 : item.Size;
            if (index < 0 || Math.Abs(rows[index].Baseline - item.Baseline) > reach)
                rows.Add(([item], item.Baseline, item.Size));
            else
                rows[index].Items.Add(item);
        }

        List<(List<Item> Items, double Baseline, double Size)> result = [];
        foreach ((List<Item> row, double baseline, double size) in rows.OrderByDescending(static row => row.Baseline))
            result.Add((AttachInlineScripts(row, baseline, size), baseline, size));

        return result;
    }

    /// <summary>
    /// Writes the plain text of items: the glyphs with a space at each wide gap, and the scripts after their bases.
    /// </summary>
    public static string PlainText(IEnumerable<Item> items)
    {
        System.Text.StringBuilder builder = new();
        Item? previous = null;
        foreach (Item item in items)
        {
            if (previous is not null && NeedsSpace(previous, item))
                builder.Append(' ');

            builder.Append(item.Text);
            foreach (Item script in item.Sub.Concat(item.Sup))
                builder.Append(script.Text);

            previous = item;
        }

        return builder.ToString();
    }

    /// <summary>
    /// Tells whether a space separates two neighbor items. A word space is at least a sixth of the size of the font.
    /// Monospace text keeps its spaces only through the gaps, so its threshold is half a character.
    /// </summary>
    public static bool NeedsSpace(Item left, Item right)
    {
        double gap = right.Left - left.Right;
        if ((left.Style & right.Style & GlyphStyle.Mono) != 0)
            return gap > MonoWidth(left) * 0.5;

        // A change between the text, code and a formula squeezes the space of a justified line, so a smaller gap counts.
        bool fontChange = ((left.Style ^ right.Style) & GlyphStyle.Mono) != 0 || (left.IsMath != right.IsMath && !left.Text.All(char.IsPunctuation) && !right.Text.All(char.IsPunctuation));
        return gap > Math.Max(left.Size, right.Size) * (fontChange ? 0.1 : 0.17);
    }

    /// <summary>Gets the width of a character of a monospace item.</summary>
    public static double MonoWidth(Item item) => Math.Max(0.3, (item.Right - item.Left) / Math.Max(1, item.Text.Length));

    private static Item FromText(Glyph glyph)
    {
        Item item = Item.From(glyph);
        return item.IsMath ? new Item
        {
            Text = glyph.Text,
            Left = glyph.Left,
            Right = glyph.Right,
            Baseline = glyph.Baseline,
            Top = glyph.Top,
            Bottom = glyph.Bottom,
            Size = glyph.Size,
            Style = glyph.Style & ~GlyphStyle.Math,
            Font = glyph.Font,
        } : item;
    }

    // CMEX pieces at one horizontal position that touch each other form one large delimiter.
    private static void JoinDelimiters(List<Item> items)
    {
        List<Item> pieces = [.. items.Where(static item => (item.Style & GlyphStyle.MathExtension) != 0).OrderBy(static item => item.Left).ThenByDescending(static item => item.Baseline)];
        HashSet<Item> used = [];
        // A single large delimiter belongs to the line at its middle, not to its own baseline.
        foreach (Item piece in pieces)
        {
            items.Remove(piece);
            items.Add(new Item
            {
                Text = piece.Text,
                Left = piece.Left,
                Right = piece.Right,
                Top = piece.Top,
                Bottom = piece.Bottom,
                Baseline = ((piece.Top + piece.Bottom) / 2) - (piece.Size * 0.25),
                Size = piece.Size,
                Style = GlyphStyle.MathExtension,
                Font = piece.Font,
                IsFloating = true,
            });
        }

        pieces = [.. items.Where(static item => (item.Style & GlyphStyle.MathExtension) != 0).OrderBy(static item => item.Left).ThenByDescending(static item => item.Baseline)];
        foreach (Item piece in pieces)
        {
            if (used.Contains(piece))
                continue;

            List<Item> stack = [piece];
            // A stack repeats one piece, such as the bar of a large |. A brace next to a bar is no part of it.
            foreach (Item other in pieces.Where(other => other != piece && !used.Contains(other) && other.Text == piece.Text && Math.Abs(other.Left - piece.Left) < piece.Size * 0.3).OrderByDescending(static other => other.Baseline))
            {
                if (stack[^1].Baseline - other.Baseline <= piece.Size * 1.1)
                    stack.Add(other);
            }

            if (stack.Count < 2)
                continue;

            foreach (Item member in stack)
                used.Add(member);

            double top = stack.Max(static member => member.Top);
            double bottom = stack.Min(static member => member.Bottom);
            items.RemoveAll(stack.Contains);
            items.Add(new Item
            {
                Text = stack.Select(static member => member.Text).GroupBy(static text => text).MaxBy(static group => group.Count())!.Key,
                Left = stack.Min(static member => member.Left),
                Right = stack.Max(static member => member.Right),
                Top = top,
                Bottom = bottom,
                Baseline = ((top + bottom) / 2) - (piece.Size * 0.25),
                Size = piece.Size,
                Style = GlyphStyle.MathExtension,
                Font = piece.Font,
                IsFloating = true,
            });
        }
    }

    // A short rule with formula items right above and right below it is the bar of a fraction.
    private static void JoinFractions(List<Item> items, IReadOnlyList<Rule> rules)
    {
        foreach (Rule rule in rules.Where(static rule => rule.Width < 200).OrderBy(static rule => rule.Width))
        {
            List<Item> above = [.. items.Where(item => Covers(rule, item) && item.Bottom >= rule.Y - 1 && item.Bottom <= rule.Y + (item.Size * 0.6))];
            List<Item> below = [.. items.Where(item => Covers(rule, item) && item.Top <= rule.Y + 1 && item.Top >= rule.Y - (item.Size * 0.6))];
            // The rule of a footnote has text above and below it. A fraction has parts of a formula, or digits as in 1/2.
            if (above.Count == 0 || below.Count == 0 || !IsFormulaPart(above) || !IsFormulaPart(below))
                continue;

            // A row of a table between two rules is no fraction: the rule of a fraction is not wider than its parts by much.
            double partsWidth = Math.Max(above.Max(static item => item.Right) - above.Min(static item => item.Left), below.Max(static item => item.Right) - below.Min(static item => item.Left));
            if (rule.Width > (partsWidth * 1.6) + 6)
                continue;

            string numerator = MathWriter.WriteRows(BuildRows(above));
            string denominator = MathWriter.WriteRows(BuildRows(below));
            double size = above.Concat(below).Max(static item => item.Size);
            items.RemoveAll(item => above.Contains(item) || below.Contains(item));
            items.Add(new Item
            {
                Text = $"\\frac{{{numerator}}}{{{denominator}}}",
                IsLatex = true,
                Left = rule.Left,
                Right = rule.Right,
                Top = above.Max(static item => item.Top),
                Bottom = below.Min(static item => item.Bottom),
                // TeX puts the bar on the axis of the formula, a quarter of the size above the baseline.
                Baseline = rule.Y - (size * 0.25),
                Size = size,
                Style = GlyphStyle.Math,
                IsFloating = true,
            });
        }
    }

    private static bool IsFormulaPart(List<Item> items) =>
        items.Count(static item => item.IsMath || item.Text.All(char.IsDigit)) >= items.Count * 0.6;

    private static bool Covers(Rule rule, Item item)
    {
        double center = (item.Left + item.Right) / 2;
        return center >= rule.Left - 1 && center <= rule.Right + 1;
    }

    // A row of smaller items next to a row of normal items is a row of scripts: TeX shifts the baseline of a script.
    private static void AttachScriptRows(List<(List<Item> Items, double Baseline, double Size)> rows)
    {
        for (int i = rows.Count - 1; i >= 0; i--)
        {
            (List<Item> scripts, double baseline, double size) = rows[i];
            int best = -1;
            for (int j = 0; j < rows.Count; j++)
            {
                if (j == i || rows[j].Size * 0.85 < size || Math.Abs(rows[j].Baseline - baseline) > rows[j].Size * 0.75 || !Overlaps(rows[j].Items, scripts))
                    continue;

                if (best < 0 || Math.Abs(rows[j].Baseline - baseline) < Math.Abs(rows[best].Baseline - baseline))
                    best = j;
            }

            if (best < 0)
                continue;

            rows[best].Items.AddRange(scripts);
            rows.RemoveAt(i);
        }
    }

    private static bool Overlaps(List<Item> line, List<Item> scripts)
    {
        double left = line.Min(static item => item.Left) - 2;
        double right = line.Max(static item => item.Right) + 2;
        // The mark of a footnote stands before the start of its line, so a script may start left of the line.
        return scripts.All(item => item.Right >= left && item.Left <= right + (item.Size * 2));
    }

    // In a row, a smaller item above or below the baseline belongs to the item on its left. A run of such items is one script.
    private static List<Item> AttachInlineScripts(List<Item> row, double baseline, double size)
    {
        List<Item> sorted = [.. row.OrderBy(static item => item.Left)];
        List<Item> result = [];
        foreach (Item item in sorted)
        {
            double shift = item.Baseline - baseline;
            bool isScript = !item.IsTall && item.Size <= size * 0.85 && Math.Abs(shift) >= Math.Min(0.8, size * 0.06) && result.Count > 0;
            if (!isScript)
            {
                result.Add(item);
                continue;
            }

            Item host = result[^1];
            // A script follows its base closely. The next script of the same kind continues the same run.
            if (item.Left - host.Right > size * 0.6 && host.Sub.Count == 0 && host.Sup.Count == 0)
            {
                result.Add(item);
                continue;
            }

            (shift > 0 ? host.Sup : host.Sub).Add(item);
        }

        return result;
    }

    private static LineKind Classify(Line line)
    {
        int total = 0;
        int mono = 0;
        int math = 0;
        foreach (Item item in line.Items)
        {
            int weight = item.IsLatex ? 3 : Math.Max(1, item.Text.Length);
            total += weight;
            if ((item.Style & GlyphStyle.Mono) != 0)
                mono += weight;

            if (item.IsMath)
                math += weight;
        }

        // A prototype of a function sets its parameters in italics, so a line of code is mostly, not fully, monospace.
        if (total > 0 && mono >= total * 0.7)
            return LineKind.Code;

        // A display formula starts with a formula item and is mostly formula. A sentence with an inline formula starts with a word.
        bool startsWithMath = line.Items[0].IsMath || (line.Items.Count > 1 && line.Items[0].Text.Length == 1 && line.Items[1].IsMath);
        bool hasTall = line.Items.Any(static item => item.IsTall || item.IsLatex);
        if (total > 0 && startsWithMath && (math >= total * 0.6 || (hasTall && math >= total * 0.35)))
            return LineKind.Math;

        return LineKind.Text;
    }

    private static double Median(IEnumerable<double> values)
    {
        List<double> sorted = [.. values.Order()];
        return sorted.Count == 0 ? 0 : sorted[sorted.Count / 2];
    }
}
