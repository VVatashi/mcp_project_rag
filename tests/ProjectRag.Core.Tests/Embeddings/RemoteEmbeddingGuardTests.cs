using Microsoft.Extensions.Logging.Abstractions;

using ProjectRag.Core.Embeddings;

namespace ProjectRag.Core.Tests.Embeddings;

public sealed class RemoteEmbeddingGuardTests
{
    private static readonly EmbeddingModel Model = new(1, "stored", 4, "{text}");
    private static readonly float[] StoredProbe = [1, 0, 0, 0];

    [Fact]
    public async Task CheckAsync_PassesForTheSameVector()
    {
        FakeRemote remote = new([1, 0.01f, 0, 0]);
        RemoteEmbeddingGuard guard = Create(remote, new ManualTime());

        RemoteCheck check = await guard.CheckAsync(Model, "probe", StoredProbe, TestContext.Current.CancellationToken);

        Assert.True(check.Ok);
        Assert.Null(check.Problem);
        Assert.Equal(["probe"], remote.Texts);
        Assert.Same(check, guard.Last);
    }

    [Fact]
    public async Task CheckAsync_FailsForARotatedVector()
    {
        // A rotation by 0.06 rad gives the cosine 0.9982, below 0.999.
        FakeRemote remote = new([MathF.Cos(0.06f), MathF.Sin(0.06f), 0, 0]);
        RemoteEmbeddingGuard guard = Create(remote, new ManualTime());

        RemoteCheck check = await guard.CheckAsync(Model, "probe", StoredProbe, TestContext.Current.CancellationToken);

        Assert.False(check.Ok);
        Assert.Contains("0.998", check.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckAsync_FailsForOtherDimensions()
    {
        RemoteEmbeddingGuard guard = Create(new FakeRemote([1, 0, 0]), new ManualTime());

        RemoteCheck check = await guard.CheckAsync(Model, "probe", StoredProbe, TestContext.Current.CancellationToken);

        Assert.False(check.Ok);
        Assert.Contains("3 dimensions", check.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckAsync_FailsWithoutAKeyAndSendsNothing()
    {
        FakeRemote remote = new([1, 0, 0, 0]) { Unavailable = "The environment variable KEY holds no API key." };
        RemoteEmbeddingGuard guard = Create(remote, new ManualTime());

        RemoteCheck check = await guard.CheckAsync(Model, "probe", StoredProbe, TestContext.Current.CancellationToken);

        Assert.False(check.Ok);
        Assert.Equal(remote.Unavailable, check.Problem);
        Assert.Equal(0, remote.Calls);
    }

    [Fact]
    public async Task CheckAsync_FailsWhenTheApiFails()
    {
        FakeRemote remote = new([1, 0, 0, 0]) { Failure = new HttpRequestException("busy") };
        RemoteEmbeddingGuard guard = Create(remote, new ManualTime());

        RemoteCheck check = await guard.CheckAsync(Model, "probe", StoredProbe, TestContext.Current.CancellationToken);

        Assert.False(check.Ok);
        Assert.Contains("busy", check.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckAsync_FailsWhenTheKeyDisappearsDuringTheCheck()
    {
        FakeRemote remote = new([1, 0, 0, 0]) { Failure = new InvalidOperationException("The environment variable KEY holds no API key.") };
        RemoteEmbeddingGuard guard = Create(remote, new ManualTime());

        RemoteCheck check = await guard.CheckAsync(Model, "probe", StoredProbe, TestContext.Current.CancellationToken);

        Assert.False(check.Ok);
        Assert.Contains("holds no API key", check.Problem, StringComparison.Ordinal);
        Assert.Same(check, guard.Last);
    }

    [Fact]
    public async Task CheckAsync_KeepsTheAnswerForAMinute()
    {
        ManualTime time = new();
        FakeRemote remote = new([1, 0, 0, 0]);
        RemoteEmbeddingGuard guard = Create(remote, time);
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        await guard.CheckAsync(Model, "probe", StoredProbe, cancellationToken);
        time.Advance(TimeSpan.FromSeconds(59));
        await guard.CheckAsync(Model, "probe", StoredProbe, cancellationToken);
        Assert.Equal(1, remote.Calls);

        time.Advance(TimeSpan.FromSeconds(2));
        await guard.CheckAsync(Model, "probe", StoredProbe, cancellationToken);
        Assert.Equal(2, remote.Calls);
    }

    [Fact]
    public async Task CheckAsync_KeepsAFailedAnswerForAMinuteToo()
    {
        ManualTime time = new();
        FakeRemote remote = new([0, 1, 0, 0]);
        RemoteEmbeddingGuard guard = Create(remote, time);
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Assert.False((await guard.CheckAsync(Model, "probe", StoredProbe, cancellationToken)).Ok);
        remote.Vector = [1, 0, 0, 0];
        time.Advance(TimeSpan.FromSeconds(30));
        Assert.False((await guard.CheckAsync(Model, "probe", StoredProbe, cancellationToken)).Ok);
        time.Advance(TimeSpan.FromSeconds(31));
        Assert.True((await guard.CheckAsync(Model, "probe", StoredProbe, cancellationToken)).Ok);
    }

    [Fact]
    public async Task CheckAsync_ChecksAnotherModelAgain()
    {
        FakeRemote remote = new([1, 0, 0, 0]);
        RemoteEmbeddingGuard guard = Create(remote, new ManualTime());
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        await guard.CheckAsync(Model, "probe", StoredProbe, cancellationToken);
        await guard.CheckAsync(Model with { ModelId = 2 }, "probe", StoredProbe, cancellationToken);

        Assert.Equal(2, remote.Calls);
    }

    [Fact]
    public async Task CheckAsync_AppliesTheTemplateOfTheModel()
    {
        FakeRemote remote = new([1, 0, 0, 0]);
        RemoteEmbeddingGuard guard = Create(remote, new ManualTime());

        await guard.CheckAsync(new EmbeddingModel(1, "stored", 4, "doc: {text}"), "probe", StoredProbe, TestContext.Current.CancellationToken);

        Assert.Equal(["doc: probe"], remote.Texts);
    }

    private static RemoteEmbeddingGuard Create(FakeRemote remote, TimeProvider time) =>
        new(remote, new FixedOptions<RemoteEmbeddingOptions>(new RemoteEmbeddingOptions { Enabled = true }), time, NullLogger<RemoteEmbeddingGuard>.Instance);

    private sealed class FakeRemote(float[] vector) : IRemoteEmbeddingClient
    {
        public float[] Vector { get; set; } = vector;

        public string? Unavailable { get; init; }

        public Exception? Failure { get; init; }

        public int Calls { get; private set; }

        public List<string> Texts { get; } = [];

        public Task<RemoteEmbedding> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken)
        {
            Calls++;
            Texts.AddRange(texts);
            return Failure is not null ? throw Failure : Task.FromResult(new RemoteEmbedding([.. texts.Select(_ => Vector)], 5, 0));
        }
    }

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan time) => _now += time;
    }
}
