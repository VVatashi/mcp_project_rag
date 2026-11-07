using System.Runtime.CompilerServices;
using System.Text;

using Dapper;

using Npgsql;

using ProjectRag.Core.Storage;

namespace ProjectRag.Core.Code;

/// <summary>
/// Reads the files of the code sources for the exact search.
/// </summary>
/// <param name="database">The database.</param>
public sealed class CodeSearchStore(Database database)
{
    // The most seconds that the scan of all files may run.
    private const int ScanSeconds = 120;

    // The owner project of a file is the project whose compilation gave its symbols. A file without a graph has no owner and counts as no test.
    private const string NotTestSql = """
         AND NOT EXISTS (SELECT 1 FROM code_file_project fp JOIN code_project tp ON tp.project_id = fp.project_id WHERE fp.document_id = d.document_id AND fp.is_owner AND tp.is_test)
        """;

    // The number of documents that one query reads. The caller usually stops after a few files, so a small batch ends the work early.
    internal const int BatchSize = 50;

    private long _documentsRead;

    /// <summary>
    /// Gets the number of documents whose text the store has read since its start.
    /// </summary>
    internal long DocumentsRead => Interlocked.Read(ref _documentsRead);

    /// <summary>
    /// Streams the files that may hold a match, ordered by the name of the source and the path.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The store first reads the list of the candidates without the text, ordered by the name of the source and the path.
    /// Then it reads the text of <see cref="BatchSize"/> candidates at a time, so the work stops when the caller stops.
    /// </para>
    /// <para>
    /// The query repeats <c>language IS NOT NULL</c>, so the planner can use the partial trigram index of the content.
    /// A literal text filters the rows in SQL. A regular expression filters nothing, because the regular expressions of .NET and of PostgreSQL differ.
    /// </para>
    /// </remarks>
    /// <param name="sourceId">The code source, or <see langword="null"/> for all code sources.</param>
    /// <param name="language">The language, or <see langword="null"/> for all languages.</param>
    /// <param name="literal">The literal text that the content must hold, or <see langword="null"/> for no filter.</param>
    /// <param name="caseSensitive">Tells that the case of letters matters for <paramref name="literal"/>.</param>
    /// <param name="includeTests"><see langword="false"/> drops the files whose owner project is a test project.</param>
    /// <param name="acceptPath">Tells which paths to read, or <see langword="null"/> for all paths. A rejected file is not read.</param>
    /// <param name="cancellationToken">The token that cancels the query.</param>
    /// <returns>The files, one at a time.</returns>
    /// <exception cref="CodeSearchException">A query ran longer than the limit of the scan or was canceled by the server.</exception>
    public async IAsyncEnumerable<CodeFile> StreamAsync(int? sourceId, string? language, string? literal, bool caseSensitive, bool includeTests, Func<string, bool>? acceptPath, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        List<CodeFile> candidates = [.. (await ListCandidatesAsync(sourceId, language, literal, caseSensitive, includeTests, cancellationToken)).Where(file => acceptPath is null || acceptPath(file.Path))];
        for (int start = 0; start < candidates.Count; start += BatchSize)
        {
            List<CodeFile> batch = candidates.GetRange(start, Math.Min(BatchSize, candidates.Count - start));
            Dictionary<long, string> contents = await ReadContentsAsync([.. batch.Select(static file => file.DocumentId)], cancellationToken);
            foreach (CodeFile file in batch)
            {
                if (contents.TryGetValue(file.DocumentId, out string? content))
                {
                    yield return new CodeFile { DocumentId = file.DocumentId, SourceId = file.SourceId, Source = file.Source, Path = file.Path, Language = file.Language, Content = content };
                }
            }
        }
    }

    private async Task<IReadOnlyList<CodeFile>> ListCandidatesAsync(int? sourceId, string? language, string? literal, bool caseSensitive, bool includeTests, CancellationToken cancellationToken)
    {
        StringBuilder sql = new(
            """
            SELECT d.document_id, d.source_id, s.name AS source, d.path, d.language
            FROM document d
            JOIN source s USING (source_id)
            WHERE d.language IS NOT NULL
            """);
        DynamicParameters parameters = new();
        if (sourceId is { } id)
        {
            sql.Append(" AND d.source_id = @SourceId");
            parameters.Add("SourceId", id);
        }

        if (language is not null)
        {
            sql.Append(" AND d.language = @Language");
            parameters.Add("Language", language);
        }

        if (!includeTests)
            sql.Append(NotTestSql);

        if (literal is not null)
        {
            sql.Append(caseSensitive
                ? @" AND d.content LIKE '%' || @Literal || '%' ESCAPE '\'"
                : @" AND d.content ILIKE '%' || @Literal || '%' ESCAPE '\'");
            parameters.Add("Literal", EscapeLike(literal));
        }

        sql.Append(" ORDER BY s.name COLLATE \"C\", d.path COLLATE \"C\"");
        await using NpgsqlConnection connection = await database.OpenAsync(cancellationToken);
        try
        {
            return [.. await connection.QueryAsync<CodeFile>(new CommandDefinition(sql.ToString(), parameters, commandTimeout: ScanSeconds, cancellationToken: cancellationToken))];
        }
        catch (NpgsqlException exception) when (IsTimeout(exception, cancellationToken))
        {
            throw TimeoutError();
        }
    }

