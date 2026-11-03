using System.Text;
using System.Text.RegularExpressions;

namespace ProjectRag.Core.Documents;

/// <summary>
/// Writes plain text as Markdown: the escapes of the characters with a meaning and the code spans. The importers of PDF and HTML share it.
/// </summary>
internal static partial class MarkdownText
{
    public static string CodeSpan(string text)
    {
        string trimmed = text.Trim();
        if (trimmed.Length == 0)
            return string.Empty;

        string fence = trimmed.Contains('`', StringComparison.Ordinal) ? "``" : "`";
        string pad = trimmed.StartsWith('`') || trimmed.EndsWith('`') ? " " : string.Empty;
        string lead = text.StartsWith(' ') ? " " : string.Empty;
        string tail = text.EndsWith(' ') ? " " : string.Empty;
        return $"{lead}{fence}{pad}{trimmed}{pad}{fence}{tail}";
    }

    // Plain text holds no Markdown, so the characters with a meaning in Markdown get a backslash.
    // An underscore inside a word has no meaning, so a name such as gl_Position keeps it without a backslash.
    public static string Escape(string text)
    {
        StringBuilder builder = new(text.Length + 8);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            bool inWord = i > 0 && i < text.Length - 1 && char.IsLetterOrDigit(text[i - 1]) && char.IsLetterOrDigit(text[i + 1]);
            if (c is '\\' or '`' or '*' or '[' or ']' or '<' or '>' or '$' or '|' || (c == '_' && !inWord))
                builder.Append('\\');

            builder.Append(c);
        }

        return builder.ToString();
    }

    // A "#" at the end of a heading would close it, so "C#" keeps its sign escaped.
    public static string EscapeHeading(string text)
    {
        string escaped = Escape(text).Trim();
        return escaped.EndsWith('#') ? $"{escaped[..^1]}\\#" : escaped;
    }

    // A paragraph that starts like a heading, a quote, a list or a rule would change its kind.
    // Markdown escapes only punctuation, so a number of a list keeps its digits and escapes its dot: "1\. Text".
    public static string EscapeStart(string text)
    {
        Match match = BlockStartRegex().Match(text);
        if (!match.Success)
            return text;

        int digits = match.Groups["digits"].Length;
        return digits > 0 ? $"{text[..digits]}\\{text[digits..]}" : $"\\{text}";
    }

    [GeneratedRegex(@"^(?:#|>|[-+=]\s|(?<digits>\d+)[.)]\s)")]
    private static partial Regex BlockStartRegex();
}
