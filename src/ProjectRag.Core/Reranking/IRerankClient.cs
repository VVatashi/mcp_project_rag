namespace ProjectRag.Core.Reranking;

/// <summary>
/// A client of a reranker: a model that scores how well a text answers a query.
/// </summary>
public interface IRerankClient
{
    /// <summary>
    /// Scores texts against a query.
    /// </summary>
    /// <param name="query">The query.</param>
    /// <param name="documents">The texts.</param>
    /// <param name="cancellationToken">The token that cancels the request.</param>
    /// <returns>A score for each text, in the order of the texts. A higher score means a better answer.</returns>
    /// <exception cref="HttpRequestException">The server does not answer or answers with an error.</exception>
    Task<IReadOnlyList<double>> RerankAsync(string query, IReadOnlyList<string> documents, CancellationToken cancellationToken);

    /// <summary>
    /// Tells whether the server is up and has loaded its model.
    /// </summary>
    /// <param name="cancellationToken">The token that cancels the request.</param>
    /// <returns><see langword="true"/> when the server answers its health check.</returns>
    Task<bool> IsHealthyAsync(CancellationToken cancellationToken);
}
