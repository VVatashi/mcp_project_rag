namespace ProjectRag.Code.Chunks;

/// <summary>
/// Gives the offsets of the lines of a text. Lines count from 0.
/// </summary>
internal sealed class LineIndex
{
    private readonly int[] _starts;

    /// <summary>
    /// Finds the lines of a text.
    /// </summary>
    /// <param name="text">The text with the line ends "\n".</param>
    public LineIndex(string text)
    {
        Text = text;
        int count = 1;
        foreach (char character in text)
        {
            if (character == '\n')
                count++;
        }

        _starts = new int[count];
        int line = 1;
        for (int index = 0; index < text.Length; index++)
        {
            if (text[index] == '\n')
                _starts[line++] = index + 1;
        }
    }

    /// <summary>
    /// Gets the text.
    /// </summary>
    public string Text { get; }

    /// <summary>
    /// Gets the number of lines. A text that ends with a line end has a last empty line.
    /// </summary>
    public int Count => _starts.Length;

    /// <summary>
    /// Gets the offset of the first character of a line.
    /// </summary>
    /// <param name="line">The line.</param>
    /// <returns>The offset.</returns>
    public int Start(int line) => _starts[line];

    /// <summary>
    /// Gets the offset after the last character of a line, without its line end.
    /// </summary>
    /// <param name="line">The line.</param>
    /// <returns>The offset.</returns>
    public int End(int line) => line + 1 < _starts.Length ? _starts[line + 1] - 1 : Text.Length;

    /// <summary>
    /// Gets the number of characters from the start of the first line to the end of the last line.
    /// </summary>
    /// <param name="firstLine">The first line.</param>
    /// <param name="lastLine">The last line.</param>
    /// <returns>The length.</returns>
    public int Length(int firstLine, int lastLine) => End(lastLine) - Start(firstLine);

    /// <summary>
    /// Finds the line of an offset.
    /// </summary>
    /// <param name="position">The offset.</param>
    /// <returns>The line.</returns>
    public int LineOf(int position)
    {
        int index = Array.BinarySearch(_starts, position);
        return index >= 0 ? index : ~index - 1;
    }

    /// <summary>
    /// Checks whether a line has only white space.
    /// </summary>
    /// <param name="line">The line.</param>
    /// <returns><see langword="true"/> when the line is blank.</returns>
    public bool IsBlank(int line)
    {
        int end = End(line);
        for (int index = Start(line); index < end; index++)
        {
            if (!char.IsWhiteSpace(Text[index]))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Checks whether a line holds only a comment that starts with "//" or "/*".
    /// </summary>
    /// <param name="line">The line.</param>
    /// <returns><see langword="true"/> when the line is a comment.</returns>
    public bool IsComment(int line)
    {
        int end = End(line);
        int index = Start(line);
        while (index < end && char.IsWhiteSpace(Text[index]))
            index++;

        return index + 1 < end && Text[index] == '/' && (Text[index + 1] == '/' || Text[index + 1] == '*');
    }
}
