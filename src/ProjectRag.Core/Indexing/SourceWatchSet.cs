using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.Logging;

using ProjectRag.Core.Sources;

namespace ProjectRag.Core.Indexing;

/// <summary>
/// Follows the folders of the sources with one <see cref="FileSystemWatcher"/> each and asks the scheduler for a run after a burst of changes.
/// </summary>
/// <remarks>
/// <para>
/// A watcher is a hint. The prefilter drops the events in <c>.git</c>, <c>bin</c>, <c>obj</c>, <c>node_modules</c>, <c>artifacts</c> and dot folders, and the run applies the exact rules of the file list.
/// The scan of the worker stays as a safety net for the events that the system loses.
/// </para>
/// <para>
/// The debounce works per source. After the first change the set waits until the quiet time passes without a change, but not longer than the maximum delay.
/// A buffer overflow of the system asks for a run at once, because the set no longer knows which files changed.
/// The set reads the time from a <see cref="TimeProvider"/> and starts no timer, so a test drives it with <see cref="Flush"/>.
/// </para>
/// </remarks>
/// <param name="scheduler">The scheduler that gets the requests of runs. It keeps a source at most once in its queue.</param>
/// <param name="time">The clock.</param>
/// <param name="logger">The logger.</param>
internal sealed class SourceWatchSet(IndexScheduler scheduler, TimeProvider time, ILogger logger) : IDisposable
{
    private const int BufferBytes = 64 * 1024;

    private static readonly string[] NoiseFolders = [".git", "bin", "obj", "node_modules", "artifacts"];

    private readonly Lock _lock = new();
    private readonly Dictionary<int, FolderWatch> _watches = [];
    private readonly Dictionary<int, Pending> _pending = [];
    private readonly HashSet<int> _reported = [];
    private bool _disposed;

    /// <summary>
    /// Gets the number of folders that the set watches now.
    /// </summary>
    public int WatchCount
    {
        get
        {
            lock (_lock)
                return _watches.Count;
        }
    }

    /// <summary>
    /// Gets the keys of the sources with changes that wait for the end of the quiet time.
    /// </summary>
    public IReadOnlyList<int> Waiting
    {
        get
        {
            lock (_lock)
                return [.. _pending.Keys.Order()];
        }
    }

    /// <summary>
    /// Checks whether the prefilter drops the event of a path.
    /// </summary>
    /// <param name="relativePath">The path relative to the folder of the source, with any separator.</param>
    /// <param name="dotGlobs">The patterns that bring dot files back, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> for a path in a folder that the file list skips.</returns>
    public static bool IsNoise(string relativePath, Matcher? dotGlobs)
    {
        string[] segments = relativePath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(static segment => NoiseFolders.Contains(segment, StringComparer.OrdinalIgnoreCase)))
            return true;

