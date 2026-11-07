namespace ProjectRag.Code.Chunks;

/// <summary>
/// Cleans the text of a heading.
/// </summary>
internal static class HeadingText
{
    /// <summary>
    /// Replaces every run of white space with one space, trims the text and cuts it at a limit.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <param name="limit">The most characters of the result.</param>
    /// <returns>The clean text.</returns>
    public static string Collapse(ReadOnlySpan<char> text, int limit)
    {
        char[] buffer = new char[Math.Min(limit, text.Length)];
        int length = 0;
        bool space = false;
        foreach (char character in text)
        {
            if (char.IsWhiteSpace(character))
            {
                space = length > 0;
                continue;
            }

            if (space)
            {
                if (length == buffer.Length)
                    break;

                buffer[length++] = ' ';
                space = false;
            }

            if (length == buffer.Length)
                break;

            buffer[length++] = character;
        }

        return new string(buffer, 0, length);
    }
}
