using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

using Microsoft.Extensions.Options;

using ProjectRag.Core.Llama;

namespace ProjectRag.Core.Embeddings;

/// <summary>
/// The vectors of one request to the remote backend, with the usage that the API reports.
/// </summary>
/// <param name="Vectors">A normalized vector for each text, in the order of the texts.</param>
/// <param name="Tokens">The tokens of the request. The client estimates them from the characters when the API reports none.</param>
/// <param name="Cost">The cost of the request in USD: the larger of the reported cost and the tokens times the price.</param>
public sealed record RemoteEmbedding(IReadOnlyList<float[]> Vectors, long Tokens, decimal Cost);

/// <summary>
/// The API answered with the status 2xx, but the answer is not usable. The client never repeats such a request, because the API already billed it.
/// </summary>
public sealed class RemoteEmbeddingException : HttpRequestException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="RemoteEmbeddingException"/> class.
    /// </summary>
    /// <param name="message">The message.</param>
    /// <param name="tokens">The tokens of the answer, or 0 when the body could not be read.</param>
    /// <param name="cost">The cost of the answer in USD, or 0 when the body could not be read.</param>
    /// <param name="inner">The exception that caused this one, or <see langword="null"/>.</param>
    public RemoteEmbeddingException(string message, long tokens, decimal cost, Exception? inner = null)
        : base(message, inner)
    {
        Tokens = tokens;
        Cost = cost;
    }

    /// <summary>
    /// Gets the tokens of the answer, or 0 when the body could not be read.
    /// </summary>
    public long Tokens { get; }

    /// <summary>
    /// Gets the cost of the answer in USD, or 0 when the body could not be read.
    /// </summary>
    public decimal Cost { get; }
}

/// <summary>
/// A client of the remote embedding backend.
/// </summary>
public interface IRemoteEmbeddingClient
{
    /// <summary>
    /// Gets the reason why the backend cannot work now, or <see langword="null"/> when it is enabled and has an API key.
    /// </summary>
    string? Unavailable { get; }

    /// <summary>
    /// Embeds the texts of one batch in one request. The caller applies the templates and splits the batches.
    /// </summary>
    /// <param name="texts">The texts.</param>
    /// <param name="cancellationToken">The token that cancels the request.</param>
    /// <returns>The vectors and the usage.</returns>
    /// <exception cref="InvalidOperationException">The backend is unavailable, see <see cref="Unavailable"/>.</exception>
    /// <exception cref="HttpRequestException">The API does not answer or answers with an error after the last retry.</exception>
    Task<RemoteEmbedding> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken);
}

/// <summary>
/// The client of the endpoint <c>v1/embeddings</c> of an API of the OpenAI kind, for example OpenRouter.
/// </summary>
/// <remarks>
/// A call sends one request. It repeats the request after the status 429 or 5xx and after a network error, at most <see cref="RemoteEmbeddingOptions.Retries"/> times.
/// The pause doubles from one second and follows the header <c>Retry-After</c> when the API sends it.
/// Like the local client, it normalizes the vectors. The key never leaves the header and never goes into a message.
/// </remarks>
/// <param name="clients">The factory of the HTTP clients.</param>
/// <param name="options">The options of the backend.</param>
public sealed partial class RemoteEmbeddingClient(IHttpClientFactory clients, IOptionsMonitor<RemoteEmbeddingOptions> options) : IRemoteEmbeddingClient
{
    /// <summary>
    /// The name of the HTTP client in the factory.
    /// </summary>
    public const string HttpClientName = "remote-embedding";

    private static readonly TimeSpan MaxPause = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Gets the first pause between two attempts. The tests set it to zero.
    /// </summary>
    internal TimeSpan BackoffBase { get; init; } = TimeSpan.FromSeconds(1);

