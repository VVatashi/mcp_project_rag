using System.Text;
using System.Text.RegularExpressions;

namespace ProjectRag.Core.Code.Graph;

/// <summary>
/// Builds the pattern of the camel-hump search: <c>SSync</c> finds <c>SceneSynchronizer</c>.
/// </summary>
public static class CodeHumps
{
    private const string Gap = "[a-z0-9_]*";

    /// <summary>
    /// Splits a query before each uppercase letter and before each run of digits.
    /// </summary>
    /// <param name="query">The query.</param>
    /// <returns>The parts. A query of other characters than letters, digits and '_' gives no parts.</returns>
    public static IReadOnlyList<string> Split(string query)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Length == 0 || !query.All(static c => char.IsAsciiLetterOrDigit(c) || c == '_'))
            return [];

        List<string> parts = [];
        StringBuilder part = new();
        foreach (char c in query)
        {
            bool startsPart = part.Length > 0 && (char.IsAsciiLetterUpper(c) || (char.IsAsciiDigit(c) && !char.IsAsciiDigit(part[^1])));
            if (startsPart)
            {
                parts.Add(part.ToString());
                part.Clear();
            }

            part.Append(c);
        }

        parts.Add(part.ToString());
        return parts;
    }

    /// <summary>
    /// Builds the regular expression of a query: the name starts with the first part, and each next part starts a word of the name.
    /// </summary>
    /// <param name="query">The query, such as <c>SSync</c>.</param>
    /// <returns>The case-sensitive expression, such as <c>^S[a-z0-9_]*Sync</c>, or <see langword="null"/> when the query has fewer than two parts.</returns>
    public static string? BuildPattern(string query)
    {
        IReadOnlyList<string> parts = Split(query);
        return parts.Count < 2 ? null : "^" + string.Join(Gap, parts.Select(static part => Regex.Escape(part)));
    }
}
