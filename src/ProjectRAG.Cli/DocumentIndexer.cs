using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace ProjectRAG.Cli;

/// <summary>A chunk together with the vector of its rendered text.</summary>
public readonly record struct IndexedChunk(Chunk Chunk, float[] Vector);

/// <summary>What one call of the indexer did. Changed is false when the file was already in the database with the same hash.</summary>
public readonly record struct IndexResult(long DocumentId, Guid Uuid, string Path, int ChunkCount, bool Changed);

/// <summary>Indexes one document: picks a splitter for the file type, cuts the file into chunks, embeds them and stores both.</summary>
public sealed class DocumentIndexer(IEnumerable<IDocumentSplitter> splitters, IEmbeddingProvider embeddings, IDocumentRepository documents, ILogger<DocumentIndexer> logger)
{
    private readonly ImmutableArray<IDocumentSplitter> _splitters = [.. splitters];
    private readonly IEmbeddingProvider _embeddings = embeddings;
    private readonly IDocumentRepository _documents = documents;
    private readonly ILogger<DocumentIndexer> _logger = logger;

    /// <summary>Reads the file and stores it with its chunks. A file whose hash did not change keeps the rows it already has. Throws when no splitter reads that file type.</summary>
    public async Task<IndexResult> IndexAsync(string path, CancellationToken cancellationToken = default)
    {
        IDocumentSplitter splitter = Select(path);
        string source = Path.GetFullPath(path);
        string key = Locate(source);
        string text = await File.ReadAllTextAsync(source, cancellationToken);
        string hash = Hash(text);

        StoredDocument? known = await _documents.FindAsync(key, cancellationToken);
        if (known is not null && known.Hash == hash)
        {
            int count = await _documents.CountChunksAsync(known.Id, cancellationToken);
            _logger.LogInformation("{Path} is already indexed with the same hash, {Count} chunks", source, count);

            return new IndexResult(known.Id, known.Uuid, key, count, false);
        }

        Stopwatch split = Stopwatch.StartNew();
        ImmutableArray<Chunk> chunks = splitter.Split(source, text);
        split.Stop();

        int tokens = chunks.Sum(chunk => chunk.TokenCount);
        _logger.LogInformation("{Path}: {Count} chunks, {Tokens} tokens in {Seconds:F1} s", source, chunks.Length, tokens, split.Elapsed.TotalSeconds);

        Stopwatch embed = Stopwatch.StartNew();
        ImmutableArray<float[]> vectors = _embeddings.EmbedDocumentBatch([.. chunks.Select(chunk => chunk.Render())]);
        embed.Stop();

        _logger.LogInformation("{Count} vectors of {Dimensions} numbers in {Seconds:F1} s, {Rate:F0} tokens per second",
            vectors.Length, _embeddings.Dimensions, embed.Elapsed.TotalSeconds, tokens / embed.Elapsed.TotalSeconds);

        ImmutableArray<IndexedChunk> indexed = [.. chunks.Zip(vectors, (chunk, vector) => new IndexedChunk(chunk, vector))];
        StoredDocument document = await _documents.SaveWithChunksAsync(new NewDocument(key, Path.GetFileNameWithoutExtension(source), hash, text), indexed, cancellationToken);

        _logger.LogInformation("Stored document {Id} ({Uuid}) with {Count} chunks", document.Id, document.Uuid, indexed.Length);

        return new IndexResult(document.Id, document.Uuid, key, indexed.Length, true);
    }

    /// <summary>The hash of the content, in lower case hexadecimal. The indexer compares it with the stored one to skip a file that did not change.</summary>
    public static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>The key of a document. A local path becomes an absolute file URI, another URI stays as it stands, so one document keeps one key.</summary>
    public static string Locate(string pathOrUri)
    {
        if (Uri.TryCreate(pathOrUri, UriKind.Absolute, out Uri? uri) && !uri.IsFile)
            return uri.AbsoluteUri;

        // A plain Windows path parses as a file URI as well, so the local path of the parsed URI is the path to make absolute.
        string path = uri is not null && uri.IsFile ? uri.LocalPath : pathOrUri;

        return new Uri(Path.GetFullPath(path)).AbsoluteUri;
    }

    private IDocumentSplitter Select(string path)
    {
        IDocumentSplitter? splitter = _splitters.FirstOrDefault(candidate => candidate.CanSplit(path));
        if (splitter is null)
            throw new NotSupportedException($"No splitter reads {path}. Known types: {string.Join(", ", _splitters.SelectMany(candidate => candidate.Extensions))}");

        return splitter;
    }
}
