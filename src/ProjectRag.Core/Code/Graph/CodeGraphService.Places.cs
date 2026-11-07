using Dapper;

using Npgsql;

namespace ProjectRag.Core.Code.Graph;

public sealed partial class CodeGraphService
{
    /// <summary>
    /// Finds the innermost symbol whose declaration holds a line, for each of several places.
    /// </summary>
    /// <remarks>
    /// The declaration with the fewest lines wins, so a method wins over its type. A place in a file without a graph, or outside every declaration, has no symbol.
    /// </remarks>
    /// <param name="places">The documents and the lines, from 1.</param>
    /// <param name="cancellationToken">The token that cancels the query.</param>
    /// <returns>A symbol or <see langword="null"/> for each place, in the order of the places.</returns>
    public async Task<IReadOnlyList<SymbolSummary?>> FindEnclosingAsync(IReadOnlyList<(long DocumentId, int Line)> places, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(places);
        if (places.Count == 0)
            return [];

        await using NpgsqlConnection connection = await database.OpenAsync(cancellationToken);
        List<(long Ordinal, int SymbolId)> found = [.. await connection.QueryAsync<(long, int)>(new CommandDefinition(
            """
            SELECT p.ordinal, x.symbol_id
            FROM unnest(@Documents, @Lines) WITH ORDINALITY AS p(document_id, line, ordinal)
            CROSS JOIN LATERAL (
                SELECT cd.symbol_id
                FROM code_declaration cd
                WHERE cd.document_id = p.document_id AND cd.start_line <= p.line AND cd.end_line >= p.line
                ORDER BY cd.end_line - cd.start_line, cd.start_line DESC, cd.symbol_id
                LIMIT 1) x
            """,
            new { Documents = places.Select(static place => place.DocumentId).ToArray(), Lines = places.Select(static place => place.Line).ToArray() },
            cancellationToken: cancellationToken))];

        Dictionary<int, SymbolSummary> summaries = await LoadSummariesAsync(connection, [.. found.Select(static item => item.SymbolId).Distinct()], cancellationToken);
        SymbolSummary?[] result = new SymbolSummary?[places.Count];
        foreach ((long ordinal, int symbolId) in found)
            result[ordinal - 1] = summaries.GetValueOrDefault(symbolId);

        return result;
    }
}
