namespace ProjectRag.Core.Reranking;

/// <summary>
/// The options of the llama.cpp server of the reranker.
/// </summary>
/// <remarks>
/// The default model is Qwen3-Reranker-0.6B in the GGUF of ggml-org. That file holds the classifier tensor and the template of the reranker.
/// Many other GGUF files of this model lack them and give scores near zero (ADR 0047 of the project rag).
/// </remarks>
public sealed class RerankOptions
{
    /// <summary>
    /// The configuration section that holds these options.
    /// </summary>
    public const string SectionName = "Rerank";

    /// <summary>
    /// Gets or sets a value that tells whether the search reorders its candidates with the reranker.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Gets or sets the address of the server, for example <c>http://127.0.0.1:8196</c>.
    /// </summary>
    public Uri BaseUrl { get; set; } = new("http://127.0.0.1:8196");

    /// <summary>
    /// Gets or sets the number of candidates of the hybrid search that the reranker reorders.
    /// </summary>
    public int Candidates { get; set; } = 30;

    /// <summary>
    /// Gets or sets the time limit of one request in seconds.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 60;
}
