using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using System.Xml.XPath;

namespace ProjectRag.Core.Documents.DocBook;

/// <summary>
/// Reads a file of DocBook 5, such as a reference page of OpenGL, into Markdown.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>A reference page starts with its name and its purpose. The synopsis of a function becomes a block of C or GLSL.</item>
/// <item>The sections become headings. A list of terms, such as the parameters, becomes paragraphs that start with the term.</item>
/// <item>A table becomes a pipe table, and a formula of MathML becomes LaTeX.</item>
/// <item>A reference to another page becomes a link to its file. The importer leaves out the section with the copyright, since every page repeats it.</item>
/// <item>The importer reads the entities of the DTD and the parts of XInclude from the folder of the file and its subfolders.
/// A pointer of XInclude may hold a simple path of XPath: <c>xpointer(/*/*[@role='45']/*)</c>.</item>
/// </list>
/// </remarks>
internal static partial class DocBookMarkdownImporter
{
    private const int MaxIncludeDepth = 8;

    // The most parts of XInclude that one document reads. A page of OpenGL reads about twenty, and a loop of parts stops here.
    private const int MaxIncludes = 500;
    private static readonly XNamespace DocBook = "http://docbook.org/ns/docbook";
    private static readonly XNamespace MathMl = "http://www.w3.org/1998/Math/MathML";
    private static readonly XNamespace XInclude = "http://www.w3.org/2001/XInclude";
    private static readonly XNamespace XLink = "http://www.w3.org/1999/xlink";

    // The base of the addresses of the files. The resolver maps an address under it back to a path relative to the document.
    private static readonly Uri Base = new("file:///docbook/");

    /// <summary>
    /// Checks whether the bytes hold a document of DocBook 5.
    /// </summary>
    public static bool IsDocBook(ReadOnlySpan<byte> bytes)
    {
        int length = Math.Min(bytes.Length, 4096);
        return Encoding.UTF8.GetString(bytes[..length]).Contains("http://docbook.org/ns/docbook", StringComparison.Ordinal);
    }

    /// <summary>
    /// Reads a document of DocBook.
    /// </summary>
    /// <param name="bytes">The bytes of the file.</param>
    /// <param name="name">The name of the file, the title when the document has none.</param>
    /// <param name="readFile">Reads another file by its path relative to the document, or gives <see langword="null"/>. It serves the entities and the parts of XInclude.</param>
    /// <returns>The text of the document in Markdown.</returns>
    public static DocumentText Read(byte[] bytes, string name, Func<string, byte[]?>? readFile)
    {
        XDocument document = Load(bytes, new Uri(Base, "document.xml"), readFile);
        if (document.Root is null)
            return new DocumentText(name, string.Empty);

        Include(document.Root, new Uri(Base, "document.xml"), new Includes(readFile), ["document.xml"]);
        Writer writer = new();
        string title = writer.Document(document.Root) ?? name;
        return new DocumentText(title, writer.Text());
    }

    private static XDocument Load(byte[] bytes, Uri address, Func<string, byte[]?>? readFile)
    {
        XmlReaderSettings settings = new()
        {
            DtdProcessing = DtdProcessing.Parse,
            XmlResolver = readFile is null ? null : new FileResolver(readFile),
            // The entities of MathML are many but short. The limit stops a file whose entities grow without end.
            MaxCharactersFromEntities = 10_000_000,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
        };
        using MemoryStream stream = new(bytes);
        using XmlReader reader = XmlReader.Create(stream, settings, address.AbsoluteUri);
        return XDocument.Load(reader, LoadOptions.None);
    }

    // Puts the parts that XInclude names in place of their elements. A part may include parts of its own.
    // A part that includes itself, directly or through other parts, gives nothing. A broken part also gives nothing.
    private static void Include(XElement root, Uri address, Includes includes, List<string> chain)
    {
        foreach (XElement include in root.Descendants(XInclude + "include").ToList())
        {
            List<XNode> parts = [];
            string href = include.Attribute("href")?.Value ?? string.Empty;
            if (includes.ReadFile is not null && chain.Count <= MaxIncludeDepth && includes.Count < MaxIncludes
                && Uri.TryCreate(address, href, out Uri? partAddress) && Relative(partAddress) is { } path && !chain.Contains(path))
            {
                includes.Count++;
                try
                {
                    if (includes.Part(path, partAddress) is { Root: not null } part)
                    {
                        XDocument copy = new(part);
                        Include(copy.Root!, partAddress, includes, [.. chain, path]);
                        parts.AddRange(Select(copy, include.Attribute("xpointer")?.Value));
                    }
                }
                catch (Exception exception) when (exception is XmlException or XPathException or InvalidOperationException)
                {
                    parts.Clear();
                }
            }

            include.ReplaceWith(parts);
        }
    }

