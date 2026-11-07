using System.Diagnostics;

using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.Options;

using ProjectRag.Core.Code.Chunks;
using ProjectRag.Core.Code.Graph;
using ProjectRag.Core.Documents;
using ProjectRag.Core.Embeddings;
using ProjectRag.Core.Search;

namespace ProjectRag.Core.Code;

/// <summary>
/// The search over the code sources in tiers: symbols, exact lines and similar code.
/// </summary>
/// <remarks>
/// <para>
/// The three tiers return different kinds of items, so one rank over them would mean nothing. Each tier answers on its own, and a place appears in its highest tier.
/// </para>
/// <list type="number">
/// <item><description>Symbols from the code graph, by exact name, name without case, prefix, humps or qualified name. The tier runs for a query without spaces.</description></item>
/// <item><description>Exact lines: a literal text with the case, and without the case when that finds nothing. The tier runs for a query of one line with 3 or more characters.</description></item>
/// <item><description>Similar code: the vector search with the template of the code query and the token search over split identifiers, fused by their ranks, then the reranker.</description></item>
/// </list>
/// <para>
/// Each model is optional. Without the embedding server the third tier uses the token search only and says so in the notes.
/// </para>
/// </remarks>
/// <param name="search">The exact search and the lookup of sources.</param>
/// <param name="files">The reads of the files of the code sources.</param>
/// <param name="chunks">The SQL of the third tier.</param>
/// <param name="graph">The queries over the code graph.</param>
/// <param name="queryModels">The embedding of the query and the reranker.</param>
/// <param name="searchOptions">The options of the search.</param>
/// <param name="embeddingOptions">The options of the embedding model, for the template of the query.</param>
public sealed class CodeTierSearch(
    CodeSearchService search,
    CodeSearchStore files,
    CodeChunkStore chunks,
    CodeGraphService graph,
    QueryModels queryModels,
    IOptionsMonitor<SearchOptions> searchOptions,
    IOptionsMonitor<EmbeddingOptions> embeddingOptions)
{
    /// <summary>The number of similar chunks when the request names no limit.</summary>
    public const int DefaultLimit = 10;

    /// <summary>The largest number of similar chunks.</summary>
    public const int MaxLimit = 50;

    /// <summary>The largest number of characters of a query.</summary>
    public const int MaxQueryLength = 500;

    /// <summary>The largest number of symbols.</summary>
    public const int MaxSymbols = 10;

    /// <summary>The largest number of exact lines.</summary>
    public const int MaxMatches = 20;

    private const int MinLiteralLength = 3;

    private static readonly string[] SymbolMatchKinds =
    [
        CodeMatchKinds.Exact, CodeMatchKinds.ExactIgnoreCase, CodeMatchKinds.Prefix, CodeMatchKinds.Humps, CodeMatchKinds.Qualified,
    ];

    /// <summary>
    /// Searches the code sources.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">The token that cancels the search.</param>
    /// <returns>The three tiers and the state of the legs of the third tier.</returns>
    /// <exception cref="CodeSearchException">The query is empty or too long, the source is unknown or no code source, or the pattern of paths is wrong.</exception>
    public async Task<CodeSearchResult> SearchAsync(CodeSearchRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        // A JSON null reaches the property despite its type.
        string query = request.Query?.Trim() ?? string.Empty;
        if (query.Length == 0)
            throw new CodeSearchException("query", "The query must not be empty.");

        if (query.Length > MaxQueryLength)
            throw new CodeSearchException("query", $"The query must hold at most {MaxQueryLength} characters.");

        Stopwatch watch = Stopwatch.StartNew();
        int limit = Math.Clamp(request.Limit ?? DefaultLimit, 1, MaxLimit);
        int? sourceId = await search.ResolveSourceAsync(request.SourceId, request.Source, "sourceId", "codebase", cancellationToken);
        string? language = string.IsNullOrWhiteSpace(request.Language) ? null : request.Language.Trim().ToLowerInvariant();
        Matcher? paths = CodeSearchService.CreatePathMatcher(request.Path, "path");
        bool includeTests = request.IncludeTests ?? true;

        IReadOnlyList<SymbolMatch> symbols = query.Any(char.IsWhiteSpace)
            ? []
            : await FindSymbolsAsync(query, sourceId, language, paths, includeTests, cancellationToken);
        IReadOnlyList<CodeMatch> matches = await FindLinesAsync(query, sourceId, language, request.Path, includeTests, symbols, cancellationToken);

        List<string> notes = [];
        (IReadOnlyList<CodeChunkHit> hits, string mode, bool reranked) = await FindSimilarAsync(query, request, sourceId, language, paths, includeTests, limit, notes, cancellationToken);

        IReadOnlyList<SymbolSummary?> enclosing = await graph.FindEnclosingAsync([.. hits.Select(static hit => (hit.DocumentId, hit.LineStart))], cancellationToken);
        for (int i = 0; i < hits.Count; i++)
            hits[i].Symbol = enclosing[i];

        return new CodeSearchResult(query, symbols, matches, hits, mode, reranked, notes, watch.ElapsedMilliseconds);
    }

    private async Task<IReadOnlyList<SymbolMatch>> FindSymbolsAsync(
        string query, int? sourceId, string? language, Matcher? paths, bool includeTests, CancellationToken cancellationToken)
    {
        // The graph knows no filter by path or test, so a filtered search reads a wider list and keeps the first matches.
        bool filtered = paths is not null || language is not null || !includeTests;
        IReadOnlyList<SymbolMatch> found = await graph.SearchSymbolsAsync(query, sourceId, null, null, filtered ? CodeGraphService.MaxSearchLimit : MaxSymbols, SymbolMatchKinds, cancellationToken);
        IReadOnlySet<int> testProjects = includeTests ? new HashSet<int>() : await files.GetTestProjectIdsAsync(sourceId, cancellationToken);
        return
        [
            .. found.Where(symbol => !testProjects.Contains(symbol.ProjectId)
                    && (language is null || (symbol.Path is not null && CodeLanguages.FromPath(symbol.Path) == language))
                    && (paths is null || (symbol.Path is not null && paths.Match(symbol.Path).HasMatches)))
                .Take(MaxSymbols),
        ];
    }

    private async Task<IReadOnlyList<CodeMatch>> FindLinesAsync(
        string query, int? sourceId, string? language, string? path, bool includeTests, IReadOnlyList<SymbolMatch> symbols, CancellationToken cancellationToken)
    {
        if (query.Length < MinLiteralLength || query.Contains('\n', StringComparison.Ordinal) || query.Contains('\r', StringComparison.Ordinal))
            return [];

        // The lines on the name line of a listed symbol belong to the tier of symbols, so the search reads a few more lines than it keeps.
        CodeGrepRequest request = new()
        {
            Pattern = query,
            SourceId = sourceId,
            Path = path,
            Language = language,
            IncludeTests = includeTests,
            CaseSensitive = true,
            Limit = MaxMatches + symbols.Count,
        };
        CodeGrepResult result = await search.GrepAsync(request, cancellationToken);
        if (result.Matches.Count == 0)
        {
            request.CaseSensitive = false;
            result = await search.GrepAsync(request, cancellationToken);
        }

        HashSet<(long DocumentId, int Line)> declared = [.. symbols.Where(static symbol => symbol.DocumentId is not null && symbol.Line is not null).Select(static symbol => (symbol.DocumentId!.Value, symbol.Line!.Value))];
        return [.. result.Matches.Where(match => !declared.Contains((match.DocumentId, match.Line))).Take(MaxMatches)];
    }

    private async Task<(IReadOnlyList<CodeChunkHit> Hits, string Mode, bool Reranked)> FindSimilarAsync(
        string query, CodeSearchRequest request, int? sourceId, string? language, Matcher? paths, bool includeTests, int limit, List<string> notes, CancellationToken cancellationToken)
    {
        (EmbeddingModel? model, float[]? vector) = await queryModels.EmbedAsync(query, embeddingOptions.CurrentValue.CodeQueryTemplate, notes, cancellationToken);
        if (model is null)
            notes.Add("The similar code comes from the token search only.");

        (bool useReranker, int candidates) = queryModels.PlanRerank(request.Rerank, limit);
        long[]? documentIds = null;
        if (paths is not null)
        {
            documentIds = [.. (await files.ListPathsAsync(sourceId, language, cancellationToken)).Where(file => paths.Match(file.Path).HasMatches).Select(static file => file.DocumentId)];
            if (documentIds.Length == 0)
                return ([], model is null ? "text" : "hybrid", false);
        }

        IReadOnlyList<CodeChunkHit> found = await chunks.SearchAsync(
            model, vector, IdentifierTokens.Terms(query), sourceId, language, documentIds, includeTests, candidates, searchOptions.CurrentValue, cancellationToken);
        List<CodeChunkHit> hits = [.. found];

        bool reranked = false;
        if (useReranker && hits.Count > 1)
        {
            IReadOnlyList<double>? scores = await queryModels.RerankAsync(query, [.. hits.Select(static hit => TextChunk.Compose(hit.Heading, hit.Text))], notes, cancellationToken);
            if (scores is not null)
            {
                for (int i = 0; i < hits.Count; i++)
                {
                    hits[i].RerankScore = scores[i];
                    hits[i].Score = scores[i];
                }

                hits = [.. hits.OrderByDescending(static hit => hit.RerankScore).ThenBy(static hit => hit.VectorRank ?? int.MaxValue).ThenBy(static hit => hit.TextRank ?? int.MaxValue)];
                reranked = true;
            }
        }

        bool anyVector = hits.Any(static hit => hit.VectorRank is not null);
        bool anyText = hits.Any(static hit => hit.TextRank is not null);
        string mode = model is null || (!anyVector && anyText) ? "text" : anyVector && !anyText ? "vector" : "hybrid";
        return ([.. hits.Take(limit)], mode, reranked);
    }
}
