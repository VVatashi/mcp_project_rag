using System.Net;
using System.Text;

using Microsoft.Extensions.Options;

namespace ProjectRag.Core.Tests;

/// <summary>
/// A handler that answers each request with a function and records the bodies of the requests.
/// </summary>
internal sealed class FakeHandler(Func<HttpRequestMessage, string, (HttpStatusCode Status, string Body)> answer) : HttpMessageHandler
{
    public List<string> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        lock (Requests)
            Requests.Add(body);

        (HttpStatusCode status, string text) = answer(request, body);
        return new HttpResponseMessage(status) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    }
}

/// <summary>
/// A factory that gives clients over one handler.
/// </summary>
internal sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
}

/// <summary>
/// Options that never change.
/// </summary>
internal sealed class FixedOptions<T>(T value) : IOptionsMonitor<T>
{
    public T CurrentValue => value;

    public T Get(string? name) => value;

    public IDisposable? OnChange(Action<T, string?> listener) => null;
}