    // The scheme xpointer(…) holds a simple path of XPath: steps of names or stars, each with one test of an attribute.
    // Without a pointer, the part is the whole root. Any other pointer gives nothing.
    private static IEnumerable<XNode> Select(XDocument part, string? pointer)
    {
        Match match = XPointerRegex().Match(pointer ?? string.Empty);
        if (!match.Success)
            return pointer is null or "" ? [new XElement(part.Root!)] : [];

        return part.XPathSelectElements(match.Groups["path"].Value).Select(static element => new XElement(element));
    }

    // The path of an address relative to the folder of the document. An absolute path, a drive, a share or a step up gives nothing.
    private static string? Relative(Uri address)
    {
        if (!Base.IsBaseOf(address))
            return null;

        string path = Uri.UnescapeDataString(Base.MakeRelativeUri(address).ToString());
        if (path.Length == 0 || Path.IsPathRooted(path) || path.Contains(':', StringComparison.Ordinal) || path[0] is '/' or '\\'
            || path.Split('/', '\\').Any(static step => step == ".."))
            return null;

        return path;
    }

    // The parts of XInclude that one document reads. A file that several elements include is read once.
    private sealed class Includes(Func<string, byte[]?>? readFile)
    {
        private readonly Dictionary<string, XDocument?> _parts = [];

        public Func<string, byte[]?>? ReadFile { get; } = readFile;

        public int Count { get; set; }

        public XDocument? Part(string path, Uri address)
        {
            if (!_parts.TryGetValue(path, out XDocument? part))
            {
                part = ReadFile?.Invoke(path) is { } bytes ? Load(bytes, address, ReadFile) : null;
                _parts[path] = part;
            }

            return part;
        }
    }

    // Serves the files of the same source to the parser. Any other address gives nothing.
    private sealed class FileResolver(Func<string, byte[]?> readFile) : XmlResolver
    {
        public override object? GetEntity(Uri absoluteUri, string? role, Type? ofObjectToReturn)
        {
            byte[]? bytes = Relative(absoluteUri) is { } path ? readFile(path) : null;
            return bytes is null ? throw new XmlException($"The file {absoluteUri} is outside the source or missing.") : new MemoryStream(bytes);
        }
    }

    // Writes the blocks of Markdown. A list item indents its blocks under its marker.
    private sealed class Writer
    {
        private readonly List<string> _blocks = [];
        private bool _inTable;

        public string Text() => string.Join("\n\n", _blocks) + "\n";

        // Writes a reference page or any other root, and gives its title.
        public string? Document(XElement root)
        {
            if (root.Name != DocBook + "refentry")
            {
                Blocks(root, 1);
                return Child(root, "info") is { } info ? Child(info, "title")?.Value.Trim() : Child(root, "title")?.Value.Trim();
            }

            XElement? nameDiv = Child(root, "refnamediv");
            string? title = Child(Child(root, "refmeta"), "refentrytitle")?.Value.Trim()
                ?? Child(nameDiv, "refname")?.Value.Trim();
            if (title is not null)
                Add($"# {MarkdownText.EscapeHeading(title)}");

            if (nameDiv is not null)
            {
                string names = string.Join(", ", nameDiv.Elements(DocBook + "refname").Select(static name => Collapse(name.Value)));
                string purpose = Collapse(Child(nameDiv, "refpurpose")?.Value ?? string.Empty);
                Add(MarkdownText.EscapeStart(MarkdownText.Escape(purpose.Length > 0 ? $"{names} — {purpose}" : names)));
            }

            foreach (XElement section in root.Elements().Where(static element => element.Name.LocalName is "refsynopsisdiv" or "refsect1" or "refsection" or "section"))
                Section(section, 2);

            return title;
        }

