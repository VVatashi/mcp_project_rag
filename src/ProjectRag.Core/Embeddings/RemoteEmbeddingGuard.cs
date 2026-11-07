using System.Globalization;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ProjectRag.Core.Embeddings;

/// <summary>
/// The result of the identity check of the remote backend.
/// </summary>
/// <param name="Ok">A value that tells whether the backend gives the vectors of the stored model.</param>
/// <param name="Problem">The reason of a failed check, or <see langword="null"/>.</param>
/// <param name="CheckedAt">The time of the check.</param>
public sealed record RemoteCheck(bool Ok, string? Problem, DateTimeOffset CheckedAt);

/// <summary>
/// Checks that the remote backend gives the vectors of the stored embedding model.
/// </summary>
/// <remarks>
/// The guard embeds the stored probe text of the model through the backend and compares the vector with the stored probe vector.
/// It needs the same cosine as <see cref="EmbeddingModelRegistry"/>, <see cref="EmbeddingModelRegistry.MinProbeCosine"/>.
/// The indexer stores no remote vector while the check fails. Like the registry, the guard keeps the result for a minute.
/// </remarks>
/// <param name="client">The client of the remote backend.</param>
/// <param name="options">The options of the remote backend.</param>
/// <param name="time">The clock.</param>
/// <param name="logger">The logger.</param>
public sealed class RemoteEmbeddingGuard(IRemoteEmbeddingClient client, IOptionsMonitor<RemoteEmbeddingOptions> options, TimeProvider time, ILogger<RemoteEmbeddingGuard> logger) : IDisposable
{
    private static readonly TimeSpan CacheTime = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private Cached? _cached;

    /// <summary>
    /// Gets the last result of a check, or <see langword="null"/> before the first check.
    /// </summary>
    public RemoteCheck? Last => Volatile.Read(ref _cached)?.Check;

    /// <summary>
    /// Checks the backend against a stored model. The call sends a request when the last result is older than a minute or belongs to another model.
    /// </summary>
    /// <param name="model">The model.</param>
    /// <param name="probeText">The stored text of the probe of the model.</param>
    /// <param name="probeEmbedding">The stored vector of the probe.</param>
    /// <param name="cancellationToken">The token that cancels the check.</param>
    /// <returns>The result. A failed request gives a failed result instead of an exception.</returns>
    public async Task<RemoteCheck> CheckAsync(EmbeddingModel model, string probeText, float[] probeEmbedding, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(probeText);
        ArgumentNullException.ThrowIfNull(probeEmbedding);
        RemoteEmbeddingOptions settings = options.CurrentValue;
        string backend = $"{settings.BaseUrl} {settings.Model}";
        if (Fresh(model.ModelId, backend) is { } cached)
            return cached;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (Fresh(model.ModelId, backend) is { } fresh)
                return fresh;

            RemoteCheck check = await CheckCoreAsync(model, probeText, probeEmbedding, settings, cancellationToken);
            if (!check.Ok && check.Problem != Last?.Problem)
                logger.LogWarning("The remote embedding is not used: {Problem}", check.Problem);

            Volatile.Write(ref _cached, new Cached(model.ModelId, backend, check));
            return check;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc/>
    public void Dispose() => _gate.Dispose();

    // One immutable reference, so a reader outside the lock never sees a half-written result.
    private sealed record Cached(short ModelId, string Backend, RemoteCheck Check);

    private RemoteCheck? Fresh(short modelId, string backend) =>
        Volatile.Read(ref _cached) is { } cached && cached.ModelId == modelId && cached.Backend == backend && time.GetUtcNow() - cached.Check.CheckedAt < CacheTime ? cached.Check : null;

    private async Task<RemoteCheck> CheckCoreAsync(EmbeddingModel model, string probeText, float[] probeEmbedding, RemoteEmbeddingOptions settings, CancellationToken cancellationToken)
    {
        if (client.Unavailable is { } reason)
            return new RemoteCheck(false, reason, time.GetUtcNow());

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProbeTimeout);
        float[] probe;
        try
        {
            probe = (await client.EmbedAsync([model.FormatDocument(probeText)], timeout.Token)).Vectors[0];
        }
        catch (HttpRequestException exception)
        {
            return new RemoteCheck(false, $"The remote embedding {settings.BaseUrl} does not work: {exception.Message}", time.GetUtcNow());
        }
        catch (InvalidOperationException exception)
        {
            return new RemoteCheck(false, $"The remote embedding {settings.BaseUrl} does not work: {exception.Message}", time.GetUtcNow());
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new RemoteCheck(false, $"The remote embedding {settings.BaseUrl} did not answer in {ProbeTimeout.TotalSeconds:F0} seconds.", time.GetUtcNow());
        }

        double cosine = EmbeddingModelRegistry.Cosine(probeEmbedding, probe);
        if (cosine < EmbeddingModelRegistry.MinProbeCosine)
        {
            string detail = probe.Length == probeEmbedding.Length
                ? string.Create(CultureInfo.InvariantCulture, $"the cosine of the probe is {cosine:F4}")
                : $"it gives {probe.Length} dimensions, the stored model has {probeEmbedding.Length}";
            return new RemoteCheck(false, $"The remote model {settings.Model} gives other vectors than the stored model {model.Name}: {detail}.", time.GetUtcNow());
        }

        return new RemoteCheck(true, null, time.GetUtcNow());
    }
}
