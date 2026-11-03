using System.Globalization;

namespace ProjectRag.Core.Sources;

/// <summary>
/// Gets the web pages of the sources.
/// </summary>
public interface IPageClient
{
    /// <summary>
    /// Gets the bytes of a page.
    /// </summary>
    /// <param name="address">The address of the page.</param>
    /// <param name="maxBytes">The size of the largest page.</param>
    /// <param name="cancellationToken">The token that cancels the request.</param>
    /// <returns>The bytes of the page.</returns>
    /// <exception cref="HttpRequestException">The server does not answer or answers with an error.</exception>
    /// <exception cref="InvalidDataException">The page is larger than <paramref name="maxBytes"/>.</exception>
    Task<byte[]> GetAsync(Uri address, long maxBytes, CancellationToken cancellationToken);
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

    /// <inheritdoc/>
    public async Task<byte[]> GetAsync(Uri address, long maxBytes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(address);
        HttpClient http = factory.CreateClient(HttpClientName);
        using HttpResponseMessage response = await http.GetAsync(address, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(string.Create(CultureInfo.InvariantCulture, $"The server answered {(int)response.StatusCode} {response.ReasonPhrase}."), null, response.StatusCode);

        if (response.Content.Headers.ContentLength > maxBytes)
            throw TooLarge(maxBytes);

        // A server may send no length, so the read stops one byte past the limit.
        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using MemoryStream buffer = new();
        byte[] chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > maxBytes)
                throw TooLarge(maxBytes);
        }

        return buffer.ToArray();
    }

    private static InvalidDataException TooLarge(long maxBytes) =>
        new(string.Create(CultureInfo.InvariantCulture, $"The page is larger than {maxBytes} bytes."));
}
