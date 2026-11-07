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

    /// <summary>
    /// Streams the files that may hold a match, ordered by the name of the source and the path.
    /// </summary>
    /// <remarks>
    /// The query repeats <c>language IS NOT NULL</c>, so the planner can use the partial trigram index of the content.
    /// A literal text filters the rows in SQL. A regular expression filters nothing, because the regular expressions of .NET and of PostgreSQL differ.
    /// </remarks>
    /// <param name="sourceId">The code source, or <see langword="null"/> for all code sources.</param>
    /// <param name="language">The language, or <see langword="null"/> for all languages.</param>
    /// <param name="literal">The literal text that the content must hold, or <see langword="null"/> for no filter.</param>
    /// <param name="caseSensitive">Tells that the case of letters matters for <paramref name="literal"/>.</param>
    /// <param name="includeTests"><see langword="false"/> drops the files whose owner project is a test project.</param>
    /// <param name="cancellationToken">The token that cancels the query.</param>
    /// <returns>The files, one at a time.</returns>
    public async IAsyncEnumerable<CodeFile> StreamAsync(int? sourceId, string? language, string? literal, bool caseSensitive, bool includeTests, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        StringBuilder sql = new(
            """
            SELECT d.document_id, d.source_id, s.name AS source, d.path, d.language, d.content
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
        await foreach (CodeFile file in connection.QueryUnbufferedAsync<CodeFile>(sql.ToString(), parameters, commandTimeout: ScanSeconds).WithCancellation(cancellationToken))
            yield return file;
    }

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
