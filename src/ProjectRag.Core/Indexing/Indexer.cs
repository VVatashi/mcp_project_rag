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
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Npgsql.NpgsqlException or HttpRequestException)
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
    /// Gets the version that the documents store: a change of the readers, the chunker or the size of a chunk reads every file again.
    /// </summary>
    /// <param name="maxChars">The most characters in a chunk.</param>
    /// <returns>The version.</returns>
    internal static int ParserVersion(int maxChars) => (DocumentReader.Version * 100_000) + maxChars;

    /// <summary>
    /// Computes the SHA-256 of bytes in lowercase hexadecimal.
    /// </summary>
    internal static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private async Task SyncFilesAsync(Source source, IndexRun run, List<string> warnings, Action<IndexRun>? progress, CancellationToken cancellationToken)
    {
        ChunkingOptions options = chunking.CurrentValue;
        MarkdownChunker chunker = new(options.MaxChars);
        int parserVersion = ParserVersion(options.MaxChars);
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

            seen.Add(path);
            if (info.Length > options.MaxFileBytes)
            {
                skipped.Add(path);
                continue;
            }

            // PostgreSQL keeps microseconds, so the time of the file loses its last digit before the comparison.
            DateTime modifiedAt = new(info.LastWriteTimeUtc.Ticks - (info.LastWriteTimeUtc.Ticks % 10), DateTimeKind.Utc);
            DocumentFileState? state = known.GetValueOrDefault(path);
            if (state is not null && state.SizeBytes == info.Length && state.ModifiedAt == modifiedAt && state.ParserVersion == parserVersion)
            {
                run.DocumentsUnchanged++;
                continue;
            }

            byte[] bytes = await File.ReadAllBytesAsync(info.FullName, cancellationToken);
            DocumentFile file = new(Hash(bytes), parserVersion, bytes.LongLength, modifiedAt);
            if (state is not null && state.ContentHash == file.ContentHash && state.ParserVersion == parserVersion)
            {
                await documents.TouchAsync(state.DocumentId, file.SizeBytes, modifiedAt, cancellationToken);
                run.DocumentsUnchanged++;
                continue;
            }

            DocumentText text;
            IReadOnlyList<TextChunk> chunks;
            try
            {
                text = DocumentReader.Read(path, bytes);
                chunks = chunker.Split(text);
            }
            // A broken file must not stop the run. PdfPig throws many kinds of exceptions on a damaged PDF.
#pragma warning disable CA1031
            catch (Exception exception) when (exception is not OperationCanceledException)
#pragma warning restore CA1031
            {
                logger.LogWarning(exception, "The indexer cannot read the file {Path} of the source {Source}.", path, source.Name);
                run.DocumentsFailed++;
                continue;
            }

            List<(TextChunk Chunk, string Hash)> hashed = [.. chunks.Select(static chunk => (chunk, Hash(Encoding.UTF8.GetBytes(chunk.EmbeddingText))))];
            run.ChunksReused += await documents.SaveAsync(source.SourceId, path, text, file, hashed, cancellationToken);
            if (state is null)
                run.DocumentsAdded++;
            else
                run.DocumentsUpdated++;

            logger.LogDebug("The indexer read {Path}: {Chunks} chunks.", path, chunks.Count);
            progress?.Invoke(run);
        }

        run.DocumentsRemoved = await documents.DeleteMissingAsync(source.SourceId, seen, cancellationToken);
        if (skipped.Count > 0)
        {
            warnings.Add(string.Create(CultureInfo.InvariantCulture,
                $"{skipped.Count} files are larger than {options.MaxFileBytes} bytes and were skipped, for example {skipped[0]}."));
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
        try
        {
            while (true)
            {
                IReadOnlyList<PendingChunk> pending = await embeddings.GetPendingAsync(source.SourceId, model.ModelId, roundSize, cancellationToken);
                if (pending.Count == 0)
                    break;

                string[] texts = [.. pending.Select(chunk => model.FormatDocument(TextChunk.Compose(chunk.Heading, chunk.Body)))];
                IReadOnlyList<float[]> vectors = await client.EmbedAsync(texts, cancellationToken);
                await embeddings.AddEmbeddingsAsync(model.ModelId, [.. pending.Select((chunk, index) => (chunk.ChunkId, vectors[index]))], cancellationToken);
                run.ChunksEmbedded += pending.Count;
                await runs.SaveAsync(run, cancellationToken);
                progress?.Invoke(run);
            }
        }
        catch (HttpRequestException exception)
        {
            models.Invalidate();
            await WarnPendingAsync(source, run, warnings, exception.Message, cancellationToken);
        }
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
