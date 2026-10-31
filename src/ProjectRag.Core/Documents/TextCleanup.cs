using System.Text;

namespace ProjectRag.Core.Documents;

/// <summary>
/// Makes a text safe for PostgreSQL and for the chunker.
/// </summary>
internal static class TextCleanup
{
    /// <summary>
    /// Decodes the bytes of a text file. A byte order mark selects UTF-16 or UTF-32. Without it the text is UTF-8, and a wrong byte becomes U+FFFD.
    /// </summary>
    /// <param name="bytes">The bytes of the file.</param>
    /// <returns>The cleaned text, see <see cref="Clean"/>.</returns>
    public static string Decode(byte[] bytes)
    {
        using MemoryStream stream = new(bytes, writable: false);
        using StreamReader reader = new(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false), detectEncodingFromByteOrderMarks: true);
        return Clean(reader.ReadToEnd());
    }

    /// <summary>
    /// Cleans a text. Line ends become LF and a form feed becomes LF. The text keeps the tab and drops the NUL and the other control characters.
    /// A lone surrogate becomes U+FFFD. PostgreSQL rejects the NUL, and the encoder of Npgsql rejects a lone surrogate.
    /// </summary>
    /// <param name="text">The raw text.</param>
    /// <returns>The cleaned text.</returns>
    public static string Clean(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        StringBuilder builder = new(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n')
                    continue;

                builder.Append('\n');
            }
            else if (char.IsHighSurrogate(c))
            {
                if (i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    builder.Append(c).Append(text[i + 1]);
                    i++;
                }
                else
                {
                    builder.Append('�');
                }
            }
            else if (char.IsLowSurrogate(c))
            {
                builder.Append('�');
            }
            else if (c is '\n' or '\t')
            {
                builder.Append(c);
            }
            else if (c is '\f' or '\v')
            {
                builder.Append('\n');
            }
            else if (!char.IsControl(c) && c != '﻿')
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }
}