        private void Section(XElement section, int level)
        {
            string title = Collapse(Child(section, "title")?.Value ?? string.Empty);
            if (title == "Copyright")
                return;

            if (title.Length > 0)
                Add($"{new string('#', System.Math.Min(level, 6))} {MarkdownText.EscapeHeading(title)}");

            Blocks(section, level + 1);
        }

        private void Blocks(XElement parent, int level)
        {
            foreach (XElement element in parent.Elements())
                Block(element, level);
        }

        private void Block(XElement element, int level)
        {
            if (element.Name.Namespace == MathMl)
            {
                Add($"$$\n{MathMlReader.Read(element)}\n$$");
                return;
            }

            switch (element.Name.LocalName)
            {
                case "title" or "info" or "refmeta" or "titleabbrev" or "indexterm" or "remark":
                    break;
                case "refsect1" or "refsect2" or "refsect3" or "refsection" or "section" or "simplesect" or "refsynopsisdiv":
                    Section(element, level);
                    break;
                case "para" or "simpara" or "formalpara":
                    Paragraph(element, level);
                    break;
                case "funcsynopsis":
                    Synopsis(element);
                    break;
                case "fieldsynopsis":
                    Add($"```glsl\n{Collapse(string.Join(' ', element.Elements().Select(static part => part.Value)))};\n```");
                    break;
                case "programlisting" or "screen" or "literallayout" or "synopsis":
                    Code(element);
                    break;
                case "variablelist" or "glosslist":
                    Terms(element, level);
                    break;
                case "itemizedlist" or "orderedlist":
                    List(element, element.Name.LocalName == "orderedlist", level);
                    break;
                case "informaltable" or "table":
                    Table(element);
                    break;
                case "informalequation" or "equation":
                    foreach (XElement math in element.Descendants(MathMl + "math"))
                        Add(_inTable ? $"${CellMath(MathMlReader.Read(math))}$" : $"$$\n{MathMlReader.Read(math)}\n$$");

                    break;
                case "blockquote":
                    Quote(element, level);
                    break;
                case "note" or "warning" or "important" or "tip" or "caution" or "sidebar" or "example" or "informalexample" or "figure":
                    if (Child(element, "title") is { } caption)
                        Add(MarkdownText.EscapeStart(MarkdownText.Escape(Collapse(caption.Value))));

                    Blocks(element, level);
                    break;
                default:
                    // An unknown element with text is a paragraph. An unknown element with blocks writes its blocks.
                    if (element.Elements().Any(static child => IsBlock(child)))
                        Blocks(element, level);
                    else
                        Paragraph(element, level);

                    break;
            }
        }

        // A paragraph of DocBook may hold a list or a table inside its text. The text before and after it becomes paragraphs of their own.
        private void Paragraph(XElement element, int level)
        {
            StringBuilder line = new();
            foreach (XNode node in element.Nodes())
            {
                if (node is XElement child && IsBlock(child))
                {
                    Flush(line);
                    Block(child, level);
                }
                else
                {
                    line.Append(Inline(node));
                }
            }

            Flush(line);
        }

        private void Flush(StringBuilder line)
        {
            string text = Collapse(line.ToString());
            line.Clear();
            if (text.Length > 0)
                Add(MarkdownText.EscapeStart(text));
        }

        // The synopsis of a function: one line for each prototype. A page of GLSL declares its functions under "Declaration".
        private void Synopsis(XElement synopsis)
        {
            string language = synopsis.Parent?.Element(DocBook + "title")?.Value.Contains("C Specification", StringComparison.Ordinal) == true ? "c" : "glsl";
            List<string> lines = [];
            foreach (XElement prototype in synopsis.Elements(DocBook + "funcprototype"))
            {
                string head = Collapse(Child(prototype, "funcdef")?.Value ?? string.Empty);
                IEnumerable<string> parameters = prototype.Elements()
                    .Where(static part => part.Name.LocalName is "paramdef" or "void" or "varargs")
                    .Select(static part => part.Name.LocalName switch
                    {
                        "void" => "void",
                        "varargs" => "...",
                        _ => Collapse(part.Value),
                    });
                lines.Add($"{head}({string.Join(", ", parameters)});");
            }

            if (lines.Count > 0)
                Add($"```{language}\n{string.Join('\n', lines)}\n```");
        }

