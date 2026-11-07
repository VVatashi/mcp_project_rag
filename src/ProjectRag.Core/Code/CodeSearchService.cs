using System.Diagnostics;

using Microsoft.Extensions.FileSystemGlobbing;

using ProjectRag.Core.Documents;
using ProjectRag.Core.Sources;

namespace ProjectRag.Core.Code;

/// <summary>
/// The exact search over the files of the code sources and the read of their lines.
/// </summary>
/// <param name="store">The store of the files.</param>
/// <param name="sources">The store of the sources.</param>
/// <param name="documents">The store of the documents.</param>
public sealed class CodeSearchService(CodeSearchStore store, SourceStore sources, DocumentStore documents)
{
    /// <summary>The number of matches when the request names no limit.</summary>
    public const int DefaultLimit = 100;

    /// <summary>The largest number of matches.</summary>
    public const int MaxLimit = 1000;

    /// <summary>The largest number of lines that one read returns.</summary>
    public const int MaxLines = 2000;

    private const int MaxGlobLength = 300;

    /// <summary>
    /// Finds the lines that match a pattern in the files of the code sources.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">The token that cancels the search.</param>
    /// <returns>The matches, the number of files with matches and the time.</returns>
    /// <exception cref="CodeSearchException">The pattern is empty or wrong, or the source is not a code source.</exception>
    public async Task<CodeGrepResult> GrepAsync(CodeGrepRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        CodeLineMatcher matcher = CodeLineMatcher.Create(request.Pattern, request.IsRegex, request.CaseSensitive);
        int limit = Math.Clamp(request.Limit ?? DefaultLimit, 1, MaxLimit);
        int? sourceId = await ResolveSourceAsync(request.SourceId, request.Source, nameof(CodeGrepRequest.SourceId), nameof(CodeGrepRequest.Source), cancellationToken);
        Matcher? paths = CreatePathMatcher(request.Path, nameof(CodeGrepRequest.Path));

        Stopwatch watch = Stopwatch.StartNew();
        List<CodeMatch> matches = [];
        int filesMatched = 0;
        bool truncated = false;
        string? language = string.IsNullOrWhiteSpace(request.Language) ? null : request.Language.Trim().ToLowerInvariant();
        await foreach (CodeFile file in store.StreamAsync(sourceId, language, request.IsRegex ? null : request.Pattern, request.CaseSensitive, request.IncludeTests, cancellationToken))
        {
            if (paths is not null && !paths.Match(file.Path).HasMatches)
                continue;

            bool any = false;
            foreach (LineMatch match in matcher.Find(file.Content))
            {
                if (matches.Count == limit)
                {
                    truncated = true;
                    break;
                }

                matches.Add(new CodeMatch(file.DocumentId, file.SourceId, file.Source, file.Path, file.Language, match.Line, match.Column, match.Length, match.Text, match.TextStart));
                any = true;
            }

            if (any)
                filesMatched++;

            if (truncated)
                break;
        }

        return new CodeGrepResult(matches, filesMatched, truncated, watch.ElapsedMilliseconds);
    }

    /// <summary>
    /// Reads lines of a file of a code source.
    /// </summary>
    /// <param name="documentId">The key of the document.</param>
    /// <param name="start">The number of the first line, from 1. A smaller number counts as 1.</param>
    /// <param name="end">The number of the last line. <see langword="null"/> reads <see cref="MaxLines"/> lines. A read never returns more than that.</param>
    /// <param name="cancellationToken">The token that cancels the query.</param>
    /// <returns>The lines, or <see langword="null"/> when the document does not exist or belongs to a source of documentation.</returns>
    public async Task<CodeLines?> ReadLinesAsync(long documentId, int start, int? end, CancellationToken cancellationToken)
    {
        if (await documents.GetAsync(documentId, cancellationToken) is not { Language: not null } document
            || await store.GetContentAsync(documentId, cancellationToken) is not { } content)
        {
            return null;
        }

        IReadOnlyList<string> all = CodeText.SplitLines(content);
        start = Math.Max(1, start);
        int last = (int)Math.Min(Math.Min(end ?? (long)start + MaxLines - 1, (long)start + MaxLines - 1), all.Count);
        IReadOnlyList<string> lines = start > last ? [] : [.. all.Skip(start - 1).Take(last - start + 1)];
        return new CodeLines(document, start, Math.Max(last, start - 1), all.Count, lines);
    }

    /// <summary>
    /// Creates the matcher of a pattern of paths. A pattern without a slash matches the name of a file at any depth.
    /// </summary>
    /// <param name="pattern">The pattern, or <see langword="null"/> or blank for no filter.</param>
    /// <param name="field">The name of the field for an error.</param>
    /// <returns>The matcher, or <see langword="null"/> for no filter.</returns>
    /// <exception cref="CodeSearchException">The pattern is too long or holds a character that no path holds.</exception>
    internal static Matcher? CreatePathMatcher(string? pattern, string field)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            return null;

        string glob = pattern.Trim().Replace('\\', '/');
        if (glob.Length > MaxGlobLength || glob.Contains('\0', StringComparison.Ordinal) || glob.Contains("//", StringComparison.Ordinal))
            throw new CodeSearchException(field, $"The pattern of paths is wrong: {pattern.Trim()[..Math.Min(pattern.Trim().Length, 60)]}");

        Matcher matcher = new(StringComparison.OrdinalIgnoreCase);
        matcher.AddInclude(glob.Contains('/', StringComparison.Ordinal) ? glob : $"**/{glob}");
        return matcher;
    }

    /// <summary>
    /// Finds a code source by its key or its name.
    /// </summary>
    /// <param name="sourceId">The key, or <see langword="null"/>.</param>
    /// <param name="name">The name, used when the key is missing, or <see langword="null"/>.</param>
    /// <param name="idField">The name of the field of the key for an error.</param>
    /// <param name="nameField">The name of the field of the name for an error.</param>
    /// <param name="cancellationToken">The token that cancels the query.</param>
    /// <returns>The key of the code source, or <see langword="null"/> when the request names none.</returns>
    /// <exception cref="CodeSearchException">The source does not exist or is not a code source.</exception>
    internal async Task<int?> ResolveSourceAsync(int? sourceId, string? name, string idField, string nameField, CancellationToken cancellationToken)
    {
        Source? source;
        string field;
        if (sourceId is { } id)
        {
            field = idField;
            source = await sources.GetAsync(id, null, cancellationToken);
        }
        else if (!string.IsNullOrWhiteSpace(name))
        {
            field = nameField;
            IReadOnlyDictionary<string, int> found = await sources.FindIdsAsync([name.Trim()], cancellationToken);
            source = found.Count == 0 ? null : await sources.GetAsync(found.Values.First(), null, cancellationToken);
        }
        else
        {
            return null;
        }

        if (source is null)
            throw new CodeSearchException(field, "The knowledge base has no such source. Call list_sources.");

        if (source.Kind != SourceKind.Code)
            throw new CodeSearchException(field, $"The source {source.Name} is not a code source.");

        return source.SourceId;
    }
}
