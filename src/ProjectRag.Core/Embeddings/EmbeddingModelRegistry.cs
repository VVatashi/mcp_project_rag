using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ProjectRag.Core.Embeddings;

/// <summary>
/// Knows the current embedding model: the model behind the server that the database also knows.
/// </summary>
/// <remarks>
/// <para>
/// The registry embeds the probe text and looks for the model by its name, its dimensions and its templates.
/// A known model must give the stored vector of the probe: a cosine below 0.999 means other weights, another quantization or another pooling under the same name.
/// The search then works with the full text search only, and the indexer writes no vectors. An unknown model goes into the database with its own HNSW index.
/// </para>
/// <para>
/// The registry keeps the answer for a minute, so a restarted server comes back without a restart of the application.
/// </para>
/// </remarks>
/// <param name="client">The client of the embedding server.</param>
/// <param name="store">The store of the models.</param>
/// <param name="options">The options of the embedding model.</param>
/// <param name="time">The clock.</param>
/// <param name="logger">The logger.</param>
public sealed class EmbeddingModelRegistry(IEmbeddingClient client, EmbeddingStore store, IOptionsMonitor<EmbeddingOptions> options, TimeProvider time, ILogger<EmbeddingModelRegistry> logger) : IDisposable
{
    /// <summary>The lowest cosine between the stored and the new vector of the probe.</summary>
    public const double MinProbeCosine = 0.999;

    private static readonly TimeSpan CacheTime = TimeSpan.FromMinutes(1);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private EmbeddingModelStatus? _status;
    private DateTimeOffset _checkedAt;

    /// <summary>
    /// Gets the state of the current model. The call embeds the probe when the last check is older than a minute.
    /// </summary>
    /// <param name="cancellationToken">The token that cancels the check.</param>
    /// <returns>The model, or the reason why it is not ready.</returns>
    public async Task<EmbeddingModelStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        if (_status is { } cached && time.GetUtcNow() - _checkedAt < CacheTime)
            return cached;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_status is { } fresh && time.GetUtcNow() - _checkedAt < CacheTime)
                return fresh;

            EmbeddingModelStatus status = await CheckAsync(cancellationToken);
            if (status.Problem is not null && status.Problem != _status?.Problem)
                logger.LogWarning("The embedding model is not ready: {Problem}", status.Problem);

            (_status, _checkedAt) = (status, time.GetUtcNow());
            return status;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Forgets the last check, for example after a request to the server failed.
    /// </summary>
    public void Invalidate() => _status = null;

    /// <inheritdoc/>
    public void Dispose() => _gate.Dispose();

    private async Task<EmbeddingModelStatus> CheckAsync(CancellationToken cancellationToken)
    {
        EmbeddingOptions settings = options.CurrentValue;
        float[] probe;
        try
        {
            string text = settings.DocumentTemplate.Replace(EmbeddingOptions.TextPlaceholder, settings.ProbeText, StringComparison.Ordinal);
            probe = (await client.EmbedAsync([text], cancellationToken))[0];
        }
        catch (HttpRequestException exception)
        {
            return new EmbeddingModelStatus(null, $"The embedding server {settings.BaseUrl} does not work: {exception.Message}");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new EmbeddingModelStatus(null, $"The embedding server {settings.BaseUrl} did not answer in time.");
        }

        if (probe.Length > 4000)
            return new EmbeddingModelStatus(null, $"The model {settings.Model} gives {probe.Length} dimensions. An HNSW index over halfvec takes at most 4000.");

        if (await store.FindModelAsync(settings.Model, probe.Length, settings.QueryTemplate, settings.DocumentTemplate, cancellationToken) is { } known)
        {
            double cosine = Cosine(known.ProbeEmbedding, probe);
            if (cosine < MinProbeCosine)
            {
                return new EmbeddingModelStatus(null,
                    $"The server gives other vectors than the stored model {settings.Model}: the cosine of the probe is {cosine:F4}. " +
                    "Check the weights, the quantization and the pooling of the server, or give the model a new name.");
            }

            return new EmbeddingModelStatus(new EmbeddingModel(known.ModelId, settings.Model, probe.Length, settings.QueryTemplate, settings.DocumentTemplate), null);
        }

        short modelId = await store.AddModelAsync(settings.Model, settings.QueryTemplate, settings.DocumentTemplate, settings.ProbeText, probe, cancellationToken);
        logger.LogInformation("The database knows a new embedding model {Model} with {Dims} dimensions as model {ModelId}.", settings.Model, probe.Length, modelId);
        return new EmbeddingModelStatus(new EmbeddingModel(modelId, settings.Model, probe.Length, settings.QueryTemplate, settings.DocumentTemplate), null);
    }

    private static double Cosine(float[] a, float[] b)
    {
        if (a.Length != b.Length)
            return 0;

        double dot = 0;
        double normA = 0;
        double normB = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }

        return normA <= 0 || normB <= 0 ? 0 : dot / Math.Sqrt(normA * normB);
    }
}
