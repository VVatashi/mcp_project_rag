using System.Text;
using System.Text.RegularExpressions;

namespace ProjectRag.Core.Code;

/// <summary>
/// The rules of one <c>.gitignore</c> file. The patterns follow the documentation of gitignore.
/// </summary>
/// <remarks>
/// A path is relative to the folder of the file and has forward slashes. The last matching rule of a file wins.
/// The comparison of names is case sensitive.
/// </remarks>
public sealed class GitIgnore
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(2);

    private readonly List<Rule> _rules = [];

    private GitIgnore()
    {
    }

    /// <summary>
    /// Reads the rules from the lines of a file.
    /// </summary>
    /// <param name="lines">The lines.</param>
    /// <returns>The rules.</returns>
    public static GitIgnore Parse(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        GitIgnore ignore = new();
        bool first = true;
        foreach (string line in lines)
        {
            string text = first ? line.TrimStart('﻿') : line;
            first = false;
            if (ParseRule(text) is { } rule)
                ignore._rules.Add(rule);
        }

        return ignore;
    }

    /// <summary>
    /// Reads the rules from a file.
    /// </summary>
    /// <param name="path">The path of the file.</param>
    /// <returns>The rules.</returns>
    public static GitIgnore Load(string path) => Parse(File.ReadLines(path));

    /// <summary>
    /// Finds the last rule that matches a path. The parents of the path do not count.
    /// </summary>
    /// <param name="path">The path relative to the folder of the file.</param>
    /// <param name="isDirectory">Tells that the path is a folder.</param>
    /// <returns><see langword="true"/> when the rule ignores the path, <see langword="false"/> when a negated rule brings it back, <see langword="null"/> when no rule matches.</returns>
    public bool? Match(string path, bool isDirectory)
    {
        ArgumentNullException.ThrowIfNull(path);
        string name = path[(path.LastIndexOf('/') + 1)..];
        for (int i = _rules.Count - 1; i >= 0; i--)
        {
            Rule rule = _rules[i];
            if (rule.DirectoryOnly && !isDirectory)
                continue;

            if (rule.Pattern.IsMatch(rule.Anchored ? path : name))
                return !rule.Negated;
        }

        return null;
    }

    /// <summary>
    /// Checks whether the rules ignore a path. An ignored folder also ignores everything in it, and a rule cannot bring such a file back.
    /// </summary>
    /// <param name="path">The path relative to the folder of the file.</param>
    /// <param name="isDirectory">Tells that the path is a folder.</param>
    /// <returns><see langword="true"/> for an ignored path.</returns>
    public bool IsIgnored(string path, bool isDirectory)
    {
        ArgumentNullException.ThrowIfNull(path);
        for (int slash = path.IndexOf('/', StringComparison.Ordinal); slash >= 0; slash = path.IndexOf('/', slash + 1))
        {
            if (Match(path[..slash], true) == true)
                return true;
        }

        return Match(path, isDirectory) == true;
    }

    private static Rule? ParseRule(string line)
    {
        // A trailing space counts unless a backslash quotes it.
        int end = line.Length;
        while (end > 0 && line[end - 1] == ' ' && (end < 2 || line[end - 2] != '\\'))
            end--;

        string pattern = line[..end];
        if (pattern.Length == 0 || pattern[0] == '#')
            return null;

        bool negated = pattern[0] == '!';
        if (negated)
            pattern = pattern[1..];

        bool directoryOnly = pattern.EndsWith('/');
        if (directoryOnly)
            pattern = pattern[..^1];

        // A slash at the start or in the middle ties the pattern to the folder of the file.
        bool anchored = pattern.Contains('/', StringComparison.Ordinal);
        pattern = pattern.TrimStart('/');
        if (pattern.Length == 0)
            return null;

        return new Rule(new Regex(ToRegex(pattern), RegexOptions.CultureInvariant | RegexOptions.Singleline, MatchTimeout), negated, directoryOnly, anchored);
    }

    private static string ToRegex(string glob)
    {
        StringBuilder regex = new("^");
        int i = 0;
        while (i < glob.Length)
        {
            char c = glob[i];
            switch (c)
            {
                case '\\':
                    if (i + 1 < glob.Length)
                        regex.Append(Regex.Escape(glob[i + 1].ToString()));
                    else
                        regex.Append(@"\\");
                    i += 2;
                    break;

                case '*':
                    int stars = 1;
                    while (i + stars < glob.Length && glob[i + stars] == '*')
                        stars++;

                    bool atStart = i == 0 || glob[i - 1] == '/';
                    bool atEnd = i + stars == glob.Length;
                    if (stars >= 2 && atStart && atEnd)
                    {
                        regex.Append(".*");
                    }
                    else if (stars >= 2 && atStart && glob[i + stars] == '/')
                    {
                        regex.Append("(?:.*/)?");
                        stars++;
                    }
                    else
                    {
                        regex.Append("[^/]*");
                    }

                    i += stars;
                    break;

                case '?':
                    regex.Append("[^/]");
                    i++;
                    break;

                case '[':
                    int close = FindClassEnd(glob, i);
                    if (close < 0)
                    {
                        regex.Append(@"\[");
                        i++;
                    }
                    else
                    {
                        AppendClass(regex, glob.AsSpan(i + 1, close - i - 1));
                        i = close + 1;
                    }

                    break;

                default:
                    regex.Append(Regex.Escape(c.ToString()));
                    i++;
                    break;
            }
        }

        return regex.Append('$').ToString();
    }

    // Finds the ']' that closes the class. A ']' right after the opening or the negation is a member.
    private static int FindClassEnd(string glob, int open)
    {
        int i = open + 1;
        if (i < glob.Length && (glob[i] == '!' || glob[i] == '^'))
            i++;

        if (i < glob.Length && glob[i] == ']')
            i++;

        for (; i < glob.Length; i++)
        {
            if (glob[i] == '\\')
            {
                i++;
            }
            else if (glob[i] == '[' && i + 1 < glob.Length && glob[i + 1] == ':')
            {
                int named = glob.IndexOf(":]", i + 2, StringComparison.Ordinal);
                if (named >= 0)
                    i = named + 1;
            }
            else if (glob[i] == ']')
            {
                return i;
            }
        }

        return -1;
    }

    private static void AppendClass(StringBuilder regex, ReadOnlySpan<char> body)
    {
        bool negated = body.Length > 0 && (body[0] == '!' || body[0] == '^');
        if (negated)
            body = body[1..];

        regex.Append(negated ? "[^/" : "[");
        for (int i = 0; i < body.Length; i++)
        {
            char c = body[i];
            if (c == '\\' && i + 1 < body.Length)
            {
                regex.Append(Regex.Escape(body[++i].ToString()));
            }
            else if (c == '[' && i + 1 < body.Length && body[i + 1] == ':' && body[(i + 2)..].IndexOf(":]", StringComparison.Ordinal) is var named and >= 0)
            {
                regex.Append(PosixClass(body.Slice(i + 2, named).ToString()));
                i += named + 3;
            }
            else if (c == '-')
            {
                regex.Append('-');
            }
            else if (c is '[' or ']' or '^')
            {
                regex.Append('\\').Append(c);
            }
            else
            {
                regex.Append(c);
            }
        }

        regex.Append(']');
    }

    private static string PosixClass(string name) => name switch
    {
        "alpha" => @"a-zA-Z",
        "digit" => "0-9",
        "alnum" => "a-zA-Z0-9",
        "upper" => "A-Z",
        "lower" => "a-z",
        "space" => @" \t\n\r\f\v",
        "blank" => @" \t",
        "punct" => @"!-/:-@\[-`{-~",
        "xdigit" => "0-9a-fA-F",
        _ => string.Empty,
    };

    private sealed record Rule(Regex Pattern, bool Negated, bool DirectoryOnly, bool Anchored);
}
