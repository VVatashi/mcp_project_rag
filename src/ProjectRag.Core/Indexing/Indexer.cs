using System.Globalization;
using System.Security.Cryptography;
using System.Text;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using ProjectRag.Core.Documents;
using ProjectRag.Core.Embeddings;
using ProjectRag.Core.Sources;

namespace ProjectRag.Core.Indexing;

/// <summary>
/// Brings the documents, the chunks and the vectors of a source up to date with its folder.
/// </summary>
/// <remarks>
/// <para>
/// A run has two steps. The first step reads the files. A file with the stored size and time is skipped without a read.
/// A file with the stored hash only gets its new time. Any other file is read, split into chunks and written in one transaction.
/// The documents of the files that are gone are deleted.
/// </para>
/// <para>
/// The second step embeds the chunks without a vector of the current model in rounds, and each round commits.
/// So a run that stops keeps its vectors, and a new embedding model gets its vectors at the next run.
/// When the embedding server does not work, the run still succeeds with a warning: the full text search finds the new chunks at once.
/// </para>
/// </remarks>
/// <param name="sources">The store of the sources.</param>
/// <param name="documents">The store of the documents.</param>
/// <param name="embeddings">The store of the vectors.</param>
/// <param name="runs">The store of the runs.</param>
/// <param name="models">The registry of the embedding model.</param>
/// <param name="client">The client of the embedding server.</param>
/// <param name="chunking">The options of the chunker.</param>
/// <param name="indexing">The options of the indexer.</param>
/// <param name="logger">The logger.</param>
public sealed class Indexer(
    SourceStore sources,
    DocumentStore documents,
    EmbeddingStore embeddings,
    IndexRunStore runs,
    EmbeddingModelRegistry models,
    IEmbeddingClient client,
    IOptionsMonitor<ChunkingOptions> chunking,
    IOptionsMonitor<IndexingOptions> indexing,
    ILogger<Indexer> logger)
{
    /// <summary>
    /// Runs the indexer over a source.
    /// </summary>
    /// <param name="sourceId">The key of the source.</param>
    /// <param name="progress">Gets the run after each step, for example to show it.</param>
    /// <param name="cancellationToken">The token that stops the run.</param>
    /// <returns>The ended run, or <see langword="null"/> when the source does not exist.</returns>
    public async Task<IndexRun?> RunAsync(int sourceId, Action<IndexRun>? progress, CancellationToken cancellationToken)
    {
        Source? source = await sources.GetAsync(sourceId, null, cancellationToken);
        if (source is null)
            return null;

        IndexRun run = await runs.StartAsync(sourceId, cancellationToken);
        progress?.Invoke(run);
        logger.LogInformation("The indexer started the run {RunId} over the source {Source} in {Path}.", run.RunId, source.Name, source.RootPath);
        List<string> warnings = [];
        try
        {
            await SyncFilesAsync(source, run, warnings, progress, cancellationToken);
            await runs.SaveAsync(run, cancellationToken);
            await EmbedAsync(source, run, warnings, progress, cancellationToken);
            run.Status = IndexRun.Succeeded;
            await sources.MarkIndexedAsync(sourceId, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            run.Status = IndexRun.Canceled;
        }
        // Any other error ends this run only. An exception out of the background worker would stop the whole host.
#pragma warning disable CA1031
        catch (Exception exception)
#pragma warning restore CA1031
        {
            run.Status = IndexRun.Failed;
            run.Error = exception.Message;
            logger.LogError(exception, "The run {RunId} over the source {Source} failed.", run.RunId, source.Name);
        }

        run.Warning = warnings.Count == 0 ? null : string.Join(" ", warnings);
        // The final write must happen even after a cancel.
        await runs.SaveAsync(run, CancellationToken.None);
        progress?.Invoke(run);
        logger.LogInformation(
            "The run {RunId} over {Source} ended: {Status}. Files {Files}, added {Added}, updated {Updated}, removed {Removed}, failed {Failed}, vectors reused {Reused}, embedded {Embedded}.",
            run.RunId, source.Name, run.Status, run.FilesSeen, run.DocumentsAdded, run.DocumentsUpdated, run.DocumentsRemoved, run.DocumentsFailed, run.ChunksReused, run.ChunksEmbedded);
        return run;
    }

    /// <summary>
    /// Gets the version that the documents store: a change of the importers, the chunker or the sizes of the chunks reads every file again.
    /// </summary>
    /// <param name="options">The options of the chunker.</param>
    /// <returns>The version, for example <c>2;1800;3000;300</c>.</returns>
    internal static string ParserVersion(ChunkingOptions options) =>
        string.Create(CultureInfo.InvariantCulture, $"{DocumentReader.Version};{options.MaxChars};{options.AtomicMaxChars};{options.OverlapChars}");

    /// <summary>
    /// Computes the SHA-256 of bytes in lowercase hexadecimal.
    /// </summary>
    internal static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private async Task SyncFilesAsync(Source source, IndexRun run, List<string> warnings, Action<IndexRun>? progress, CancellationToken cancellationToken)
    {
        ChunkingOptions options = chunking.CurrentValue;
        DocumentChunker chunker = new(options);
        string parserVersion = ParserVersion(options);
        Dictionary<string, DocumentFileState> known = await documents.GetFileStatesAsync(source.SourceId, cancellationToken);
        IReadOnlyList<string> paths = SourceScanner.Scan(source);
        run.FilesSeen = paths.Count;
        List<string> skipped = [];
        List<string> seen = [];
        foreach (string path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileInfo info = new(Path.Combine(source.RootPath, path));
            if (!info.Exists)
                continue;

            // A file over the limit is not seen, so its old document leaves the search.
            if (info.Length > options.MaxFileBytes)
            {
                skipped.Add(path);
                continue;
            }

            seen.Add(path);
            // PostgreSQL keeps microseconds, so the time of the file loses its last digit before the comparison.
            DateTime modifiedAt = new(info.LastWriteTimeUtc.Ticks - (info.LastWriteTimeUtc.Ticks % 10), DateTimeKind.Utc);
            DocumentFileState? state = known.GetValueOrDefault(path);
            if (state is not null && state.SizeBytes == info.Length && state.ModifiedAt == modifiedAt && state.ParserVersion == parserVersion)
            {
                run.DocumentsUnchanged++;
                continue;
            }

            byte[] bytes;
            DocumentStructure structure;
            IReadOnlyList<TextChunk> chunks;
            try
            {
                bytes = await File.ReadAllBytesAsync(info.FullName, cancellationToken);
                if (state is not null && state.ContentHash == Hash(bytes) && state.ParserVersion == parserVersion)
                {
                    await documents.TouchAsync(state.DocumentId, bytes.LongLength, modifiedAt, cancellationToken);
                    run.DocumentsUnchanged++;
                    continue;
                }

                // Every format becomes Markdown first. The structure and the chunks come from the Markdown.
                structure = MarkdownStructureReader.Read(DocumentReader.Read(path, bytes), path);
                chunks = chunker.Split(structure);
            }
            // A locked, unreadable or broken file must not stop the run. PdfPig throws many kinds of exceptions on a damaged PDF.
#pragma warning disable CA1031
            catch (Exception exception) when (exception is not (OperationCanceledException or Npgsql.NpgsqlException))
#pragma warning restore CA1031
            {
                logger.LogWarning(exception, "The indexer cannot read the file {Path} of the source {Source}.", path, source.Name);
                run.DocumentsFailed++;
                continue;
            }

            DocumentFile file = new(Hash(bytes), parserVersion, bytes.LongLength, modifiedAt);

            List<(TextChunk Chunk, string Hash)> hashed = [.. chunks.Select(static chunk => (chunk, Hash(Encoding.UTF8.GetBytes(chunk.EmbeddingText))))];
            run.ChunksReused += await documents.SaveAsync(source.SourceId, path, structure, file, hashed, cancellationToken);
            if (state is null)
                run.DocumentsAdded++;
            else
                run.DocumentsUpdated++;

            logger.LogDebug("The indexer read {Path}: {Chunks} chunks.", path, chunks.Count);
            progress?.Invoke(run);
        }

        // An empty folder is more often a moment of a branch switch or an unplugged drive than a real deletion.
        // The vectors of a source take minutes to rebuild, so the indexer keeps the documents and warns.
        if (paths.Count == 0 && known.Count > 0)
            warnings.Add($"The folder {source.RootPath} holds no matching files. The indexer kept the {known.Count} stored documents. Delete the source to remove them.");
        else
            run.DocumentsRemoved = await documents.DeleteMissingAsync(source.SourceId, seen, cancellationToken);

        if (skipped.Count > 0)
        {
            warnings.Add(string.Create(CultureInfo.InvariantCulture,
                $"{skipped.Count} files are larger than {options.MaxFileBytes} bytes. The indexer skipped them and removed their old documents, for example {skipped[0]}."));
        }
    }

    private async Task EmbedAsync(Source source, IndexRun run, List<string> warnings, Action<IndexRun>? progress, CancellationToken cancellationToken)
    {
        EmbeddingModelStatus status = await models.GetStatusAsync(cancellationToken);
        if (status.Model is not { } model)
        {
            await WarnPendingAsync(source, run, warnings, status.Problem, cancellationToken);
            return;
        }

        int roundSize = Math.Max(1, indexing.CurrentValue.EmbeddingRoundSize);
        HashSet<long> rejected = [];
        try
        {
            while (true)
            {
                // A run lasts minutes. The registry checks the server again each minute, and another model behind the server stops the run.
                if ((await models.GetStatusAsync(cancellationToken)).Model?.ModelId != model.ModelId)
                {
                    await WarnPendingAsync(source, run, warnings, "The embedding model changed during the run.", cancellationToken);
                    break;
                }

                IReadOnlyList<PendingChunk> pending = await embeddings.GetPendingAsync(source.SourceId, model.ModelId, roundSize, rejected, cancellationToken);
                if (pending.Count == 0)
                    break;

                try
                {
                    await EmbedChunksAsync(model, pending, cancellationToken);
                    run.ChunksEmbedded += pending.Count;
                }
                catch (HttpRequestException exception) when (exception.StatusCode is not null)
                {
                    // The server works but rejects a text, for example one longer than its slot. One chunk at a time finds the bad ones.
                    logger.LogWarning("The embedding server rejected a round of {Count} chunks: {Reason}. The indexer tries them one at a time.", pending.Count, exception.Message);
                    foreach (PendingChunk chunk in pending)
                    {
                        try
                        {
                            await EmbedChunksAsync(model, [chunk], cancellationToken);
                            run.ChunksEmbedded++;
                        }
                        catch (HttpRequestException single) when (single.StatusCode is not null)
                        {
                            rejected.Add(chunk.ChunkId);
                            logger.LogWarning("The embedding server rejected the chunk {ChunkId}: {Reason}", chunk.ChunkId, single.Message);
                        }
                    }
                }

                await runs.SaveAsync(run, cancellationToken);
                progress?.Invoke(run);
            }
        }
        catch (Exception exception) when (exception is HttpRequestException || (exception is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            // The server is down or did not answer in time. The next run embeds the rest.
            models.Invalidate();
            await WarnPendingAsync(source, run, warnings, exception.Message, cancellationToken);
        }

        if (rejected.Count > 0)
            warnings.Add($"The embedding server rejected {rejected.Count} chunks, for example the chunk {rejected.First()}. The full text search still finds them.");
    }

    private async Task EmbedChunksAsync(EmbeddingModel model, IReadOnlyList<PendingChunk> chunks, CancellationToken cancellationToken)
    {
        string[] texts = [.. chunks.Select(chunk => model.FormatDocument(TextChunk.Compose(chunk.Heading, chunk.Body)))];
        IReadOnlyList<float[]> vectors = await client.EmbedAsync(texts, cancellationToken);
        await embeddings.AddEmbeddingsAsync(model.ModelId, [.. chunks.Select((chunk, index) => (chunk.ChunkId, vectors[index]))], cancellationToken);
    }

    private async Task WarnPendingAsync(Source source, IndexRun run, List<string> warnings, string? reason, CancellationToken cancellationToken)
    {
        EmbeddingModelStatus status = await models.GetStatusAsync(cancellationToken);
        string count = status.Model is { } model
            ? (await embeddings.CountPendingAsync(source.SourceId, model.ModelId, cancellationToken)).ToString(CultureInfo.InvariantCulture)
            : "all";
        warnings.Add($"The chunks got no vectors: {reason} {count} chunks wait for the next run.");
        logger.LogWarning("The run {RunId} over {Source} wrote no vectors: {Reason}", run.RunId, source.Name, reason);
    }
}
