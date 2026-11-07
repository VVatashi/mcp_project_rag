using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using ProjectRag.Core.Sources;

namespace ProjectRag.Core.Indexing;

/// <summary>
/// Watches the folders of the sources and asks the scheduler for a run after a burst of changes, so the index follows the disk within seconds.
/// </summary>
/// <remarks>
/// <para>
/// The service reads the folders of the sources at the start and every <see cref="WatchOptions.RefreshSeconds"/>, and keeps one watcher for each existing folder, see <see cref="SourceWatchSet"/>.
/// The scan of <see cref="IndexWorker"/> stays as a safety net for the events that the system loses.
/// </para>
/// </remarks>
/// <param name="sources">The store of the sources.</param>
/// <param name="scheduler">The scheduler of the runs.</param>
/// <param name="options">The options of the watcher.</param>
/// <param name="time">The clock.</param>
/// <param name="logger">The logger.</param>
public sealed class SourceWatcher(SourceStore sources, IndexScheduler scheduler, IOptionsMonitor<WatchOptions> options, TimeProvider time, ILogger<SourceWatcher> logger) : BackgroundService
{
    private static readonly TimeSpan MinTick = TimeSpan.FromMilliseconds(25);

    private static readonly TimeSpan MaxTick = TimeSpan.FromMilliseconds(500);

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.CurrentValue.Enabled)
        {
            logger.LogInformation("The watcher of the source folders is off. Set Watch:Enabled to true to turn it on.");
            return;
        }

        using SourceWatchSet set = new(scheduler, time, logger);
        DateTimeOffset nextRefresh = time.GetUtcNow();
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                WatchOptions current = options.CurrentValue;
                if (time.GetUtcNow() >= nextRefresh)
                {
                    await RefreshAsync(set, stoppingToken);
                    nextRefresh = time.GetUtcNow() + TimeSpan.FromSeconds(Math.Max(1, current.RefreshSeconds));
                }

                TimeSpan quiet = TimeSpan.FromMilliseconds(Math.Max(1, current.QuietMilliseconds));
                Flush(set, quiet, TimeSpan.FromSeconds(Math.Max(1, current.MaxDelaySeconds)));
                await Task.Delay(Tick(quiet), time, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The application stops.
        }
    }

    // The pause between two checks of the debounce. A short quiet time gets a short pause, a long one does not need polling every 25 ms.
    private static TimeSpan Tick(TimeSpan quiet) => TimeSpan.FromTicks(Math.Clamp(quiet.Ticks / 4, MinTick.Ticks, MaxTick.Ticks));

    // An exception out of the loop would stop the whole host, so any error except the stop of the application only skips this step.
    private async Task RefreshAsync(SourceWatchSet set, CancellationToken stoppingToken)
    {
#pragma warning disable CA1031
        try
        {
            set.Sync(await sources.ListFoldersAsync(stoppingToken));
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
#pragma warning restore CA1031
        {
            logger.LogError(exception, "The watcher cannot refresh the list of the sources.");
        }
    }

    private void Flush(SourceWatchSet set, TimeSpan quiet, TimeSpan maxDelay)
    {
#pragma warning disable CA1031
        try
        {
            set.Flush(quiet, maxDelay);
        }
        catch (Exception exception)
#pragma warning restore CA1031
        {
            logger.LogError(exception, "The watcher cannot ask for the runs of the changed sources.");
        }
    }
}
