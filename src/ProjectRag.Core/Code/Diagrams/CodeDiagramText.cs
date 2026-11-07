using System.Text;
using System.Text.RegularExpressions;

namespace ProjectRag.Core.Code.Diagrams;

/// <summary>
/// Writes the labels and the member lines of the diagrams from the stored signatures.
/// </summary>
public static partial class CodeDiagramText
{
    private static readonly HashSet<string> LeadingKeywords = new(StringComparer.Ordinal)
    {
        "public", "private", "protected", "internal",
        "static", "abstract", "virtual", "override", "sealed", "readonly", "async", "partial", "extern", "new", "const", "required", "unsafe", "volatile",
    };

    /// <summary>
    /// Writes the label of a type with its type parameters.
    /// </summary>
    /// <param name="name">The simple name, without type parameters.</param>
    /// <param name="signature">The stored signature, such as <c>public sealed class Registry&lt;T&gt; where T : class</c>.</param>
    /// <returns>The name with the type parameters, such as <c>Registry&lt;T&gt;</c>. The variance marks go away.</returns>
    public static string TypeLabel(string name, string signature)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (string.IsNullOrEmpty(signature))
            return name;

        Match match = Regex.Match(signature, @"(?<![\w@.])" + Regex.Escape(name) + @"\s*<", RegexOptions.None, TimeSpan.FromSeconds(1));
        if (!match.Success)
            return name;

        int open = match.Index + match.Length - 1;
        int depth = 0;
        for (int i = open; i < signature.Length; i++)
        {
            if (signature[i] == '<')
            {
                depth++;
            }
            else if (signature[i] == '>' && --depth == 0)
            {
                string[] parameters = [.. SplitTopLevel(signature[(open + 1)..i]).Select(StripVariance).Where(static parameter => parameter.Length > 0)];
                return parameters.Length == 0 ? name : name + "<" + string.Join(", ", parameters) + ">";
            }
        }

        return name;
    }

    /// <summary>
    /// Writes the label of an external type from its full name.
    /// </summary>
    /// <param name="target">The full name, such as <c>System.Collections.Generic.List&lt;T&gt;</c>.</param>
    /// <returns>The name without the namespace, such as <c>List&lt;T&gt;</c>.</returns>
    public static string ExternalLabel(string target)
    {
        ArgumentNullException.ThrowIfNull(target);
        int generic = target.IndexOf('<', StringComparison.Ordinal);
        string head = generic >= 0 ? target[..generic] : target;
        int dot = head.LastIndexOf('.');
        return dot < 0 ? target : target[(dot + 1)..];
    }

    /// <summary>
    /// Writes the text of a member line: the signature without the leading accessibility and modifiers, and without the accessors of a property.
    /// </summary>
    /// <param name="kind">The kind of the member.</param>
    /// <param name="signature">The stored signature.</param>
    /// <returns>The text, such as <c>void Update(double time)</c> or <c>int Count</c>.</returns>
    public static string MemberText(string kind, string signature)
    {
        ArgumentNullException.ThrowIfNull(signature);
        string text = Whitespace().Replace(signature, " ").Trim();
        while (true)
        {
            int space = text.IndexOf(' ', StringComparison.Ordinal);
            if (space <= 0 || !LeadingKeywords.Contains(text[..space]))
                break;

            text = text[(space + 1)..];
        }

        if (kind is Graph.CodeSymbolKinds.Property or Graph.CodeSymbolKinds.Indexer or Graph.CodeSymbolKinds.Event)
            text = Accessors().Replace(text, string.Empty);

        return text;
    }

    /// <summary>
    /// Maps an accessibility to the mark of UML.
    /// </summary>
    /// <param name="accessibility">One of public, internal, protected, protected internal, private protected or private.</param>
    /// <returns><c>+</c>, <c>~</c>, <c>#</c> or <c>-</c>.</returns>
    public static string Visibility(string accessibility) => accessibility switch
    {
        "public" => "+",
        "internal" => "~",
        "protected" or "protected internal" or "private protected" => "#",
        _ => "-",
    };

    private static IEnumerable<string> SplitTopLevel(string text)
    {
        StringBuilder current = new();
        int depth = 0;
        foreach (char c in text)
        {
            if (c is '<' or '(' or '[')
            {
                depth++;
            }
            else if (c is '>' or ')' or ']')
            {
                depth--;
            }
            else if (c == ',' && depth == 0)
            {
                yield return current.ToString().Trim();
                current.Clear();
                continue;
            }

            current.Append(c);
        }

        yield return current.ToString().Trim();
    }

    private static string StripVariance(string parameter)
    {
        if (parameter.StartsWith("in ", StringComparison.Ordinal))
            return parameter[3..].Trim();

        return parameter.StartsWith("out ", StringComparison.Ordinal) ? parameter[4..].Trim() : parameter;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"\s*\{[^{}]*\}\s*$")]
    private static partial Regex Accessors();
}
