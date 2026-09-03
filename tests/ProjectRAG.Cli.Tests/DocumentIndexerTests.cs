using System.Collections.Immutable;
using Microsoft.Extensions.Logging.Abstractions;
using ProjectRAG.Cli;

namespace ProjectRAG.Cli.Tests;

public sealed class DocumentIndexerTests
{
    [Fact]
    public void LocateGivesOneKeyToOneFile()
    {
        string expected = new Uri(Path.GetFullPath("docs/notes.md")).AbsoluteUri;

        Assert.Equal(expected, DocumentIndexer.Locate("docs/notes.md"));
        Assert.Equal(expected, DocumentIndexer.Locate(Path.GetFullPath("docs/notes.md")));
        Assert.Equal(expected, DocumentIndexer.Locate(Path.Combine(Path.GetFullPath("docs"), "..", "docs", "notes.md")));
        Assert.Equal(expected, DocumentIndexer.Locate(expected));
    }

    [Fact]
    public void LocateKeepsAnAddressOfAnotherScheme()
    {
        Assert.Equal("https://example.com/docs/notes.md", DocumentIndexer.Locate("https://example.com/docs/notes.md"));
    }

    [Fact]
    public void HashFollowsTheContent()
    {
        Assert.Equal(DocumentIndexer.Hash("one text"), DocumentIndexer.Hash("one text"));
        Assert.NotEqual(DocumentIndexer.Hash("one text"), DocumentIndexer.Hash("another text"));
        Assert.Equal(64, DocumentIndexer.Hash("one text").Length);
    }
}

/// <summary>Stands in for the model. The vector of a text follows the text, which is all the indexer asks of it.</summary>
internal sealed class StubEmbeddingProvider : IEmbeddingProvider
{
    public int Dimensions => Qwen3EmbeddingProvider.VectorSize;

    public float[] EmbedDocument(string text) => Vector(text);

    public float[] EmbedQuery(string query) => Vector(query);

    public ImmutableArray<float[]> EmbedDocumentBatch(IReadOnlyList<string> texts) => [.. texts.Select(Vector)];

    public ImmutableArray<float[]> EmbedQueryBatch(IReadOnlyList<string> queries) => [.. queries.Select(Vector)];

    private static float[] Vector(string text)
    {
        float[] vector = new float[Qwen3EmbeddingProvider.VectorSize];
        vector[(int)((uint)text.GetHashCode(StringComparison.Ordinal) % (uint)vector.Length)] = 1f;

        return vector;
    }
}

[Collection(IntegrationCollection.Name)]
public sealed class DocumentIndexerDatabaseTests : IAsyncLifetime
{
    private const string OneHeading = "# Title\n\nFirst text.\n";

    private const string TwoHeadings = "# Title\n\nFirst text.\n\n## Second\n\nMore text here.\n";

    private readonly IDocumentRepository _documents;
    private readonly DocumentIndexer _indexer;
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.md");

    public DocumentIndexerDatabaseTests(TokenizerFixture tokenizer, DatabaseFixture database)
    {
        _documents = database.Repository;
        _indexer = new DocumentIndexer([tokenizer.CreateSplitter()], new StubEmbeddingProvider(), _documents, NullLogger<DocumentIndexer>.Instance);
    }

    public Task InitializeAsync() => File.WriteAllTextAsync(_path, OneHeading);

    public async Task DisposeAsync()
    {
        StoredDocument? document = await _documents.FindAsync(DocumentIndexer.Locate(_path));
        if (document is not null)
            await _documents.RemoveAsync(document.Id);

        File.Delete(_path);
    }

    [DatabaseFact]
    public async Task IndexStoresTheDocumentWithItsChunks()
    {
        IndexResult result = await _indexer.IndexAsync(_path);

        Assert.True(result.Changed);
        Assert.Equal(1, result.ChunkCount);
        Assert.Equal(DocumentIndexer.Locate(_path), result.Path);
        Assert.Equal(1, await _documents.CountChunksAsync(result.DocumentId));
    }

    [DatabaseFact]
    public async Task IndexSkipsAFileThatDidNotChange()
    {
        IndexResult first = await _indexer.IndexAsync(_path);
        IndexResult second = await _indexer.IndexAsync(_path);

        Assert.True(first.Changed);
        Assert.False(second.Changed);
        Assert.Equal(first.DocumentId, second.DocumentId);
        Assert.Equal(first.ChunkCount, second.ChunkCount);
    }

    [DatabaseFact]
    public async Task IndexReplacesAFileThatChanged()
    {
        IndexResult first = await _indexer.IndexAsync(_path);
        await File.WriteAllTextAsync(_path, TwoHeadings);
        IndexResult second = await _indexer.IndexAsync(_path);

        Assert.True(second.Changed);
        Assert.Equal(first.DocumentId, second.DocumentId);
        Assert.Equal(2, second.ChunkCount);
        Assert.Equal(2, await _documents.CountChunksAsync(second.DocumentId));
    }

    [DatabaseFact]
    public async Task IndexRefusesAFileTypeWithoutASplitter()
    {
        string other = Path.ChangeExtension(_path, ".txt");
        await File.WriteAllTextAsync(other, OneHeading);
        try
        {
            await Assert.ThrowsAsync<NotSupportedException>(() => _indexer.IndexAsync(other));
        }
        finally
        {
            File.Delete(other);
        }
    }
}
