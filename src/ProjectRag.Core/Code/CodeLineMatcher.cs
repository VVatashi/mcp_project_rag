using System.Text.RegularExpressions;

namespace ProjectRag.Core.Code;

/// <summary>
/// Finds the lines of a text that match a literal text or a regular expression. A line has at most one match.
/// </summary>
public sealed class CodeLineMatcher
{
    /// <summary>The most characters of a line that a match keeps.</summary>
    public const int MaxLineText = 400;

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);

    private readonly string _literal;
    private readonly StringComparison _comparison;
    private readonly Regex? _regex;

    private CodeLineMatcher(string literal, StringComparison comparison, Regex? regex)
    {
        _literal = literal;
        _comparison = comparison;
        _regex = regex;
    }

    /// <summary>
    /// Makes a matcher.
    /// </summary>
    /// <param name="pattern">The text or the regular expression of .NET.</param>
    /// <param name="isRegex">Tells that the pattern is a regular expression.</param>
    /// <param name="caseSensitive">Tells that the case of letters matters.</param>
    /// <returns>The matcher.</returns>
    /// <exception cref="CodeSearchException">The pattern is empty, has a line end or a NUL character, or is not a valid regular expression.</exception>
    public static CodeLineMatcher Create(string pattern, bool isRegex, bool caseSensitive)
    {
        if (string.IsNullOrEmpty(pattern))
            throw new CodeSearchException(nameof(CodeGrepRequest.Pattern), "The pattern must not be empty.");

        if (pattern.AsSpan().ContainsAny('\r', '\n'))
            throw new CodeSearchException(nameof(CodeGrepRequest.Pattern), "The pattern must be one line.");

        if (pattern.Contains('\0', StringComparison.Ordinal))
            throw new CodeSearchException(nameof(CodeGrepRequest.Pattern), "The pattern must not hold a NUL character.");

        if (!isRegex)
            return new CodeLineMatcher(pattern, caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase, null);

        RegexOptions options = RegexOptions.CultureInvariant | (caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase);
        try
        {
            return new CodeLineMatcher(pattern, StringComparison.Ordinal, new Regex(pattern, options, RegexTimeout));
        }
        catch (ArgumentException exception)
        {
            throw new CodeSearchException(nameof(CodeGrepRequest.Pattern), $"The regular expression is not valid: {exception.Message}");
        }
    }

    /// <summary>
    /// Finds the matching lines of a text.
    /// </summary>
    /// <param name="content">The text with the line ends "\n". A final line end does not start another line.</param>
    /// <returns>The matches in the order of the lines.</returns>
    /// <exception cref="CodeSearchException">The regular expression ran longer than 2 seconds on a line.</exception>
    public IEnumerable<LineMatch> Find(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        int line = 0;
        for (int start = 0; start < content.Length;)
        {
            int end = content.IndexOf('\n', start);
            if (end < 0)
                end = content.Length;

            line++;
            if (TryMatch(content, start, end - start, out int index, out int length))
            {
                int textStart = TextStart(end - start, index, length);
                yield return new LineMatch(line, index + 1, length, content.Substring(start + textStart, Math.Min(end - start - textStart, MaxLineText)), textStart);
            }

            start = end + 1;
        }
    }

    /// <summary>
    /// Cuts a line to at most <see cref="MaxLineText"/> characters around a match.
    /// </summary>
    /// <param name="line">The line.</param>
    /// <param name="index">The index of the match in the line, from 0.</param>
    /// <param name="length">The length of the match.</param>
    /// <returns>The part of the line and its index in the line.</returns>
    public static (string Text, int Start) Window(string line, int index, int length)
    {
        ArgumentNullException.ThrowIfNull(line);
        int start = TextStart(line.Length, Math.Clamp(index, 0, line.Length), length);
        return (line.Substring(start, Math.Min(line.Length - start, MaxLineText)), start);
    }

    // A match beyond the first characters of a long line keeps some context before it.
    private static int TextStart(int lineLength, int index, int length)
    {
        if (lineLength <= MaxLineText || index + length <= MaxLineText)
            return 0;

        return Math.Clamp(index - (MaxLineText / 4), 0, lineLength - MaxLineText);
    }

    private bool TryMatch(string content, int start, int count, out int index, out int length)
    {
        ReadOnlySpan<char> line = content.AsSpan(start, count);
        if (_regex is null)
        {
            index = line.IndexOf(_literal, _comparison);
            length = _literal.Length;
            return index >= 0;
        }

        try
        {
            foreach (ValueMatch match in _regex.EnumerateMatches(line))
            {
                index = match.Index;
                length = match.Length;
                return true;
            }
        }
        catch (RegexMatchTimeoutException)
        {
            throw new CodeSearchException(nameof(CodeGrepRequest.Pattern), "The regular expression ran longer than 2 seconds on one line.");
        }

        index = 0;
        length = 0;
        return false;
    }
}
