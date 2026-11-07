using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

using ProjectRag.Core.Embeddings;

namespace ProjectRag.Core.Tests.Embeddings;

public sealed class LlamaEmbeddingClientTests
{
    [Fact]
    public async Task EmbedAsync_SplitsIntoBatchesAndKeepsTheOrder()
    {
        // The fake server gives the vector [length of the text, 1] and returns the items in reverse order.
        FakeHandler handler = new(static (request, body) =>
        {
            Assert.Equal("/v1/embeddings", request.RequestUri!.AbsolutePath);
            JsonArray input = JsonNode.Parse(body)!["input"]!.AsArray();
            JsonArray data = [.. input.Select((text, index) => new JsonObject { ["index"] = index, ["embedding"] = new JsonArray(text!.GetValue<string>().Length, 1) }).Reverse()];
            return (HttpStatusCode.OK, new JsonObject { ["data"] = data }.ToJsonString());
        });
        LlamaEmbeddingClient client = new(new FakeHttpClientFactory(handler), new FixedOptions<EmbeddingOptions>(new EmbeddingOptions { BatchSize = 2, Parallelism = 2 }));

        IReadOnlyList<float[]> vectors = await client.EmbedAsync(["a", "bb", "ccc", "dddd", "eeeee"], TestContext.Current.CancellationToken);

        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(5, vectors.Count);
        for (int i = 0; i < vectors.Count; i++)
        {
            // The normalization keeps the direction: the ratio of the values is the length of the text.
            Assert.Equal(1, Math.Sqrt((vectors[i][0] * vectors[i][0]) + (vectors[i][1] * vectors[i][1])), 5);
            Assert.Equal(i + 1, vectors[i][0] / vectors[i][1], 4);
        }
    }

    [Fact]
    public async Task EmbedAsync_ThrowsOnAnErrorOfTheServer()
    {
        FakeHandler handler = new(static (_, _) => (HttpStatusCode.InternalServerError, "{\"error\":\"out of memory\"}"));
        LlamaEmbeddingClient client = new(new FakeHttpClientFactory(handler), new FixedOptions<EmbeddingOptions>(new EmbeddingOptions()));

        HttpRequestException exception = await Assert.ThrowsAsync<HttpRequestException>(() => client.EmbedAsync(["text"], TestContext.Current.CancellationToken));

        Assert.Contains("out of memory", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmbedAsync_ThrowsOnAMissingVector()
    {
        FakeHandler handler = new(static (_, _) => (HttpStatusCode.OK, JsonSerializer.Serialize(new { data = Array.Empty<object>() })));
        LlamaEmbeddingClient client = new(new FakeHttpClientFactory(handler), new FixedOptions<EmbeddingOptions>(new EmbeddingOptions()));

        await Assert.ThrowsAsync<HttpRequestException>(() => client.EmbedAsync(["text"], TestContext.Current.CancellationToken));
    }

    [Fact]
    public void FormatQuery_PutsTheQueryIntoTheTemplate()
    {
        EmbeddingModel model = new(1, "model", 4, "{text}");

        Assert.Equal("Instruct: find\nQuery:what is a texture", EmbeddingModel.FormatQuery("what is a texture", "Instruct: find\nQuery:{text}"));
        Assert.Equal("chunk", model.FormatDocument("chunk"));
    }

    [Fact]
    public void FormatQuery_UsesTheTemplateOfEachSearch()
    {
        EmbeddingOptions options = new();

        string documentation = EmbeddingModel.FormatQuery("find the update", options.QueryTemplate);
        string code = EmbeddingModel.FormatQuery("find the update", options.CodeQueryTemplate);

        Assert.EndsWith("\nQuery:find the update", documentation, StringComparison.Ordinal);
        Assert.Contains("documentation", documentation, StringComparison.Ordinal);
        Assert.StartsWith("Instruct: Given a description of a behavior, a concept or an API, retrieve the source code that implements or uses it\nQuery:", code, StringComparison.Ordinal);
        Assert.EndsWith("find the update", code, StringComparison.Ordinal);
        Assert.NotEqual(documentation, code);
    }
}
