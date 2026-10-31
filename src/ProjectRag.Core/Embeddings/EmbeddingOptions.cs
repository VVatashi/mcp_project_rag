namespace ProjectRag.Core.Embeddings;

/// <summary>
/// The options of the llama.cpp server of the embedding model.
/// </summary>
/// <remarks>
/// The defaults follow the project rag (ADR 0046 and 0052 there): Qwen3-Embedding-4B in Q8_0, pooling <c>last</c>, 2560 dimensions.
/// Qwen3-Embedding is asymmetric. The query gets an instruction in English, and a chunk goes without one.
/// </remarks>
public sealed class EmbeddingOptions
{
    /// <summary>
    /// The configuration section that holds these options.
    /// </summary>
    public const string SectionName = "Embedding";

    /// <summary>
    /// The placeholder of the text in <see cref="QueryTemplate"/> and <see cref="DocumentTemplate"/>.
    /// </summary>
    public const string TextPlaceholder = "{text}";

    /// <summary>
    /// Gets or sets the address of the server, for example <c>http://127.0.0.1:8195</c>.
    /// </summary>
    public Uri BaseUrl { get; set; } = new("http://127.0.0.1:8195");

    /// <summary>
    /// Gets or sets the name of the model. The name is a part of the identity of the stored vectors, so a new name starts a new set of vectors.
    /// </summary>
    public string Model { get; set; } = "Qwen3-Embedding-4B-Q8_0";

    /// <summary>
    /// Gets or sets the template of a query. <see cref="TextPlaceholder"/> marks the place of the query.
    /// </summary>
    public string QueryTemplate { get; set; } = "Instruct: Given a question about a software project, retrieve passages of its documentation that answer the question\nQuery:{text}";

    /// <summary>
    /// Gets or sets the template of a chunk. <see cref="TextPlaceholder"/> marks the place of the chunk.
    /// </summary>
    public string DocumentTemplate { get; set; } = TextPlaceholder;

    /// <summary>
    /// Gets or sets the text of the probe. The application embeds it and compares the vector with the stored one, so it notices another model behind the same name.
    /// </summary>
    public string ProbeText { get; set; } = "The probe of the knowledge base: a vertex shader reads the attributes of a vertex. Проба базы знаний.";

    /// <summary>
    /// Gets or sets the number of texts in one request.
    /// </summary>
    /// <remarks>
    /// The server puts each text of a request into its queue and runs as many texts at once as it has slots (option <c>-np</c>).
    /// The query of a search waits behind every text in the queue. So the indexer keeps at most <see cref="BatchSize"/> × <see cref="Parallelism"/> texts in flight,
    /// twice the four slots of the server: the slots stay busy, and a query waits one round of about a second.
    /// </remarks>
    public int BatchSize { get; set; } = 4;

    /// <summary>
    /// Gets or sets the number of requests in flight.
    /// </summary>
    public int Parallelism { get; set; } = 2;

    /// <summary>
    /// Gets or sets the time limit of one request in seconds.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 300;
}
