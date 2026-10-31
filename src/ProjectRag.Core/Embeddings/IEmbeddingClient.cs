namespace ProjectRag.Core.Embeddings;

/// <summary>
/// A client of an embedding model.
/// </summary>
public interface IEmbeddingClient
{
    /// <summary>
    /// Embeds texts. The caller applies the templates of the model.
    /// </summary>
    /// <param name="texts">The texts.</param>
    /// <param name="cancellationToken">The token that cancels the requests.</param>
    /// <returns>A vector of length 1 for each text, in the order of the texts.</returns>
    /// <exception cref="HttpRequestException">The server does not answer or answers with an error.</exception>
    Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken);

    /// <summary>
    /// Tells whether the server is up and has loaded its model.
    /// </summary>
    /// <param name="cancellationToken">The token that cancels the request.</param>
    /// <returns><see langword="true"/> when the server answers its health check.</returns>
    Task<bool> IsHealthyAsync(CancellationToken cancellationToken);
}
