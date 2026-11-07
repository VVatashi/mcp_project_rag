using Dapper;

using Npgsql;

using ProjectRag.Core.Storage;

namespace ProjectRag.Core.Indexing;

/// <summary>
/// Reads and writes the runs of the indexer.
/// </summary>
/// <param name="database">The database.</param>
public sealed class IndexRunStore(Database database)
{
    /// <summary>
    /// Writes the start of a run.
    /// </summary>
    /// <param name="sourceId">The key of the source.</param>
    /// <param name="cancellationToken">The token that cancels the query.</param>
    /// <returns>The new run.</returns>
    public async Task<IndexRun> StartAsync(int sourceId, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await database.OpenAsync(cancellationToken);
        return await connection.QuerySingleAsync<IndexRun>(new CommandDefinition(
            """
            INSERT INTO index_run (source_id, status) VALUES (@SourceId, 'running')
            RETURNING run_id, source_id, (SELECT name FROM source WHERE source_id = @SourceId) AS source, status, started_at
            """,
            new { SourceId = sourceId }, cancellationToken: cancellationToken));
    }

    /// <summary>
    /// Writes the counters, the status and the messages of a run.
    /// </summary>
    /// <param name="run">The run.</param>
    /// <param name="cancellationToken">The token that cancels the query.</param>
    /// <returns>A task that completes after the write.</returns>
    public async Task SaveAsync(IndexRun run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        await using NpgsqlConnection connection = await database.OpenAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE index_run
            SET status = @Status, finished_at = CASE WHEN @Status = 'running' THEN NULL ELSE now() END,
                files_seen = @FilesSeen, documents_added = @DocumentsAdded, documents_updated = @DocumentsUpdated,
                documents_removed = @DocumentsRemoved, documents_unchanged = @DocumentsUnchanged, documents_failed = @DocumentsFailed,
                chunks_reused = @ChunksReused, chunks_embedded = @ChunksEmbedded,
                chunks_embedded_remote = @ChunksEmbeddedRemote, remote_cost = @RemoteCost, warning = @Warning, error = @Error
            WHERE run_id = @RunId
            """,
            run, cancellationToken: cancellationToken));
    }

    /// <summary>
    /// Reads the last runs.
    /// </summary>
    /// <param name="sourceId">The source, or <see langword="null"/> for all sources.</param>
    /// <param name="limit">The most runs to return.</param>
    /// <param name="cancellationToken">The token that cancels the query.</param>
    /// <returns>The runs, the newest first.</returns>
    public async Task<IReadOnlyList<IndexRun>> ListAsync(int? sourceId, int limit, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await database.OpenAsync(cancellationToken);
        IEnumerable<IndexRun> rows = await connection.QueryAsync<IndexRun>(new CommandDefinition(
            """
            SELECT r.*, s.name AS source
            FROM index_run r
            JOIN source s USING (source_id)
            WHERE @SourceId::integer IS NULL OR r.source_id = @SourceId
            ORDER BY r.run_id DESC
            LIMIT @Limit
            """,
            new { SourceId = sourceId, Limit = limit }, cancellationToken: cancellationToken));
        return [.. rows];
    }

    /// <summary>
    /// Ends the runs that a stop of the application left open.
    /// </summary>
    /// <param name="cancellationToken">The token that cancels the query.</param>
    /// <returns>The number of such runs.</returns>
    public async Task<int> CancelOpenRunsAsync(CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await database.OpenAsync(cancellationToken);
        return await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE index_run SET status = 'canceled', finished_at = now(), error = 'The application stopped during the run.' WHERE status = 'running'",
            cancellationToken: cancellationToken));
    }
}
