using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

using ProjectRag.Core.Embeddings;

namespace ProjectRag.Core.Tests.Embeddings;

public sealed class RemoteEmbeddingClientTests : IDisposable
{
    private const string KeyValue = "test-key-for-the-fake-api";

    // Each test owns a variable, so the tests of this class can run in parallel.
    private readonly string _variable = "PROJECTRAG_TEST_KEY_" + Guid.NewGuid().ToString("N");

    public RemoteEmbeddingClientTests() => Environment.SetEnvironmentVariable(_variable, KeyValue);

    public void Dispose() => Environment.SetEnvironmentVariable(_variable, null);

    [Fact]
    public async Task EmbedAsync_SendsTheKeyAndTheModelAndNormalizesTheVectors()
    {
        HttpRequestMessage? seen = null;
        string? sentBody = null;
        // The fake API returns the items in reverse order, as a server may, and reports the usage.
        ScriptedHandler handler = new((request, body, _) =>
        {
            seen = request;
            sentBody = body;
            JsonArray input = JsonNode.Parse(body)!["input"]!.AsArray();
            JsonArray data = [.. input.Select((_, index) => new JsonObject { ["index"] = index, ["embedding"] = new JsonArray(3 * (index + 1), 4 * (index + 1)) }).Reverse()];
            return Json(HttpStatusCode.OK, new JsonObject { ["data"] = data, ["provider"] = "fake", ["usage"] = new JsonObject { ["prompt_tokens"] = 12, ["cost"] = 0.00000024 } }.ToJsonString());
        });
        RemoteEmbeddingClient client = Create(handler, new RemoteEmbeddingOptions { Enabled = true, ApiKeyVariable = _variable, Model = "vendor/model" });

        RemoteEmbedding result = await client.EmbedAsync(["a", "b"], TestContext.Current.CancellationToken);

        Assert.Equal("/api/v1/embeddings", seen!.RequestUri!.AbsolutePath);
        Assert.Equal(new AuthenticationHeaderValue("Bearer", KeyValue), seen.Headers.Authorization);
        JsonNode sent = JsonNode.Parse(sentBody!)!;
        Assert.Equal("vendor/model", sent["model"]!.GetValue<string>());
        Assert.Equal(["a", "b"], sent["input"]!.AsArray().Select(static item => item!.GetValue<string>()));
        Assert.Equal(2, result.Vectors.Count);
        foreach (float[] vector in result.Vectors)
        {
            Assert.Equal(0.6f, vector[0], 4);
            Assert.Equal(0.8f, vector[1], 4);
        }

        Assert.Equal(12, result.Tokens);
        Assert.Equal(0.00000024m, result.Cost);
    }

    [Fact]
    public async Task EmbedAsync_EstimatesTheUsageFromTheCharactersWhenTheApiSendsNone()
    {
        ScriptedHandler handler = new((_, _, _) => Json(HttpStatusCode.OK, """{"data":[{"index":0,"embedding":[1,0]}]}"""));
        RemoteEmbeddingOptions options = Options();
        options.PricePerMillionTokens = 1000;
        RemoteEmbeddingClient client = Create(handler, options);

        // Seven characters give three tokens, rounded up.
        RemoteEmbedding result = await client.EmbedAsync(["abcdefg"], TestContext.Current.CancellationToken);

        Assert.Equal((3, 0.003m), (result.Tokens, result.Cost));
    }

    [Fact]
    public async Task EmbedAsync_CountsTheTokensTimesThePriceWhenTheApiReportsNoCost()
    {
        ScriptedHandler handler = new((_, _, _) => Json(HttpStatusCode.OK, """{"data":[{"index":0,"embedding":[1,0]}],"usage":{"prompt_tokens":500000}}"""));
        RemoteEmbeddingOptions options = Options();
        options.PricePerMillionTokens = 0.5m;
        RemoteEmbeddingClient client = Create(handler, options);

        RemoteEmbedding result = await client.EmbedAsync(["a"], TestContext.Current.CancellationToken);

        Assert.Equal((500000, 0.25m), (result.Tokens, result.Cost));
    }

