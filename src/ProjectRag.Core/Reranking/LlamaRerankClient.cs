using System.Text.Json.Serialization;

using Microsoft.Extensions.Options;

using ProjectRag.Core.Llama;

namespace ProjectRag.Core.Reranking;

/// <summary>
/// The client of the endpoint <c>/v1/rerank</c> of llama-server. The server needs the option <c>--rerank</c>.
/// </summary>
/// <param name="clients">The factory of the HTTP clients.</param>
/// <param name="options">The options of the server.</param>
public sealed class LlamaRerankClient(IHttpClientFactory clients, IOptionsMonitor<RerankOptions> options) : IRerankClient
{
    /// <summary>
    /// The name of the HTTP client in the factory.
    /// </summary>
    public const string HttpClientName = "llama-rerank";

    /// <inheritdoc/>
    public async Task<IReadOnlyList<double>> RerankAsync(string query, IReadOnlyList<string> documents, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(documents);
        double[] scores = new double[documents.Count];
        if (documents.Count == 0)
            return scores;

        Uri endpoint = LlamaHttp.Endpoint(options.CurrentValue.BaseUrl, "v1/rerank");
        RerankResponse response = await LlamaHttp.PostAsync<RerankResponse>(clients.CreateClient(HttpClientName), endpoint, new RerankRequest(query, [.. documents], documents.Count), cancellationToken);
        if (response.Results is not { } results || results.Length != documents.Count)
            throw new HttpRequestException($"The server {endpoint} sent {response.Results?.Length ?? 0} scores for {documents.Count} texts.");

        foreach (RerankResult result in results)
        {
            if (result.Index < 0 || result.Index >= documents.Count)
                throw new HttpRequestException($"The server {endpoint} sent a score with the wrong index {result.Index}.");

            scores[result.Index] = result.RelevanceScore;
        }

        return scores;
    }

    /// <inheritdoc/>
    public Task<bool> IsHealthyAsync(CancellationToken cancellationToken) => LlamaHttp.IsHealthyAsync(clients.CreateClient(HttpClientName), options.CurrentValue.BaseUrl, cancellationToken);

    private sealed record RerankRequest(string Query, string[] Documents, [property: JsonPropertyName("top_n")] int TopN);

    private sealed record RerankResponse(RerankResult[]? Results);

    private sealed record RerankResult(int Index, [property: JsonPropertyName("relevance_score")] double RelevanceScore);
}
