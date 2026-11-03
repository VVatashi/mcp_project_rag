using System.Globalization;

namespace ProjectRag.Core.Sources;

/// <summary>
/// A web page as its server sent it.
/// </summary>
/// <param name="Bytes">The bytes of the page.</param>
/// <param name="Address">The address of the page after its redirects. The relative links of the page start from it.</param>
public sealed record WebPage(byte[] Bytes, Uri Address);

/// <summary>
/// Gets the web pages of the sources.
/// </summary>
public interface IPageClient
{
    /// <summary>
    /// Gets a page.
    /// </summary>
    /// <param name="address">The address of the page.</param>
    /// <param name="maxBytes">The size of the largest page.</param>
    /// <param name="cancellationToken">The token that cancels the request.</param>
    /// <returns>The page.</returns>
    /// <exception cref="HttpRequestException">The server does not answer in time, breaks the connection or answers with an error.</exception>
    /// <exception cref="InvalidDataException">The page is larger than <paramref name="maxBytes"/>.</exception>
    Task<WebPage> GetAsync(Uri address, long maxBytes, CancellationToken cancellationToken);
}

/// <summary>
/// Gets web pages over HTTP. The client follows redirects and names itself ProjectRag in the header User-Agent.
/// </summary>
/// <param name="factory">The factory of the HTTP clients.</param>
public sealed class HttpPageClient(IHttpClientFactory factory) : IPageClient
{
    /// <summary>
    /// The name of the HTTP client in the factory.
    /// </summary>
    public const string HttpClientName = "ProjectRag.Pages";

    /// <summary>
    /// The most time for one page, with its body.
    /// </summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    /// <inheritdoc/>
    public async Task<WebPage> GetAsync(Uri address, long maxBytes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(address);
        HttpClient http = factory.CreateClient(HttpClientName);
        // The timeout of the client ends with the headers. A slow body must not hold the indexer either.
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        try
        {
            using HttpResponseMessage response = await http.GetAsync(address, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException(string.Create(CultureInfo.InvariantCulture, $"The server answered {(int)response.StatusCode} {response.ReasonPhrase}."), null, response.StatusCode);

            if (response.Content.Headers.ContentLength > maxBytes)
                throw TooLarge(maxBytes);

            // A server may send no length, so the read stops one byte past the limit.
            await using Stream stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using MemoryStream buffer = new();
            byte[] chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, timeout.Token)) > 0)
            {
                buffer.Write(chunk, 0, read);
                if (buffer.Length > maxBytes)
                    throw TooLarge(maxBytes);
            }

            return new WebPage(buffer.ToArray(), response.RequestMessage?.RequestUri ?? address);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new HttpRequestException(string.Create(CultureInfo.InvariantCulture, $"The server did not send the page in {Timeout.TotalSeconds:F0} seconds."), exception);
        }
        // A connection that breaks inside the body gives an error of the stream, not of HTTP.
        catch (IOException exception)
        {
            throw new HttpRequestException($"The connection broke: {exception.Message}", exception);
        }
    }

    private static InvalidDataException TooLarge(long maxBytes) =>
        new(string.Create(CultureInfo.InvariantCulture, $"The page is larger than {maxBytes} bytes."));
}