    /// <inheritdoc/>
    public string? Unavailable
    {
        get
        {
            RemoteEmbeddingOptions settings = options.CurrentValue;
            if (!settings.Enabled)
                return "The remote embedding is off (Embedding:Remote:Enabled).";

            return string.IsNullOrWhiteSpace(settings.ApiKeyVariable) || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(settings.ApiKeyVariable))
                ? $"The environment variable {settings.ApiKeyVariable} holds no API key."
                : null;
        }
    }

    /// <inheritdoc/>
    public async Task<RemoteEmbedding> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(texts);
        RemoteEmbeddingOptions settings = options.CurrentValue;
        if (Unavailable is { } reason)
            throw new InvalidOperationException(reason);

        if (texts.Count == 0)
            return new RemoteEmbedding([], 0, 0);

        string key = Environment.GetEnvironmentVariable(settings.ApiKeyVariable)!.Trim();
        HttpClient http = clients.CreateClient(HttpClientName);
        Uri endpoint = LlamaHttp.Endpoint(settings.BaseUrl, "v1/embeddings");
        string[] input = [.. texts];
        int retries = Math.Max(0, settings.Retries);
        for (int attempt = 0; ; attempt++)
        {
            TimeSpan? retryAfter = null;
            HttpRequestException failure;
            try
            {
                using HttpRequestMessage request = new(HttpMethod.Post, endpoint)
                {
                    Content = JsonContent.Create(new EmbeddingRequest(settings.Model, input), options: LlamaHttp.Json),
                };
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                using HttpResponseMessage response = await http.SendAsync(request, cancellationToken);
                if (response.IsSuccessStatusCode)
                    return await ReadAsync(response, endpoint, input, settings, key, cancellationToken);

                string body = await response.Content.ReadAsStringAsync(cancellationToken);
                failure = new HttpRequestException($"The server {endpoint} answered {(int)response.StatusCode}: {Shorten(body, key)}", null, response.StatusCode);
                if (response.StatusCode != HttpStatusCode.TooManyRequests && (int)response.StatusCode < 500)
                    throw failure;

                retryAfter = ReadRetryAfter(response);
            }
            catch (HttpRequestException exception) when (exception.StatusCode is null && exception is not RemoteEmbeddingException)
            {
                failure = exception;
            }
            catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                failure = new HttpRequestException($"The server {endpoint} did not answer in time.", exception);
            }

            if (attempt >= retries)
                throw failure;

            TimeSpan pause = retryAfter ?? TimeSpan.FromTicks(BackoffBase.Ticks << Math.Min(attempt, 10));
            if (pause > TimeSpan.Zero)
                await Task.Delay(pause > MaxPause ? MaxPause : pause, cancellationToken);
        }
    }

    private static async Task<RemoteEmbedding> ReadAsync(HttpResponseMessage response, Uri endpoint, string[] input, RemoteEmbeddingOptions settings, string key, CancellationToken cancellationToken)
    {
        int count = input.Length;
        EmbeddingResponse? body;
        try
        {
            body = await response.Content.ReadFromJsonAsync<EmbeddingResponse>(LlamaHttp.Json, cancellationToken);
        }
        catch (JsonException exception)
        {
            throw new RemoteEmbeddingException($"The server {endpoint} sent an answer that is not the expected JSON: {Shorten(exception.Message, key)}", 0, 0, exception);
        }

        (long tokens, decimal cost) = Usage(body?.Usage, input, settings);
        if (body?.Data is not { } data || data.Length != count)
            throw new RemoteEmbeddingException($"The server {endpoint} sent {body?.Data?.Length ?? 0} vectors for {count} texts.", tokens, cost);

        float[][] vectors = new float[count][];
        for (int position = 0; position < data.Length; position++)
        {
            int index = data[position].Index ?? position;
            if (index < 0 || index >= count || vectors[index] is not null || data[position].Embedding is not { Length: > 0 } vector)
                throw new RemoteEmbeddingException($"The server {endpoint} sent a vector with the wrong index {index}.", tokens, cost);

            vectors[index] = Normalize(vector);
        }

        return new RemoteEmbedding(vectors, tokens, cost);
    }

    private static (long Tokens, decimal Cost) Usage(EmbeddingUsage? usage, string[] input, RemoteEmbeddingOptions settings)
    {
        long tokens = usage?.PromptTokens is > 0 ? usage.PromptTokens.Value : (input.Sum(static text => (long)text.Length) + 2) / 3;
        decimal priced = tokens * Math.Max(0, settings.PricePerMillionTokens) / 1_000_000m;
        return (tokens, Math.Max(usage?.Cost ?? 0, priced));
    }

    private static TimeSpan? ReadRetryAfter(HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter is not { } header)
            return null;

        if (header.Delta is { } delta)
            return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;

        return header.Date is { } date ? TimeSpan.FromSeconds(Math.Max(0, (date - DateTimeOffset.UtcNow).TotalSeconds)) : null;
    }

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

    [GeneratedRegex(@"sk-[A-Za-z0-9_-]{8,}|Bearer\s+\S+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex KeyLike();

    // Redacts before it shortens, so a cut cannot leave a part of a key.
    private static string Shorten(string text, string key)
    {
        if (key.Length >= 8)
            text = text.Replace(key, "***", StringComparison.Ordinal);

        text = KeyLike().Replace(text, "***");
        return text.Length <= 500 ? text : string.Concat(text.AsSpan(0, 500), "…");
    }

    private sealed record EmbeddingRequest(string Model, string[] Input);

    private sealed record EmbeddingResponse(EmbeddingItem[]? Data, EmbeddingUsage? Usage);

    private sealed record EmbeddingItem(int? Index, float[]? Embedding);

    private sealed record EmbeddingUsage([property: JsonPropertyName("prompt_tokens")] long? PromptTokens, decimal? Cost);
}
