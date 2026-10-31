using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Npgsql;

using ProjectRag.Core.Sources;

namespace ProjectRag.Core.Indexing;

/// <summary>
/// Runs the indexer over the sources of the queue, one at a time. It also puts all sources into the queue at the start and at each scan interval.
/// </summary>
/// <param name="scheduler">The scheduler of the runs.</param>
/// <param name="indexer">The indexer.</param>
/// <param name="sources">The store of the sources.</param>
/// <param name="runs">The store of the runs.</param>
/// <param name="options">The options of the indexer.</param>
/// <param name="logger">The logger.</param>
public sealed class IndexWorker(IndexScheduler scheduler, Indexer indexer, SourceStore sources, IndexRunStore runs, IOptionsMonitor<IndexingOptions> options, ILogger<IndexWorker> logger) : BackgroundService
{
    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            int canceled = await runs.CancelOpenRunsAsync(stoppingToken);
            if (canceled > 0)
                logger.LogWarning("{Count} runs of the indexer were open after the last stop. They are canceled now.", canceled);

            Task scans = ScanAsync(stoppingToken);
            await foreach (int sourceId in scheduler.Reader.ReadAllAsync(stoppingToken))
            {
                scheduler.MarkTaken(sourceId);
                try
                {
                    await indexer.RunAsync(sourceId, run => scheduler.Current = run, stoppingToken);
                }
                catch (NpgsqlException exception)
                {
                    logger.LogError(exception, "The indexer cannot reach the database for the source {SourceId}.", sourceId);
                }
                finally
                {
                    scheduler.Current = null;
                }
            }

            await scans;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The application stops.
        }
    }

    private async Task ScanAsync(CancellationToken stoppingToken)
    {
        int minutes = options.CurrentValue.ScanIntervalMinutes;
        if (minutes <= 0)
            return;

        using PeriodicTimer timer = new(TimeSpan.FromMinutes(minutes));
        do
        {
            try
            {
                foreach (int sourceId in await sources.ListIdsAsync(stoppingToken))
                    scheduler.Enqueue(sourceId);
            }
            catch (NpgsqlException exception)
            {
                logger.LogError(exception, "The scan of the sources cannot reach the database.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