    private async Task<Dictionary<long, string>> ReadContentsAsync(long[] documentIds, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await database.OpenAsync(cancellationToken);
        try
        {
            IEnumerable<(long DocumentId, string Content)> rows = await connection.QueryAsync<(long, string)>(new CommandDefinition(
                "SELECT document_id, content FROM document WHERE document_id = ANY (@Ids)", new { Ids = documentIds }, commandTimeout: ScanSeconds, cancellationToken: cancellationToken));
            Dictionary<long, string> contents = rows.ToDictionary(static row => row.DocumentId, static row => row.Content);
            Interlocked.Add(ref _documentsRead, contents.Count);
            return contents;
        }
        catch (NpgsqlException exception) when (IsTimeout(exception, cancellationToken))
        {
            throw TimeoutError();
        }
    }

    // The command timeout gives an exception with a TimeoutException inside. The server may also cancel a query itself.
    private static bool IsTimeout(NpgsqlException exception, CancellationToken cancellationToken) =>
        !cancellationToken.IsCancellationRequested && (exception.InnerException is TimeoutException || exception is PostgresException { SqlState: PostgresErrorCodes.QueryCanceled });

    private static CodeSearchException TimeoutError() =>
        new(nameof(CodeGrepRequest.Pattern), $"The search ran longer than {ScanSeconds} seconds. Narrow it with a source, a language, a path or a longer text.");

    /// <summary>
    /// Lists the files of the code sources with their paths, for the filter by a pattern of paths.
    /// </summary>
    /// <param name="sourceId">The code source, or <see langword="null"/> for all code sources.</param>
    /// <param name="language">The language, or <see langword="null"/> for all languages.</param>
    /// <param name="cancellationToken">The token that cancels the query.</param>
    /// <returns>The keys and the paths of the documents.</returns>
    public async Task<IReadOnlyList<(long DocumentId, string Path)>> ListPathsAsync(int? sourceId, string? language, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await database.OpenAsync(cancellationToken);
        IEnumerable<(long DocumentId, string Path)> rows = await connection.QueryAsync<(long, string)>(new CommandDefinition(
            """
            SELECT document_id, path
            FROM document
            WHERE language IS NOT NULL AND (@SourceId::integer IS NULL OR source_id = @SourceId) AND (@Language::text IS NULL OR language = @Language)
            """,
            new { SourceId = sourceId, Language = language }, cancellationToken: cancellationToken));
        return [.. rows];
    }

    /// <summary>
    /// Finds which of the projects of the code sources are test projects.
    /// </summary>
    /// <param name="sourceId">The code source, or <see langword="null"/> for all code sources.</param>
    /// <param name="cancellationToken">The token that cancels the query.</param>
    /// <returns>The keys of the test projects.</returns>
    public async Task<IReadOnlySet<int>> GetTestProjectIdsAsync(int? sourceId, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await database.OpenAsync(cancellationToken);
        IEnumerable<int> ids = await connection.QueryAsync<int>(new CommandDefinition(
            "SELECT project_id FROM code_project WHERE is_test AND (@SourceId::integer IS NULL OR source_id = @SourceId)",
            new { SourceId = sourceId }, cancellationToken: cancellationToken));
        return ids.ToHashSet();
    }

    /// <summary>
    /// Reads the text of a file of a code source.
    /// </summary>
    /// <param name="documentId">The key of the document.</param>
    /// <param name="cancellationToken">The token that cancels the query.</param>
    /// <returns>The text, or <see langword="null"/> when the document does not exist or belongs to a source of documentation.</returns>
    public async Task<string?> GetContentAsync(long documentId, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await database.OpenAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT content FROM document WHERE document_id = @DocumentId AND language IS NOT NULL", new { DocumentId = documentId }, cancellationToken: cancellationToken));
    }

    /// <summary>
    /// Escapes the characters that LIKE treats as marks.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <returns>The text for a pattern with <c>ESCAPE '\'</c>.</returns>
    public static string EscapeLike(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Replace(@"\", @"\\", StringComparison.Ordinal).Replace("%", @"\%", StringComparison.Ordinal).Replace("_", @"\_", StringComparison.Ordinal);
    }
}
