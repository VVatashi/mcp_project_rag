using Microsoft.Extensions.Options;

using ProjectRag.Core.Llama;

namespace ProjectRag.Core.Embeddings;

/// <summary>
/// The client of the endpoint <c>/v1/embeddings</c> of llama-server.
/// </summary>
/// <remarks>
/// The client splits the texts into batches of <see cref="EmbeddingOptions.BatchSize"/> and keeps <see cref="EmbeddingOptions.Parallelism"/> requests in flight.
/// llama-server normalizes the vectors itself (<c>--embd-normalize 2</c>). The client normalizes them again, so a server with other options gives the same vectors.
/// </remarks>
/// <param name="clients">The factory of the HTTP clients.</param>
/// <param name="options">The options of the server.</param>
public sealed class LlamaEmbeddingClient(IHttpClientFactory clients, IOptionsMonitor<EmbeddingOptions> options) : IEmbeddingClient
{
    /// <summary>
    /// The name of the HTTP client in the factory.
    /// </summary>
    public const string HttpClientName = "llama-embedding";

    /// <inheritdoc/>
    public async Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(texts);
        EmbeddingOptions settings = options.CurrentValue;
        float[][] vectors = new float[texts.Count][];
        if (texts.Count == 0)
            return vectors;

        HttpClient http = clients.CreateClient(HttpClientName);
        Uri endpoint = LlamaHttp.Endpoint(settings.BaseUrl, "v1/embeddings");
        int batchSize = Math.Max(1, settings.BatchSize);
        IEnumerable<int> starts = Enumerable.Range(0, (texts.Count + batchSize - 1) / batchSize).Select(batch => batch * batchSize);
        ParallelOptions parallel = new() { MaxDegreeOfParallelism = Math.Max(1, settings.Parallelism), CancellationToken = cancellationToken };
        await Parallel.ForEachAsync(starts, parallel, async (start, token) =>
        {
            string[] batch = [.. texts.Skip(start).Take(batchSize)];
            EmbeddingResponse response = await LlamaHttp.PostAsync<EmbeddingResponse>(http, endpoint, new EmbeddingRequest(batch), token);
            if (response.Data is not { } data || data.Length != batch.Length)
                throw new HttpRequestException($"The server {endpoint} sent {response.Data?.Length ?? 0} vectors for {batch.Length} texts.");

            foreach (EmbeddingItem item in data)
            {
                if (item.Index < 0 || item.Index >= batch.Length || item.Embedding is not { Length: > 0 } vector)
                    throw new HttpRequestException($"The server {endpoint} sent a vector with the wrong index {item.Index}.");

                vectors[start + item.Index] = Normalize(vector);
            }
        });

        return vectors;
    }

    /// <inheritdoc/>
    public Task<bool> IsHealthyAsync(CancellationToken cancellationToken) => LlamaHttp.IsHealthyAsync(clients.CreateClient(HttpClientName), options.CurrentValue.BaseUrl, cancellationToken);

    private static float[] Normalize(float[] vector)
    {
        double sum = 0;
        foreach (float value in vector)
            sum += value * value;

        if (sum <= 0)
            return vector;

        float scale = (float)(1 / Math.Sqrt(sum));
        for (int i = 0; i < vector.Length; i++)
            vector[i] *= scale;

        return vector;
    }

    private sealed record EmbeddingRequest(string[] Input);

    private sealed record EmbeddingResponse(EmbeddingItem[]? Data);

    private sealed record EmbeddingItem(int Index, float[]? Embedding);
}