        private void Code(XElement element)
        {
            string text = element.Value.Replace("\r\n", "\n", StringComparison.Ordinal).Trim('\n').TrimEnd();
            if (text.Length == 0)
                return;

            string fence = text.Contains("```", StringComparison.Ordinal) ? "````" : "```";
            string language = element.Attribute("language")?.Value ?? string.Empty;
            Add($"{fence}{language}\n{text}\n{fence}");
        }

        // A term and its definition: "target: Specifies the target texture." The other blocks of the definition follow.
        private void Terms(XElement list, int level)
        {
            if (Child(list, "title") is { } caption)
                Add(MarkdownText.EscapeStart(MarkdownText.Escape(Collapse(caption.Value))));

            foreach (XElement entry in list.Elements().Where(static entry => entry.Name.LocalName is "varlistentry" or "glossentry"))
            {
                string term = string.Join(", ", entry.Elements().Where(static part => part.Name.LocalName is "term" or "glossterm")
                    .Select(part => Collapse(string.Concat(part.Nodes().Select(Inline)))));
                XElement? definition = entry.Elements().FirstOrDefault(static part => part.Name.LocalName is "listitem" or "glossdef");
                List<XElement> blocks = definition is null ? [] : [.. definition.Elements()];
                if (blocks.Count > 0 && blocks[0].Name.LocalName is "para" or "simpara" && !blocks[0].Elements().Any(static child => IsBlock(child)))
                {
                    Add(MarkdownText.EscapeStart($"{term}: {Collapse(string.Concat(blocks[0].Nodes().Select(Inline)))}"));
                    blocks.RemoveAt(0);
                }
                else if (term.Length > 0)
                {
                    Add(MarkdownText.EscapeStart(term));
                }

                foreach (XElement block in blocks)
                    Block(block, level);
            }
        }

        private void List(XElement list, bool ordered, int level)
        {
            int number = 1;
            foreach (XElement item in list.Elements(DocBook + "listitem"))
            {
                Writer inner = new() { _inTable = _inTable };
                inner.Blocks(item, level);
                string marker = ordered ? $"{number++}. " : "- ";
                string indent = new(' ', marker.Length);
                if (inner._blocks.Count == 0)
                    Add(marker.TrimEnd());

                for (int i = 0; i < inner._blocks.Count; i++)
                {
                    string block = inner._blocks[i].Replace("\n", "\n" + indent, StringComparison.Ordinal);
                    Add(i == 0 ? marker + block : indent + block);
                }
            }
        }

        private void Quote(XElement element, int level)
        {
            Writer inner = new();
            inner.Blocks(element, level);
            foreach (string block in inner._blocks)
                Add("> " + block.Replace("\n", "\n> ", StringComparison.Ordinal));
        }

        // A pipe table. The last row of the head is its head. A cell over several columns adds empty cells, and a cell over several rows leaves empty cells below.
        private void Table(XElement table)
        {
            if (Child(table, "title") is { } caption)
                Add(MarkdownText.EscapeStart(MarkdownText.Escape(Collapse(caption.Value))));

            foreach (XElement group in table.Elements(DocBook + "tgroup"))
            {
                List<string> columns = [.. group.Elements(DocBook + "colspec").Select(static (spec, index) => spec.Attribute("colname")?.Value ?? $"#{index}")];
                Dictionary<string, (string Start, string End)> spans = [];
                foreach (XElement span in group.Elements(DocBook + "spanspec"))
                    spans.TryAdd(span.Attribute("spanname")?.Value ?? string.Empty, (span.Attribute("namest")?.Value ?? string.Empty, span.Attribute("nameend")?.Value ?? string.Empty));

                List<XElement> head = [.. Child(group, "thead")?.Elements(DocBook + "row") ?? []];
                List<XElement> body = [.. group.Elements(DocBook + "tbody").SelectMany(static part => part.Elements(DocBook + "row"))];
                List<List<string>> rows = Rows([.. head.TakeLast(1), .. body], columns, spans);
                if (rows.Count == 0)
                    continue;

                int width = System.Math.Max(rows.Max(static row => row.Count), int.TryParse(group.Attribute("cols")?.Value, out int cols) ? cols : 0);
                StringBuilder builder = new();
                for (int i = 0; i < rows.Count; i++)
                {
                    List<string> row = rows[i];
                    row.AddRange(Enumerable.Repeat(string.Empty, width - row.Count));
                    builder.Append("| ").AppendJoin(" | ", row).Append(" |\n");
                    if (i == 0)
                        builder.Append('|').Append(string.Concat(Enumerable.Repeat(" --- |", width))).Append('\n');
                }

                Add(builder.ToString().TrimEnd('\n'));
            }
        }