    [Fact]
    public async Task EmbedAsync_KeepsTheLargerReportedCost()
    {
        ScriptedHandler handler = new((_, _, _) => Json(HttpStatusCode.OK, """{"data":[{"index":0,"embedding":[1,0]}],"usage":{"prompt_tokens":10,"cost":0.5}}"""));
        RemoteEmbeddingClient client = Create(handler, Options());

        RemoteEmbedding result = await client.EmbedAsync(["a"], TestContext.Current.CancellationToken);

        Assert.Equal(0.5m, result.Cost);
    }

    [Fact]
    public async Task EmbedAsync_DoesNotRepeatABilledAnswerThatIsNotUsable()
    {
        ScriptedHandler handler = new((_, _, _) => Json(HttpStatusCode.OK, """{"data":[],"usage":{"prompt_tokens":1000000,"cost":0.3}}"""));
        RemoteEmbeddingClient client = Create(handler, Options(retries: 2));

        RemoteEmbeddingException exception = await Assert.ThrowsAsync<RemoteEmbeddingException>(() => client.EmbedAsync(["a"], TestContext.Current.CancellationToken));

        Assert.Equal(1, handler.Requests);
        Assert.Equal((1000000, 0.3m), (exception.Tokens, exception.Cost));
    }

    [Fact]
    public async Task EmbedAsync_DoesNotRepeatAnAnswerWithBrokenJson()
    {
        ScriptedHandler handler = new((_, _, _) => Json(HttpStatusCode.OK, "{not json"));
        RemoteEmbeddingClient client = Create(handler, Options(retries: 2));

        await Assert.ThrowsAsync<RemoteEmbeddingException>(() => client.EmbedAsync(["a"], TestContext.Current.CancellationToken));

        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task EmbedAsync_RedactsTheKeyInTheErrorMessage()
    {
        ScriptedHandler handler = new((_, _, _) => Json(HttpStatusCode.Unauthorized, $$"""{"error":"bad {{KeyValue}} and sk-or-v1-abcdef123456 and Bearer abc.def"}"""));
        RemoteEmbeddingClient client = Create(handler, Options(retries: 0));

        HttpRequestException exception = await Assert.ThrowsAsync<HttpRequestException>(() => client.EmbedAsync(["a"], TestContext.Current.CancellationToken));

        Assert.Contains("***", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(KeyValue, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("abcdef123456", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("abc.def", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmbedAsync_RepeatsAfter429AndFollowsRetryAfter()
    {
        // The backoff would last an hour, so only the header of the answer can make the test fast.
        ScriptedHandler handler = new((_, _, attempt) => attempt == 0
            ? Json(HttpStatusCode.TooManyRequests, """{"error":"slow down"}""", retryAfter: TimeSpan.Zero)
            : Json(HttpStatusCode.OK, """{"data":[{"index":0,"embedding":[1,0]}],"usage":{"prompt_tokens":3,"cost":0.1}}"""));
        RemoteEmbeddingClient client = Create(handler, Options(), TimeSpan.FromHours(1));

        RemoteEmbedding result = await client.EmbedAsync(["a"], TestContext.Current.CancellationToken);

        Assert.Equal(2, handler.Requests);
        Assert.Equal(0.1m, result.Cost);
    }

    [Fact]
    public async Task EmbedAsync_RepeatsAfter500()
    {
        ScriptedHandler handler = new((_, _, attempt) => attempt < 2
            ? Json(HttpStatusCode.InternalServerError, "{}")
            : Json(HttpStatusCode.OK, """{"data":[{"index":0,"embedding":[0,2]}]}"""));
        RemoteEmbeddingClient client = Create(handler, Options(retries: 2));

        RemoteEmbedding result = await client.EmbedAsync(["a"], TestContext.Current.CancellationToken);

        Assert.Equal(3, handler.Requests);
        Assert.Equal(1f, result.Vectors[0][1], 4);
    }

    [Fact]
    public async Task EmbedAsync_GivesUpAfterTheRetries()
    {
        ScriptedHandler handler = new((_, _, _) => Json(HttpStatusCode.ServiceUnavailable, """{"error":"busy"}"""));
        RemoteEmbeddingClient client = Create(handler, Options(retries: 2));

        HttpRequestException exception = await Assert.ThrowsAsync<HttpRequestException>(() => client.EmbedAsync(["a"], TestContext.Current.CancellationToken));

        Assert.Equal(3, handler.Requests);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, exception.StatusCode);
        Assert.Contains("busy", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(KeyValue, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmbedAsync_DoesNotRepeatAfterARejection()
    {
        ScriptedHandler handler = new((_, _, _) => Json(HttpStatusCode.Unauthorized, """{"error":"no access"}"""));
        RemoteEmbeddingClient client = Create(handler, Options(retries: 2));

        HttpRequestException exception = await Assert.ThrowsAsync<HttpRequestException>(() => client.EmbedAsync(["a"], TestContext.Current.CancellationToken));

        Assert.Equal(1, handler.Requests);
        Assert.Equal(HttpStatusCode.Unauthorized, exception.StatusCode);
    }

    [Fact]
    public async Task EmbedAsync_ThrowsOnAMissingVector()
    {
        ScriptedHandler handler = new((_, _, _) => Json(HttpStatusCode.OK, """{"data":[]}"""));
        RemoteEmbeddingClient client = Create(handler, Options(retries: 0));

        await Assert.ThrowsAsync<RemoteEmbeddingException>(() => client.EmbedAsync(["a"], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task EmbedAsync_NeedsAKey()
    {
        ScriptedHandler handler = new((_, _, _) => Json(HttpStatusCode.OK, "{}"));
        RemoteEmbeddingOptions options = Options();
        options.ApiKeyVariable = _variable + "_MISSING";
        RemoteEmbeddingClient client = Create(handler, options);

        Assert.Contains(options.ApiKeyVariable, client.Unavailable, StringComparison.Ordinal);
        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() => client.EmbedAsync(["a"], TestContext.Current.CancellationToken));
        Assert.Contains(options.ApiKeyVariable, exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, handler.Requests);
    }

    [Fact]
    public void Unavailable_ExplainsADisabledBackend()
    {
        RemoteEmbeddingClient client = Create(new ScriptedHandler((_, _, _) => Json(HttpStatusCode.OK, "{}")), new RemoteEmbeddingOptions { ApiKeyVariable = _variable });

        Assert.Contains("off", client.Unavailable, StringComparison.Ordinal);
    }

    [Fact]
    public void Unavailable_IsNullWithAKey()
    {
        RemoteEmbeddingClient client = Create(new ScriptedHandler((_, _, _) => Json(HttpStatusCode.OK, "{}")), Options());

        Assert.Null(client.Unavailable);
    }

    [Fact]
    public void Allows_MatchesTheNamesOfTheListedSources()
    {
        RemoteEmbeddingOptions options = new() { Enabled = true, Sources = ["Docs"] };

        Assert.True(options.Allows("docs"));
        Assert.False(options.Allows("code"));
        Assert.False(new RemoteEmbeddingOptions { Enabled = true }.Allows("docs"));
        Assert.False(new RemoteEmbeddingOptions { Sources = ["docs"] }.Allows("docs"));
    }

    private RemoteEmbeddingOptions Options(int retries = 2) => new() { Enabled = true, ApiKeyVariable = _variable, Retries = retries };

    private static RemoteEmbeddingClient Create(ScriptedHandler handler, RemoteEmbeddingOptions options, TimeSpan? backoff = null) =>
        new(new FakeHttpClientFactory(handler), new FixedOptions<RemoteEmbeddingOptions>(options)) { BackoffBase = backoff ?? TimeSpan.Zero };

    private static HttpResponseMessage Json(HttpStatusCode status, string body, TimeSpan? retryAfter = null)
    {
        HttpResponseMessage response = new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        response.Headers.RetryAfter = retryAfter is { } delay ? new RetryConditionHeaderValue(delay) : null;
        return response;
    }

    // Answers each request with a function of the request, its body and the number of earlier requests.
    private sealed class ScriptedHandler(Func<HttpRequestMessage, string, int, HttpResponseMessage> answer) : HttpMessageHandler
    {
        private int _requests;

        public int Requests => Volatile.Read(ref _requests);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            return answer(request, body, Interlocked.Increment(ref _requests) - 1);
        }
    }
}
