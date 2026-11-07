namespace ProjectRag.Core.Embeddings;

/// <summary>
/// The options of the remote embedding backend, an API of the OpenAI kind such as OpenRouter.
/// </summary>
/// <remarks>
/// The indexer uses the backend next to the local server, for the sources in <see cref="Sources"/> only.
/// Its vectors join the stored model when the probe of the model passes, see <see cref="RemoteEmbeddingGuard"/>. The search never calls it.
/// </remarks>
public sealed class RemoteEmbeddingOptions
{
    /// <summary>
    /// The configuration section that holds these options.
    /// </summary>
    public const string SectionName = "Embedding:Remote";

    /// <summary>
    /// Gets or sets a value that tells whether the indexer may use the backend.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Gets or sets the address of the API. The client adds <c>v1/embeddings</c>.
    /// </summary>
    public Uri BaseUrl { get; set; } = new("https://openrouter.ai/api/");

    /// <summary>
    /// Gets or sets the name of the model at the API. It may differ from <see cref="EmbeddingOptions.Model"/>, which names the stored vectors.
    /// </summary>
    public string Model { get; set; } = "qwen/qwen3-embedding-4b";

    /// <summary>
    /// Gets or sets the name of the environment variable that holds the API key. The client reads it at each use and never logs it.
    /// </summary>
    public string ApiKeyVariable { get; set; } = "OPENROUTER_API_KEY";

    /// <summary>
    /// Gets or sets the names of the sources that may use the backend. An empty list means no source.
    /// </summary>
    public List<string> Sources { get; set; } = [];

    /// <summary>
    /// Gets or sets the number of requests in flight.
    /// </summary>
    public int Parallelism { get; set; } = 4;

    /// <summary>
    /// Gets or sets the number of texts in one request.
    /// </summary>
    public int BatchSize { get; set; } = 32;

    /// <summary>
    /// Gets or sets the time limit of one request in seconds.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 120;

    /// <summary>
    /// Gets or sets the cost in USD at which the remote workers of a run stop. The batches in flight finish, so the run may pass the limit a little.
    /// </summary>
    public decimal MaxCostPerRun { get; set; } = 1.0m;

    /// <summary>
    /// Gets or sets the price in USD of one million tokens. The cost of an answer is the larger of the cost that the API reports and the tokens times this price.
    /// </summary>
    public decimal PricePerMillionTokens { get; set; } = 0.02m;

    /// <summary>
    /// Gets or sets the number of repeats of a request after the status 429 or 5xx, or after a network error.
    /// </summary>
    public int Retries { get; set; } = 2;

    /// <summary>
    /// Tells whether a source may use the backend.
    /// </summary>
    /// <param name="source">The name of the source.</param>
    /// <returns><see langword="true"/> when the backend is enabled and the list holds the source.</returns>
    public bool Allows(string source) => Enabled && Sources.Contains(source, StringComparer.OrdinalIgnoreCase);
}
