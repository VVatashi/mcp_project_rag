using System.Collections.Immutable;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ProjectRAG.Cli;
using Tokenizers.DotNet;

namespace ProjectRAG.Cli.Tests;

/// <summary>Marks a test that needs the model file. The test is skipped when the model is not in the working tree.</summary>
public sealed class ModelFactAttribute : FactAttribute
{
    public ModelFactAttribute()
    {
        try
        {
            Qwen3EmbeddingProvider.Find(new EmbeddingOptions().ModelFile);
        }
        catch (FileNotFoundException)
        {
            Skip = $"{new EmbeddingOptions().ModelFile} is not in the working tree.";
        }
    }
}

/// <summary>Loads the model once for every test of the class. Loading it costs about a second and 1.8 GB.</summary>
public sealed class EmbeddingFixture : IDisposable
{
    private readonly Tokenizer _tokenizer;

    public EmbeddingFixture()
    {
        EmbeddingOptions options = new();
        _tokenizer = Qwen3EmbeddingProvider.CreateTokenizer(options);
        Embeddings = new Qwen3EmbeddingProvider(_tokenizer, Options.Create(options), NullLogger<Qwen3EmbeddingProvider>.Instance);
    }

    public Qwen3EmbeddingProvider Embeddings { get; }

    public void Dispose()
    {
        Embeddings.Dispose();
        _tokenizer.Dispose();
    }
}

[Collection(IntegrationCollection.Name)]
public sealed class Qwen3EmbeddingProviderTests(EmbeddingFixture fixture)
{
    private const string Passage = "The RHI layer separates the engine from one graphics API.";

    private const string Similar = "A render hardware interface keeps the engine away from a single graphics API.";

    private const string Unrelated = "Borscht needs beetroot, cabbage, potato and tomato paste.";

    [ModelFact]
    public void VectorIsNormalizedAndHasTheDeclaredSize()
    {
        float[] vector = fixture.Embeddings.EmbedDocument(Passage);

        Assert.Equal(fixture.Embeddings.Dimensions, vector.Length);
        Assert.Equal(1f, MathF.Sqrt(IEmbeddingProvider.Similarity(vector, vector)), 3);
    }

    [ModelFact]
    public void SimilarTextsStayCloseAndUnrelatedTextsStayFar()
    {
        float[] passage = fixture.Embeddings.EmbedDocument(Passage);
        float close = IEmbeddingProvider.Similarity(passage, fixture.Embeddings.EmbedDocument(Similar));
        float far = IEmbeddingProvider.Similarity(passage, fixture.Embeddings.EmbedDocument(Unrelated));

        Assert.True(close > 0.7f, $"similar texts scored {close}");
        Assert.True(far < 0.3f, $"unrelated texts scored {far}");
    }

    [ModelFact]
    public void BatchRepeatsTheVectorsOfSingleCalls()
    {
        string[] texts = [Passage, Similar, Unrelated];
        ImmutableArray<float[]> batch = fixture.Embeddings.EmbedDocumentBatch(texts);

        Assert.Equal(texts.Length, batch.Length);
        for (int index = 0; index < texts.Length; index++)
            Assert.Equal(1f, IEmbeddingProvider.Similarity(fixture.Embeddings.EmbedDocument(texts[index]), batch[index]), 3);
    }

    [ModelFact]
    public void QueryFindsThePassageThatAnswersIt()
    {
        float[] query = fixture.Embeddings.EmbedQuery("What does the RHI layer separate?");
        float answer = IEmbeddingProvider.Similarity(query, fixture.Embeddings.EmbedDocument(Passage));
        float noise = IEmbeddingProvider.Similarity(query, fixture.Embeddings.EmbedDocument(Unrelated));

        Assert.True(answer > noise, $"the answer scored {answer} and the noise scored {noise}");
    }
}