        private List<List<string>> Rows(List<XElement> rows, List<string> columns, Dictionary<string, (string Start, string End)> spans)
        {
            List<List<string>> result = [];
            // The cells over several rows: the column and the rows still to cover.
            Dictionary<int, int> below = [];
            bool inTable = _inTable;
            _inTable = true;
            foreach (XElement row in rows)
            {
                List<string> cells = [];
                foreach (XElement entry in row.Elements(DocBook + "entry"))
                {
                    (int start, int span) = Place(entry, columns, spans);
                    while ((below.TryGetValue(cells.Count, out int left) && left > 0) || cells.Count < start)
                    {
                        if (left > 0)
                            below[cells.Count] = left - 1;

                        cells.Add(string.Empty);
                    }

                    if (int.TryParse(entry.Attribute("morerows")?.Value, out int more) && more > 0)
                    {
                        for (int column = cells.Count; column < cells.Count + span; column++)
                            below[column] = more;
                    }

                    cells.Add(Cell(entry));
                    cells.AddRange(Enumerable.Repeat(string.Empty, span - 1));
                }

                while (below.TryGetValue(cells.Count, out int rest) && rest > 0)
                {
                    below[cells.Count] = rest - 1;
                    cells.Add(string.Empty);
                }

                result.Add(cells);
            }

            _inTable = inTable;
            return result;
        }

        // The column of a cell, when it names one, and the count of its columns.
        private static (int Start, int Span) Place(XElement entry, List<string> columns, Dictionary<string, (string Start, string End)> spans)
        {
            (string start, string end) = entry.Attribute("spanname")?.Value is { } name && spans.TryGetValue(name, out (string Start, string End) span)
                ? span
                : (entry.Attribute("namest")?.Value ?? entry.Attribute("colname")?.Value ?? string.Empty, entry.Attribute("nameend")?.Value ?? string.Empty);
            int first = columns.IndexOf(start);
            int last = columns.IndexOf(end);
            return (first, first >= 0 && last > first ? last - first + 1 : 1);
        }

        // A cell joins its blocks into one line.
        private static string Cell(XElement entry)
        {
            Writer inner = new() { _inTable = true };
            if (entry.Elements().Any(static child => IsBlock(child)))
                inner.Blocks(entry, 6);
            else
                inner.Paragraph(entry, 6);

            return LonePipeRegex().Replace(Collapse(string.Join(' ', inner._blocks).Replace('\n', ' ')), @"\|");
        }

        private string Inline(XNode node)
        {
            switch (node)
            {
                case XText text:
                    return Text(WhitespaceRegex().Replace(text.Value, " "));
                case XElement element when element.Name.Namespace == MathMl:
                    return Formula(element);
                case XElement element:
                    return Inline(element);
                default:
                    return string.Empty;
            }
        }

