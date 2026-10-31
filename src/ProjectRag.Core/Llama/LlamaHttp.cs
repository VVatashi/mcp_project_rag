using System.Net.Http.Json;
using System.Text.Json;

namespace ProjectRag.Core.Llama;

/// <summary>
/// The parts of the HTTP API of llama-server that the embedding client and the rerank client share.
/// </summary>
internal static class LlamaHttp
{
    /// <summary>
    /// The JSON options of the requests and the answers: names in camelCase, read without regard to case.
    /// </summary>
    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Joins the address of a server and the path of an endpoint. The address may end with a path of its own.
    /// </summary>
    /// <param name="baseUrl">The address of the server.</param>
    /// <param name="path">The path of the endpoint, without the leading slash.</param>
    /// <returns>The address of the endpoint.</returns>
    public static Uri Endpoint(Uri baseUrl, string path) => new($"{baseUrl.AbsoluteUri.TrimEnd('/')}/{path}");

    /// <summary>
    /// Posts a JSON request and reads the JSON answer.
    /// </summary>
    /// <typeparam name="TResponse">The type of the answer.</typeparam>
    /// <param name="http">The client.</param>
    /// <param name="endpoint">The address of the endpoint.</param>
    /// <param name="request">The body of the request.</param>
    /// <param name="cancellationToken">The token that cancels the request.</param>
    /// <returns>The answer.</returns>
    /// <exception cref="HttpRequestException">The server does not answer, answers with an error or sends no body.</exception>
    public static async Task<TResponse> PostAsync<TResponse>(HttpClient http, Uri endpoint, object request, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await http.PostAsJsonAsync(endpoint, request, Json, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            string body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException($"The server {endpoint} answered {(int)response.StatusCode}: {Shorten(body)}", null, response.StatusCode);
        }

        try
        {
            return await response.Content.ReadFromJsonAsync<TResponse>(Json, cancellationToken) ?? throw new HttpRequestException($"The server {endpoint} sent an empty answer.");
        }
        catch (JsonException exception)
        {
            throw new HttpRequestException($"The server {endpoint} sent an answer that is not the expected JSON: {exception.Message}", exception);
        }
    }

    /// <summary>
    /// Calls the endpoint <c>/health</c> of a server.
    /// </summary>
    /// <param name="http">The client.</param>
    /// <param name="baseUrl">The address of the server.</param>
    /// <param name="cancellationToken">The token that cancels the request.</param>
    /// <returns><see langword="true"/> when the server answers with a success code.</returns>
    public static async Task<bool> IsHealthyAsync(HttpClient http, Uri baseUrl, CancellationToken cancellationToken)
    {
        try
        {
            using HttpResponseMessage response = await http.GetAsync(Endpoint(baseUrl, "health"), cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private static string Shorten(string text) => text.Length <= 500 ? text : string.Concat(text.AsSpan(0, 500), "…");
}
