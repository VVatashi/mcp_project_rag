using System.Net;
using System.Text.Json.Nodes;

using ProjectRag.Core.Reranking;

namespace ProjectRag.Core.Tests.Reranking;

public sealed class LlamaRerankClientTests
{
    [Fact]
    public async Task RerankAsync_ReturnsTheScoresInTheOrderOfTheDocuments()
    {
        FakeHandler handler = new(static (request, body) =>
        {
            Assert.Equal("/v1/rerank", request.RequestUri!.AbsolutePath);
            JsonNode json = JsonNode.Parse(body)!;
            Assert.Equal("query", json["query"]!.GetValue<string>());
            Assert.Equal(2, json["top_n"]!.GetValue<int>());
            return (HttpStatusCode.OK, """{"results":[{"index":1,"relevance_score":0.9},{"index":0,"relevance_score":0.1}]}""");
        });
        LlamaRerankClient client = new(new FakeHttpClientFactory(handler), new FixedOptions<RerankOptions>(new RerankOptions()));

        IReadOnlyList<double> scores = await client.RerankAsync("query", ["first", "second"], TestContext.Current.CancellationToken);

        Assert.Equal([0.1, 0.9], scores);
    }

    [Fact]
    public async Task RerankAsync_ThrowsOnAWrongIndex()
    {
        FakeHandler handler = new(static (_, _) => (HttpStatusCode.OK, """{"results":[{"index":5,"relevance_score":0.9}]}"""));
        LlamaRerankClient client = new(new FakeHttpClientFactory(handler), new FixedOptions<RerankOptions>(new RerankOptions()));

        await Assert.ThrowsAsync<HttpRequestException>(() => client.RerankAsync("query", ["only"], TestContext.Current.CancellationToken));
    }
}