        return segments.Any(static segment => segment.StartsWith('.')) && !(dotGlobs?.Match(string.Join('/', segments)).HasMatches ?? false);
    }

    /// <summary>
    /// Brings the watchers in line with the folders of the sources.
    /// </summary>
    /// <remarks>
    /// It starts a watcher for a new folder, replaces the watcher of a changed folder or of a watcher that failed, and stops the watcher of a deleted source or of a folder that is gone.
    /// A folder that cannot be watched logs a warning once and gets a new try at the next call.
    /// </remarks>
    /// <param name="folders">The folders of all sources.</param>
    public void Sync(IReadOnlyList<SourceFolder> folders)
    {
        ArgumentNullException.ThrowIfNull(folders);
        Dictionary<int, SourceFolder> wanted = folders.Where(static folder => folder.RootPath.Length > 0 && Directory.Exists(folder.RootPath)).ToDictionary(static folder => folder.SourceId);
        List<FolderWatch> stopped = [];
        List<SourceFolder> started = [];
        lock (_lock)
        {
            if (_disposed)
                return;

            foreach ((int sourceId, FolderWatch watch) in _watches.ToArray())
            {
                if (!wanted.TryGetValue(sourceId, out SourceFolder? folder) || watch.Faulted || watch.RootPath != folder.RootPath || !watch.DotGlobs.SequenceEqual(folder.DotGlobs))
                {
                    stopped.Add(watch);
                    _watches.Remove(sourceId);
                    if (folder is null)
                    {
                        _pending.Remove(sourceId);
                        _reported.Remove(sourceId);
                    }
                }
            }

            foreach (SourceFolder folder in wanted.Values)
            {
                if (!_watches.ContainsKey(folder.SourceId))
                    started.Add(folder);
            }

            _reported.RemoveWhere(sourceId => !wanted.ContainsKey(sourceId));
        }

        // The watcher waits for its running handlers when it stops, and a handler takes the lock. So the call stays outside the lock.
        foreach (FolderWatch watch in stopped)
            watch.Dispose();

        foreach (SourceFolder folder in started)
            Start(folder);
    }

    /// <summary>
    /// Records a change of a file.
    /// </summary>
    /// <param name="sourceId">The key of the source.</param>
    /// <param name="relativePath">The path relative to the folder of the source.</param>
    public void Notify(int sourceId, string relativePath)
    {
        DateTimeOffset now = time.GetUtcNow();
        lock (_lock)
        {
            if (_disposed)
                return;

            if (_pending.TryGetValue(sourceId, out Pending? pending))
                pending.Last = now;
            else
                _pending[sourceId] = new Pending(now);
        }

        logger.LogTrace("The watcher saw a change of {Path} in the source {SourceId}.", relativePath, sourceId);
    }

    /// <summary>
    /// Records that the system lost events of a folder. The next flush asks for a run.
    /// </summary>
    /// <param name="sourceId">The key of the source.</param>
    public void Overflow(int sourceId)
    {
        DateTimeOffset now = time.GetUtcNow();
        lock (_lock)
        {
            if (_disposed)
                return;

            if (_pending.TryGetValue(sourceId, out Pending? pending))
            {
                pending.Last = now;
                pending.Immediate = true;
            }
            else
            {
                _pending[sourceId] = new Pending(now) { Immediate = true };
            }
        }
    }

    /// <summary>
    /// Asks the scheduler for a run of each source whose burst of changes has ended.
    /// </summary>
    /// <param name="quiet">The time without changes that ends a burst.</param>
    /// <param name="maxDelay">The longest time from the first change of a burst to the request.</param>
    /// <returns>The keys of the sources that the call put into the queue or that were in it already.</returns>
    public IReadOnlyList<int> Flush(TimeSpan quiet, TimeSpan maxDelay)
    {
        DateTimeOffset now = time.GetUtcNow();
        List<int> due = [];
        lock (_lock)
        {
            foreach ((int sourceId, Pending pending) in _pending)
            {
                if (pending.Immediate || now >= pending.Last + quiet || now >= pending.First + maxDelay)
                    due.Add(sourceId);
            }

            foreach (int sourceId in due)
                _pending.Remove(sourceId);
        }

        due.Sort();
        foreach (int sourceId in due)
        {
            if (scheduler.Enqueue(sourceId))
                logger.LogDebug("The watcher asked for a run over the source {SourceId}.", sourceId);
        }

        return due;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        List<FolderWatch> all;
        lock (_lock)
        {
            if (_disposed)
                return;

            _disposed = true;
            all = [.. _watches.Values];
            _watches.Clear();
            _pending.Clear();
        }

        foreach (FolderWatch watch in all)
            watch.Dispose();
    }

    private void Start(SourceFolder folder)
    {
        Matcher dots = new(StringComparison.OrdinalIgnoreCase);
        dots.AddIncludePatterns(folder.DotGlobs);
        FolderWatch? watch = null;
        try
        {
            watch = new FolderWatch(folder, dots, this);
            lock (_lock)
            {
                if (_disposed)
                {
                    watch.Dispose();
                    return;
                }

                _watches[folder.SourceId] = watch;
                _reported.Remove(folder.SourceId);
            }

            logger.LogInformation("The watcher follows the folder {Path} of the source {Source}.", folder.RootPath, folder.Name);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or PlatformNotSupportedException or InvalidOperationException)
        {
            watch?.Dispose();
            ReportOnce(folder, exception);
        }
    }

    private void ReportOnce(SourceFolder folder, Exception exception)
    {
        bool first;
        lock (_lock)
            first = _reported.Add(folder.SourceId);

        if (first)
            logger.LogWarning("The watcher cannot follow the folder {Path} of the source {Source}: {Reason} It tries again at the next refresh.", folder.RootPath, folder.Name, exception.Message);
    }

    private void OnPath(FolderWatch watch, string fullPath)
    {
        string relative = Path.GetRelativePath(watch.RootPath, fullPath);
        if (!IsNoise(relative, watch.Dots))
            Notify(watch.SourceId, relative);
    }

    private void OnError(FolderWatch watch, Exception exception)
    {
        if (exception is InternalBufferOverflowException)
        {
            logger.LogWarning("The system lost events of the folder {Path} of the source {Source}. The watcher asks for a full scan.", watch.RootPath, watch.Name);
            Overflow(watch.SourceId);
            return;
        }

        // Any other error, for example a deleted folder or a lost network drive, ends the watcher. The next refresh starts a new one if the folder is back.
        watch.Faulted = true;
        ReportOnce(new SourceFolder(watch.SourceId, watch.Name, watch.RootPath, watch.DotGlobs), exception);
        Overflow(watch.SourceId);
    }

    private sealed class Pending(DateTimeOffset first)
    {
        public DateTimeOffset First { get; } = first;

        public DateTimeOffset Last { get; set; } = first;

        public bool Immediate { get; set; }
    }

    private sealed class FolderWatch : IDisposable
    {
        private readonly FileSystemWatcher _watcher;

        public FolderWatch(SourceFolder folder, Matcher dots, SourceWatchSet owner)
        {
            SourceId = folder.SourceId;
            Name = folder.Name;
            RootPath = folder.RootPath;
            DotGlobs = folder.DotGlobs;
            Dots = dots;
            _watcher = new FileSystemWatcher(folder.RootPath)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                InternalBufferSize = BufferBytes,
            };
            _watcher.Changed += (_, e) => owner.OnPath(this, e.FullPath);
            _watcher.Created += (_, e) => owner.OnPath(this, e.FullPath);
            _watcher.Deleted += (_, e) => owner.OnPath(this, e.FullPath);
            _watcher.Renamed += (_, e) =>
            {
                owner.OnPath(this, e.OldFullPath);
                owner.OnPath(this, e.FullPath);
            };
            _watcher.Error += (_, e) => owner.OnError(this, e.GetException());
            _watcher.EnableRaisingEvents = true;
        }

        public int SourceId { get; }

        public string Name { get; }

        public string RootPath { get; }

        public IReadOnlyList<string> DotGlobs { get; }

        public Matcher Dots { get; }

        public volatile bool Faulted;

        public void Dispose() => _watcher.Dispose();
    }
}
