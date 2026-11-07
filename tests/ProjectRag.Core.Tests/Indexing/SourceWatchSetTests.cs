using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.Logging.Abstractions;

using ProjectRag.Core.Indexing;
using ProjectRag.Core.Sources;

namespace ProjectRag.Core.Tests.Indexing;

public sealed class SourceWatchSetTests : IDisposable
{
    private static readonly TimeSpan Quiet = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(30);

    private readonly ManualTime _time = new();
    private readonly IndexScheduler _scheduler = new();
    private readonly SourceWatchSet _set;
    private readonly string _root = Directory.CreateTempSubdirectory("project-rag-watch-").FullName;

    public SourceWatchSetTests() => _set = new SourceWatchSet(_scheduler, _time, NullLogger.Instance);

    public void Dispose()
    {
        _set.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void Flush_WaitsForTheQuietTimeAfterTheLastChange()
    {
        _set.Notify(1, "a.cs");
        Assert.Empty(_set.Flush(Quiet, MaxDelay));

        _time.Advance(TimeSpan.FromSeconds(1.5));
        Assert.Empty(_set.Flush(Quiet, MaxDelay));

        _time.Advance(TimeSpan.FromSeconds(0.5));
        Assert.Equal([1], _set.Flush(Quiet, MaxDelay));
        Assert.Equal([1], _scheduler.Queued);
        Assert.Empty(_set.Waiting);
    }

    [Fact]
    public void Flush_ResetsTheQuietTimeOnEachChangeOfABurst()
    {
        for (int i = 0; i < 5; i++)
        {
            _set.Notify(1, $"{i}.cs");
            _time.Advance(TimeSpan.FromSeconds(1.5));
            Assert.Empty(_set.Flush(Quiet, MaxDelay));
        }

        _time.Advance(TimeSpan.FromSeconds(0.5));
        Assert.Equal([1], _set.Flush(Quiet, MaxDelay));
        Assert.Equal([1], _scheduler.Queued);
    }

    [Fact]
    public void Flush_AsksForARunAtTheMaximumDelayOfAnEndlessBurst()
    {
        DateTimeOffset start = _time.GetUtcNow();
        List<int> asked = [];
        for (int second = 0; second < 40 && asked.Count == 0; second++)
        {
            _set.Notify(1, "a.cs");
            _time.Advance(TimeSpan.FromSeconds(1));
            asked.AddRange(_set.Flush(Quiet, MaxDelay));
        }

        Assert.Equal([1], asked);
        Assert.Equal(MaxDelay, _time.GetUtcNow() - start);
    }

    [Fact]
    public void Flush_StartsANewBurstAfterTheRequest()
    {
        _set.Notify(1, "a.cs");
        _time.Advance(Quiet);
        Assert.Equal([1], _set.Flush(Quiet, MaxDelay));
        Assert.Empty(_set.Flush(Quiet, MaxDelay));

        _set.Notify(1, "b.cs");
        Assert.Equal([1], _set.Waiting);
        Assert.Empty(_set.Flush(Quiet, MaxDelay));
    }

    [Fact]
    public void Flush_AsksAtOnceAfterAnOverflowAndKeepsASourceOnceInTheQueue()
    {
        _set.Overflow(4);
        Assert.Equal([4], _set.Flush(Quiet, MaxDelay));
        Assert.Equal([4], _scheduler.Queued);

        _set.Overflow(4);
        Assert.Equal([4], _set.Flush(Quiet, MaxDelay));
        Assert.Equal([4], _scheduler.Queued);
    }

    [Fact]
    public void Flush_TurnsAWaitingBurstIntoAnImmediateRequestOnOverflow()
    {
        _set.Notify(2, "a.cs");
        Assert.Empty(_set.Flush(Quiet, MaxDelay));

        _set.Overflow(2);

        Assert.Equal([2], _set.Flush(Quiet, MaxDelay));
    }

    [Fact]
    public void Flush_TimesEachSourceOnItsOwn()
    {
        _set.Notify(1, "a.cs");
        _time.Advance(TimeSpan.FromSeconds(1));
        _set.Notify(2, "b.cs");
        _time.Advance(TimeSpan.FromSeconds(1));
        _set.Overflow(3);

        Assert.Equal([1, 3], _set.Flush(Quiet, MaxDelay));
        Assert.Equal([2], _set.Waiting);

        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal([2], _set.Flush(Quiet, MaxDelay));
        Assert.Equal([1, 2, 3], _scheduler.Queued.Order());
    }

    [Theory]
    [InlineData(".git/index", true)]
    [InlineData("src/.git/HEAD", true)]
    [InlineData("src\\bin\\Debug\\a.dll", true)]
    [InlineData("obj/project.assets.json", true)]
    [InlineData("web/node_modules/pkg/index.js", true)]
    [InlineData("artifacts/out.txt", true)]
    [InlineData("BIN/a.dll", true)]
    [InlineData(".hidden/readme.md", true)]
    [InlineData(".editorconfig", true)]
    [InlineData("src/Program.cs", false)]
    [InlineData("docs/binary.md", false)]
    [InlineData("src/objects/a.cs", false)]
    public void IsNoise_DropsTheFoldersThatTheFileListSkips(string path, bool expected) =>
        Assert.Equal(expected, SourceWatchSet.IsNoise(path, null));

    [Fact]
    public void IsNoise_KeepsADotPathThatAPatternBringsBack()
    {
        Matcher dots = new(StringComparison.OrdinalIgnoreCase);
        dots.AddIncludePatterns([".github/**", ".editorconfig"]);

        Assert.False(SourceWatchSet.IsNoise(".github/workflows/ci.yml", dots));
        Assert.False(SourceWatchSet.IsNoise(".editorconfig", dots));
        Assert.True(SourceWatchSet.IsNoise(".idea/workspace.xml", dots));
        Assert.True(SourceWatchSet.IsNoise(".git/config", dots));
    }

    [Fact]
    public void Sync_SkipsMissingFoldersAndStopsWatchersOfGoneSources()
    {
        string other = Directory.CreateTempSubdirectory("project-rag-watch-other-").FullName;
        try
        {
            _set.Sync([new SourceFolder(1, "a", _root, []), new SourceFolder(2, "b", other, []), new SourceFolder(3, "c", Path.Combine(_root, "missing"), []), new SourceFolder(4, "d", "", [])]);
            Assert.Equal(2, _set.WatchCount);

            _set.Sync([new SourceFolder(1, "a", _root, [])]);
            Assert.Equal(1, _set.WatchCount);

            _set.Sync([new SourceFolder(1, "a", other, [])]);
            Assert.Equal(1, _set.WatchCount);
        }
        finally
        {
            Directory.Delete(other, recursive: true);
        }
    }

    [Fact]
    public void Watcher_AsksForARunAfterAChangeInARealFolder()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        Directory.CreateDirectory(Path.Combine(_root, "bin"));
        TimeSpan quiet = TimeSpan.FromMilliseconds(100);
        using SourceWatchSet set = new(_scheduler, TimeProvider.System, NullLogger.Instance);
        set.Sync([new SourceFolder(7, "real", _root, [])]);
        Assert.Equal(1, set.WatchCount);

        File.WriteAllText(Path.Combine(_root, "bin", "noise.dll"), "x");
        Thread.Sleep(300);
        Assert.Empty(set.Waiting);

        File.WriteAllText(Path.Combine(_root, "src", "A.cs"), "class A;");
        Assert.True(SpinWait.SpinUntil(() => set.Waiting.Count == 1, TimeSpan.FromSeconds(10)), "The watcher saw no event.");
        Assert.True(SpinWait.SpinUntil(() =>
        {
            set.Flush(quiet, TimeSpan.FromSeconds(5));
            return _scheduler.Queued.Contains(7);
        }, TimeSpan.FromSeconds(10)), "The source did not reach the queue.");
        Assert.Equal([7], _scheduler.Queued);
    }

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan span) => _now += span;
    }
}
