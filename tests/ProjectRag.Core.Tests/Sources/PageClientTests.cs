using System.Net;

using ProjectRag.Core.Sources;

namespace ProjectRag.Core.Tests.Sources;

public sealed class PageClientTests
{
    private static readonly Uri Address = new("https://example.com/spec.html");

    [Fact]
    public async Task GetAsync_GivesThePageWithItsAddress()
    {
        using FakeHandler handler = new(static (_, _) => (HttpStatusCode.OK, "<h1>Spec</h1>"));
        HttpPageClient client = new(new FakeHttpClientFactory(handler));

        WebPage page = await client.GetAsync(Address, 1000, TestContext.Current.CancellationToken);

        Assert.Equal("<h1>Spec</h1>", System.Text.Encoding.UTF8.GetString(page.Bytes));
        Assert.Equal(Address, page.Address);
    }

    [Fact]
    public async Task GetAsync_ThrowsForAnErrorAndForALargePage()
    {
        using FakeHandler missing = new(static (_, _) => (HttpStatusCode.NotFound, "Not found"));
        using FakeHandler large = new(static (_, _) => (HttpStatusCode.OK, new string('x', 2000)));
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        HttpRequestException error = await Assert.ThrowsAsync<HttpRequestException>(() => new HttpPageClient(new FakeHttpClientFactory(missing)).GetAsync(Address, 1000, cancellationToken));
        Assert.Equal(HttpStatusCode.NotFound, error.StatusCode);
        await Assert.ThrowsAsync<InvalidDataException>(() => new HttpPageClient(new FakeHttpClientFactory(large)).GetAsync(Address, 1000, cancellationToken));
    }
}
