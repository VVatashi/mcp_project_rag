using System.Text;
using System.Text.RegularExpressions;

namespace ProjectRag.Core.Code.Chunks;

/// <summary>
/// Splits the identifiers of code into lowercase tokens for the token search.
/// </summary>
/// <remarks>
/// <para>
/// Every identifier gives its whole lowercase name without underscores, then its parts: <c>SceneSynchronizer</c> gives <c>scenesynchronizer scene synchronizer</c>,
/// <c>_softGPU</c> gives <c>softgpu soft gpu</c>, and <c>HTTPClient</c> gives <c>httpclient http client</c>.
/// A name of one part gives one token. A number is a token of its own.
/// </para>
/// <para>
/// The database reads the result with the configuration <c>simple</c>, which stems nothing. So the query and the chunks meet at the same tokens.
/// </para>
/// </remarks>
public static partial class IdentifierTokens
{
    private const int MaxTerms = 32;

    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "a", "an", "and", "are", "as", "at", "be", "by", "can", "do", "does", "for", "from", "how", "in", "is", "it", "of", "on", "or", "that", "the", "this", "to", "was", "what", "when", "where", "which", "who", "why", "with",
    };

    /// <summary>
    /// Splits a text into tokens, in the order of the text and with repeats.
    /// </summary>
    /// <param name="text">The text: code, a heading or a query.</param>
    /// <returns>The tokens joined with spaces, or an empty text.</returns>
    public static string Split(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        StringBuilder result = new(text.Length + text.Length / 2);
        foreach (Match match in TokenRegex().Matches(text))
        {
            foreach (string token in Expand(match.Value))
            {
                if (result.Length > 0)
                    result.Append(' ');

                result.Append(token);
            }
        }

        return result.ToString();
    }

    /// <summary>
    /// Finds the distinct tokens of a query for the token search.
    /// </summary>
    /// <remarks>
    /// A token of one character and a common English word are noise in a question, so they stay out.
    /// A query that holds nothing else keeps them.
    /// </remarks>
    /// <param name="query">The query.</param>
    /// <returns>The tokens in the order of the query, at most 32.</returns>
    public static IReadOnlyList<string> Terms(string query)
    {
        ArgumentNullException.ThrowIfNull(query);
        List<string> all = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (string token in Split(query).Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (seen.Add(token))
                all.Add(token);
        }

        List<string> useful = [.. all.Where(static token => token.Length > 1 && !StopWords.Contains(token))];
        return [.. (useful.Count > 0 ? useful : all).Take(MaxTerms)];
    }

    private static IEnumerable<string> Expand(string token)
    {
        if (char.IsDigit(token[0]))
        {
            yield return token;
            yield break;
        }

        List<string> parts = [];
        foreach (string segment in token.Split('_', StringSplitOptions.RemoveEmptyEntries))
            parts.AddRange(SplitCamel(segment));

        if (parts.Count == 0)
            yield break;

        yield return string.Concat(parts);
        if (parts.Count > 1)
        {
            foreach (string part in parts)
                yield return part;
        }
    }

    // A part ends before an uppercase letter that follows a lowercase letter or a digit, and before the last uppercase letter of a run that a lowercase letter follows.
    private static IEnumerable<string> SplitCamel(string segment)
    {
        int start = 0;
        for (int i = 1; i < segment.Length; i++)
        {
            char previous = segment[i - 1];
            char current = segment[i];
            bool boundary = char.IsUpper(current)
                && (!char.IsUpper(previous) || (i + 1 < segment.Length && char.IsLower(segment[i + 1])));
            if (!boundary)
                continue;

            yield return segment[start..i].ToLowerInvariant();
            start = i;
        }

        yield return segment[start..].ToLowerInvariant();
    }

    [GeneratedRegex(@"[\p{L}_][\p{L}\p{Nd}_]*|\p{Nd}+")]
    private static partial Regex TokenRegex();
}
