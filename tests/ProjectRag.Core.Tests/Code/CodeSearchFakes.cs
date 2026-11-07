using ProjectRag.Core.Code.Chunks;
using ProjectRag.Core.Embeddings;
using ProjectRag.Core.Reranking;
using ProjectRag.Core.Sources;

namespace ProjectRag.Core.Tests.Code;

// Splits C# files at blank lines: one chunk for each block. The heading holds the path and the first line of the block.
internal sealed class BlockChunker : ICodeChunker
{
    public int Version { get; set; } = 1;

    public bool Supports(string language) => language == "csharp";

    public IReadOnlyList<CodeChunk> Split(string path, string content, CodeChunkingOptions options)
    {
        List<CodeChunk> chunks = [];
        string[] lines = content.Split('\n');
        int offset = 0;
        int blockStart = -1;
        int blockLine = 0;
        int blockEnd = 0;
        string first = string.Empty;
        for (int i = 0; i <= lines.Length; i++)
        {
            string line = i < lines.Length ? lines[i] : string.Empty;
            if (line.Trim().Length > 0)
            {
                if (blockStart < 0)
                    (blockStart, blockLine, first) = (offset, i + 1, line.Trim());

                blockEnd = offset + line.Length;
            }
            else if (blockStart >= 0)
            {
                chunks.Add(new CodeChunk($"{path} > {first}", blockStart, blockEnd, blockLine, i, 0));
                blockStart = -1;
            }

            offset += line.Length + 1;
        }

        return chunks;
    }
}

// A bag of identifier tokens: each token adds 1 to a dimension from its hash. A switch makes the server fail like a stopped server.
internal sealed class BagEmbeddingClient : IEmbeddingClient
{
    private const int Dims = 64;

    public bool Fail { get; set; }

    public int Requests { get; private set; }

    public int TextsEmbedded { get; private set; }

    public Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken)
    {
        Requests++;
        if (Fail)
            return Task.FromException<IReadOnlyList<float[]>>(new HttpRequestException("The embedding server is down."));

        TextsEmbedded += texts.Count;
        return Task.FromResult<IReadOnlyList<float[]>>([.. texts.Select(Embed)]);
    }

    public Task<bool> IsHealthyAsync(CancellationToken cancellationToken) => Task.FromResult(!Fail);

    private static float[] Embed(string text)
    {
        float[] vector = new float[Dims];
        foreach (string token in IdentifierTokens.Split(text).Split(' ', StringSplitOptions.RemoveEmptyEntries))
            vector[(int)((uint)StableHash(token) % Dims)] += 1;

        vector[0] += 0.01f;
        float norm = MathF.Sqrt(vector.Sum(static value => value * value));
        return [.. vector.Select(value => value / norm)];
    }

    private static int StableHash(string word)
    {
        unchecked
        {
            int hash = (int)2166136261;
            foreach (char c in word)
                hash = (hash ^ c) * 16777619;

            return hash & int.MaxValue;
        }
    }
}

// The score of a text is the number of the tokens of the query in it.
internal sealed class TokenRerankClient : IRerankClient
{
    public Task<IReadOnlyList<double>> RerankAsync(string query, IReadOnlyList<string> documents, CancellationToken cancellationToken)
    {
        HashSet<string> terms = [.. IdentifierTokens.Terms(query)];
        return Task.FromResult<IReadOnlyList<double>>([.. documents.Select(document => (double)IdentifierTokens.Split(document).Split(' ').Distinct().Count(terms.Contains))]);
    }

    public Task<bool> IsHealthyAsync(CancellationToken cancellationToken) => Task.FromResult(true);
}

internal sealed class NoPageClient : IPageClient
{
    public Task<WebPage> GetAsync(Uri address, long maxBytes, CancellationToken cancellationToken) =>
        throw new HttpRequestException("No pages.", null, System.Net.HttpStatusCode.NotFound);
}
