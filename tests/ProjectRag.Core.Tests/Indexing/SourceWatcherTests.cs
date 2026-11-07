using Microsoft.Extensions.Logging;

using Npgsql;

using ProjectRag.Core.Indexing;
using ProjectRag.Core.Sources;
using ProjectRag.Core.Storage;

namespace ProjectRag.Core.Tests.Indexing;

public sealed class SourceWatcherTests
{
    [Fact]
    public async Task Watcher_KeepsRunningAfterARefreshThatThrowsAnyException()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        NpgsqlDataSource dataSource = NpgsqlDataSource.Create("Host=127.0.0.1;Database=unused");
        await dataSource.DisposeAsync();
        ErrorCounter logger = new();
        WatchOptions options = new() { Enabled = true, RefreshSeconds = 1, QuietMilliseconds = 20 };
        using SourceWatcher watcher = new(new SourceStore(new Database(dataSource)), new IndexScheduler(), new FixedOptions<WatchOptions>(options), TimeProvider.System, logger);

        await watcher.StartAsync(cancellationToken);
        for (int i = 0; i < 200 && logger.Errors == 0; i++)
            await Task.Delay(25, cancellationToken);

        await Task.Delay(300, cancellationToken);
        Assert.True(logger.Errors > 0);
        Assert.False(watcher.ExecuteTask!.IsCompleted);

        await watcher.StopAsync(cancellationToken);
        Assert.True(watcher.ExecuteTask.IsCompletedSuccessfully);
    }

    private sealed class ErrorCounter : ILogger<SourceWatcher>
    {
        private int _errors;

        public int Errors => Volatile.Read(ref _errors);

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Error)
                Interlocked.Increment(ref _errors);
        }
    }
}
