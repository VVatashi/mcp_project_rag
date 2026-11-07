using System.Text;

namespace ProjectRag.Core.Code;

/// <summary>
/// Reads the bytes of a file of a code source as exact text.
/// </summary>
public static class CodeText
{
    /// <summary>The number of leading bytes that the check for a binary file reads.</summary>
    public const int BinaryProbeBytes = 8192;

    /// <summary>
    /// Checks whether the start of a file shows a binary file: a NUL byte in the first 8 KB. A file with a UTF-16 mark holds NUL bytes as text.
    /// </summary>
    /// <param name="head">The first bytes of the file.</param>
    /// <returns><see langword="true"/> for a binary file.</returns>
    public static bool IsBinary(ReadOnlySpan<byte> head)
    {
        if (head.StartsWith(Utf16LittleEndianMark) || head.StartsWith(Utf16BigEndianMark))
            return false;

        return head[..Math.Min(head.Length, BinaryProbeBytes)].Contains((byte)0);
    }

    /// <summary>
    /// Decodes the bytes of a file: UTF-8, UTF-16 LE or UTF-16 BE by the byte order mark, else UTF-8. The mark does not reach the text.
    /// The text has the line ends "\n".
    /// </summary>
    /// <param name="bytes">The bytes of the file.</param>
    /// <returns>The text.</returns>
    public static string Decode(ReadOnlySpan<byte> bytes)
    {
        string text;
        if (bytes.StartsWith(Utf8Mark))
            text = Encoding.UTF8.GetString(bytes[Utf8Mark.Length..]);
        else if (bytes.StartsWith(Utf16LittleEndianMark))
            text = Encoding.Unicode.GetString(bytes[Utf16LittleEndianMark.Length..]);
        else if (bytes.StartsWith(Utf16BigEndianMark))
            text = Encoding.BigEndianUnicode.GetString(bytes[Utf16BigEndianMark.Length..]);
        else
            text = Encoding.UTF8.GetString(bytes);

        return NormalizeLineEnds(text);
    }

    /// <summary>
    /// Replaces "\r\n" and a lone "\r" with "\n".
    /// </summary>
    /// <param name="text">The text.</param>
    /// <returns>The text with the line ends "\n".</returns>
    public static string NormalizeLineEnds(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Contains('\r', StringComparison.Ordinal)
            ? text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n')
            : text;
    }

    /// <summary>
    /// Splits a text into lines. A final line end does not start another line, and an empty text has no lines.
    /// </summary>
    /// <param name="content">The text with the line ends "\n".</param>
    /// <returns>The lines.</returns>
    public static IReadOnlyList<string> SplitLines(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (content.Length == 0)
            return [];

        string[] lines = content.Split('\n');
        return content[^1] == '\n' ? lines[..^1] : lines;
    }

    private static ReadOnlySpan<byte> Utf8Mark => [0xEF, 0xBB, 0xBF];

    private static ReadOnlySpan<byte> Utf16LittleEndianMark => [0xFF, 0xFE];

    private static ReadOnlySpan<byte> Utf16BigEndianMark => [0xFE, 0xFF];
}