        private string Inline(XElement element)
        {
            string Children() => string.Concat(element.Nodes().Select(Inline));
            switch (element.Name.LocalName)
            {
                case "parameter" or "constant" or "function" or "code" or "varname" or "type" or "literal" or "filename" or "command"
                    or "structname" or "structfield" or "symbol" or "returnvalue" or "envar" or "option" or "classname" or "methodname":
                    string span = MarkdownText.CodeSpan(Collapse(element.Value));
                    return _inTable ? span.Replace("|", "\\|", StringComparison.Ordinal) : span;
                case "emphasis":
                    string emphasized = Collapse(Children());
                    if (emphasized.Length == 0)
                        return string.Empty;

                    return element.Attribute("role")?.Value is "bold" or "strong" ? $"**{emphasized}**" : $"*{emphasized}*";
                case "citerefentry":
                    string page = Collapse(Child(element, "refentrytitle")?.Value ?? element.Value);
                    return page.Length == 0 ? string.Empty : $"[{MarkdownText.Escape(page)}]({Uri.EscapeDataString(page)}.xml)";
                case "link" when element.Attribute(XLink + "href")?.Value is { Length: > 0 } href:
                    string label = Collapse(Children());
                    if (href.Any(static c => c is ' ' or '(' or ')' or '<' or '>'))
                        return label.Length > 0 ? $"{label} ({MarkdownText.Escape(href)})" : MarkdownText.Escape(href);

                    return $"[{(label.Length > 0 ? label : MarkdownText.Escape(href))}]({href})";
                case "xref" when element.Attribute("linkend")?.Value is { Length: > 0 } target:
                    return $"[{MarkdownText.Escape(target)}](#{Uri.EscapeDataString(target)})";
                case "superscript":
                    return Script(Children(), '^');
                case "subscript":
                    return Script(Children(), '_');
                case "inlineequation":
                    return string.Concat(element.Elements().Select(child => child.Name.Namespace == MathMl ? Formula(child) : Inline(child)));
                case "footnote":
                    return $" ({Collapse(string.Join(' ', element.Elements().Select(child => string.Concat(child.Nodes().Select(Inline)))))})";
                case "indexterm" or "remark":
                    return string.Empty;
                default:
                    return Children();
            }
        }

        // The pages of GLSL write LaTeX between dollars for MathJax: $x = e^y$ or $$y_d = {f \times y_c} \over w_c$$.
        // The formula keeps its LaTeX and stands inside the line. The rest of the text gets the escapes of Markdown.
        private string Text(string text)
        {
            StringBuilder builder = new(text.Length + 8);
            int last = 0;
            foreach (Match match in DollarMathRegex().Matches(text))
            {
                builder.Append(MarkdownText.Escape(text[last..match.Index]));
                string latex = match.Groups["math"].Value.Trim();
                builder.Append('$').Append(_inTable ? CellMath(latex) : latex).Append('$');
                last = match.Index + match.Length;
            }

            return builder.Append(MarkdownText.Escape(text[last..])).ToString();
        }

        private string Formula(XElement math)
        {
            string latex = MathMlReader.Read(math);
            if (latex.Length == 0)
                return string.Empty;

            return $"${(_inTable ? CellMath(latex) : latex)}$";
        }

        private static string Script(string text, char sign)
        {
            text = Collapse(text);
            if (text.Length == 0)
                return string.Empty;

            return text.All(char.IsLetterOrDigit) ? $"{sign}{text}" : $"{sign}{{{text}}}";
        }

        // A cell of a pipe table ends at a pipe, also inside a formula. The commands \vert and \Vert draw the same lines.
        private static string CellMath(string latex) =>
            latex.Replace(@"\|", @"\Vert ", StringComparison.Ordinal).Replace("|", @"\vert ", StringComparison.Ordinal);

        private void Add(string block)
        {
            if (block.Length > 0)
                _blocks.Add(block);
        }
    }

    private static bool IsBlock(XElement element) =>
        element.Name.LocalName is "itemizedlist" or "orderedlist" or "variablelist" or "glosslist" or "informaltable" or "table" or "programlisting"
            or "screen" or "literallayout" or "informalequation" or "equation" or "blockquote" or "note" or "warning" or "important" or "tip"
            or "caution" or "para" or "simpara" or "funcsynopsis" or "fieldsynopsis" or "example" or "informalexample" or "figure" or "sidebar";

    private static XElement? Child(XElement? parent, string name) => parent?.Element(DocBook + name);

    private static string Collapse(string text) => WhitespaceRegex().Replace(text, " ").Trim();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    // The rules of Pandoc. A pair of double dollars holds any formula. A single dollar has no space inside next to the formula and no digit right after it.
    // So "$5 and $6" stays text.
    [GeneratedRegex(@"(?<!\\)\$\$(?<math>[^$]*?\S[^$]*?)\$\$|(?<![\\$])\$(?=\S)(?<math>[^$]*?\S)\$(?![\d$])")]
    private static partial Regex DollarMathRegex();

    [GeneratedRegex(@"^\s*xpointer\((?<path>(?:/(?:\*|[\w.-]+)(?:\[@[\w:-]+='[^']*'\])?)+)\)\s*$")]
    private static partial Regex XPointerRegex();

    [GeneratedRegex(@"(?<!\\)\|")]
    private static partial Regex LonePipeRegex();
}
