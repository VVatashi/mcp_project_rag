using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using ProjectRag.Core.Code;
using ProjectRag.Core.Code.Chunks;
using ProjectRag.Core.Code.Graph;
using ProjectRag.Core.Documents;
using ProjectRag.Core.Embeddings;
using ProjectRag.Core.Sources;

namespace ProjectRag.Core.Indexing;

/// <summary>
/// Brings the documents, the chunks and the vectors of a source up to date with its folder and its web pages.
/// </summary>
/// <remarks>
/// <para>
/// A run has two main steps. The first step reads the files. A file with the stored size and time is skipped without a read.
/// A file with the stored hash only gets its new time. Any other file is read, split into chunks and written in one transaction.
/// The documents of the files that are gone are deleted. A web page goes to its server again after the interval of the refresh.
/// </para>
/// <para>
/// A code source lists its files with <see cref="CodeFileLister"/>. A Markdown file takes the way above and gives chunks of the kind <c>prose</c>.
/// Any other file of text becomes a document with its exact text and its language, and <see cref="CodeChunkers"/> splits it into chunks of the kind <c>code</c> or <c>config</c>.
/// The chunks keep their lines and a token vector for the code search. A binary file and a file over the limit are no documents.
/// </para>
/// <para>
/// A code source with a registered extractor builds its code graph between the steps, see <see cref="CodeGraphBuilder"/>. A failure of the graph gives a warning only.
/// </para>
/// <para>
/// The last step embeds the chunks without a vector of the current model in rounds, and each round commits.
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
/// <param name="pages">The client of the web pages.</param>
/// <param name="time">The clock, for the refresh of the web pages.</param>
/// <param name="chunking">The options of the chunker.</param>
/// <param name="indexing">The options of the indexer.</param>
/// <param name="codeFiles">The lister of the files of a code source.</param>
/// <param name="code">The options of the code sources.</param>
/// <param name="graph">The step that builds the code graph of a code source.</param>
/// <param name="chunkers">The chunkers of code files.</param>
/// <param name="codeChunking">The limits of the chunks of code files.</param>
/// <param name="logger">The logger.</param>
public sealed class Indexer(
    SourceStore sources,
    DocumentStore documents,
    EmbeddingStore embeddings,
    IndexRunStore runs,
    EmbeddingModelRegistry models,
    IEmbeddingClient client,
    IPageClient pages,
    TimeProvider time,
    IOptionsMonitor<ChunkingOptions> chunking,
    IOptionsMonitor<IndexingOptions> indexing,
    CodeFileLister codeFiles,
    IOptionsMonitor<CodeOptions> code,
    CodeGraphBuilder graph,
    CodeChunkers chunkers,
    IOptionsMonitor<CodeChunkingOptions> codeChunking,
    ILogger<Indexer> logger)
{
    /// <summary>
    /// Gets the version of a code file: the layout of the stored chunks, the limits of the chunks and the chunker.
    /// A change of one of them reads the files again. A change of the reader of documents does not.
    /// </summary>
    /// <param name="options">The limits of the chunks.</param>
    /// <param name="chunker">The chunker of the language of the file.</param>
    /// <returns>The version, for example <c>code:2;2400;400;2;LineCodeChunker:1</c>.</returns>
    internal static string CodeFileVersion(CodeChunkingOptions options, ICodeChunker chunker) =>
        string.Create(CultureInfo.InvariantCulture, $"code:2;{options.Fingerprint()};{chunker.GetType().Name}:{chunker.Version}");

    // The files of code sources that are binary or hold a NUL character have no document, so the stored size and time cannot skip them.
    // The indexer remembers their size and time in memory. A restart forgets them and reads each file once.
    private readonly ConcurrentDictionary<(int SourceId, string Path), UnreadableFile> _unreadable = new();

    /// <summary>
    /// Forgets the skipped files of a source, for example after the source is deleted.
    /// </summary>
    /// <param name="sourceId">The key of the source.</param>
    public void Forget(int sourceId)
    {
        foreach ((int id, string path) in _unreadable.Keys)
        {
            if (id == sourceId)
                _unreadable.TryRemove((id, path), out _);
        }
    }

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
        {
            Forget(sourceId);
            return null;
        }

        IndexRun run = await runs.StartAsync(sourceId, cancellationToken);
        progress?.Invoke(run);
        logger.LogInformation("The indexer started the run {RunId} over the source {Source} in {Path}.", run.RunId, source.Name, source.RootPath);
        List<string> warnings = [];
        CodeFileChanges changes = new();
        try
        {
            await SyncFilesAsync(source, run, changes, warnings, progress, cancellationToken);
            await runs.SaveAsync(run, cancellationToken);
            await graph.BuildAsync(source, changes, warnings, cancellationToken);
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
        string.Create(CultureInfo.InvariantCulture, $"{DocumentReader.Version};{options.MaxChars};{options.AtomicMaxChars};{options.OverlapChars};{options.MinChars}");

    /// <summary>
    /// Gives the version of a file: the version of the parser with a hash of the replacements of the file.
    /// A change of the replacements of a file reads that file again.
    /// </summary>
    /// <param name="parserVersion">The version of the parser.</param>
    /// <param name="replacements">The replacements of the file.</param>
    /// <returns>The version, for example <c>8;1800;3000;300;400;3f2a9c01b7de</c>.</returns>
    internal static string FileVersion(string parserVersion, IReadOnlyList<TextReplacement> replacements) =>
        replacements.Count == 0 ? parserVersion
            : $"{parserVersion};{Hash(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(replacements.Select(static replacement => new[] { replacement.Find, replacement.Replace }))))[..12]}";

    /// <summary>
    /// Computes the SHA-256 of bytes in lowercase hexadecimal.
    /// </summary>
    internal static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private async Task SyncFilesAsync(Source source, IndexRun run, CodeFileChanges changes, List<string> warnings, Action<IndexRun>? progress, CancellationToken cancellationToken)
    {
        ChunkingOptions options = chunking.CurrentValue;
        Reader reader = new(source, new DocumentChunker(options), ParserVersion(options), await documents.GetFileStatesAsync(source.SourceId, cancellationToken), chunkers, codeChunking.CurrentValue);
        bool isCode = source.Kind == SourceKind.Code;
        CodeOptions codeOptions = code.CurrentValue;
        IReadOnlyList<string> paths = isCode ? await codeFiles.ListAsync(source, cancellationToken) : SourceScanner.Scan(source);
        run.FilesSeen = paths.Count + source.Urls.Count;
        List<string> skipped = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        SourceLinkGuard? guard = source.RootPath.Length > 0 ? new SourceLinkGuard(source.RootPath) : null;
        foreach (string path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileInfo info = new(Path.Combine(source.RootPath, path));
            if (!info.Exists)
                continue;

            if (guard is not null && !guard.IsInside(path))
            {
                logger.LogDebug("The indexer skipped the file {Path} of the source {Source}: it is a link to a place outside the folder.", path, source.Name);
                continue;
            }

            // A file over the limit is not seen, so its old document leaves the search.
            long limit = !isCode ? options.MaxFileBytes
                : CodeLanguages.IsConfig(CodeLanguages.FromPath(path)) ? codeOptions.MaxConfigFileBytes : codeOptions.MaxFileBytes;
            if (info.Length > limit)
            {
                if (isCode)
                    logger.LogDebug("The indexer skipped the file {Path} of the code source {Source}: {Size} bytes are over the limit {Limit}.", path, source.Name, info.Length, limit);
                else
                    skipped.Add(path);

                continue;
            }

            // PostgreSQL keeps microseconds, so the time of the file loses its last digit before the comparison.
            DateTime modifiedAt = Microseconds(info.LastWriteTimeUtc);
            if (isCode && _unreadable.TryGetValue((source.SourceId, path), out UnreadableFile known) && known.SizeBytes == info.Length && known.ModifiedAt == modifiedAt)
            {
                logger.LogDebug("The indexer skipped the file {Path} of the code source {Source}: it was binary or held a NUL character, and it did not change.", path, source.Name);
                continue;
            }

            seen.Add(path);
            (DocumentFileState? state, string fileVersion) = reader.State(path);
            if (state is not null && state.SizeBytes == info.Length && state.ModifiedAt == modifiedAt && state.ParserVersion == fileVersion)
            {
                run.DocumentsUnchanged++;
                continue;
            }

            byte[]? bytes;
            try
            {
                bytes = isCode ? await ReadCodeFileAsync(info.FullName, cancellationToken) : await File.ReadAllBytesAsync(info.FullName, cancellationToken);
            }
            // A locked or unreadable file must not stop the run.
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(exception, "The indexer cannot read the file {Path} of the source {Source}.", path, source.Name);
                run.DocumentsFailed++;
                continue;
            }

            if (bytes is null)
            {
                seen.Remove(path);
                _unreadable[(source.SourceId, path)] = new UnreadableFile(info.Length, modifiedAt);
                logger.LogDebug("The indexer skipped the binary file {Path} of the code source {Source}.", path, source.Name);
                continue;
            }

            if (isCode && CodeLanguages.FromPath(path) != CodeLanguages.Markdown)
            {
                if (await StoreCodeAsync(reader, path, bytes, modifiedAt, run, changes, cancellationToken))
                {
                    _unreadable.TryRemove((source.SourceId, path), out _);
                }
                else
                {
                    seen.Remove(path);
                    _unreadable[(source.SourceId, path)] = new UnreadableFile(info.Length, modifiedAt);
                    logger.LogDebug("The indexer skipped the file {Path} of the code source {Source}: the text holds a NUL character.", path, source.Name);
                }
            }
            else
            {
                if (isCode)
                    _unreadable.TryRemove((source.SourceId, path), out _);

                await StoreAsync(reader, path, bytes, modifiedAt, run, warnings, cancellationToken);
            }

            progress?.Invoke(run);
        }

        if (isCode)
        {
            // A file that left the list needs no entry any more.
            HashSet<string> listed = new(paths, StringComparer.Ordinal);
            foreach ((int sourceId, string path) in _unreadable.Keys)
            {
                if (sourceId == source.SourceId && !listed.Contains(path))
                    _unreadable.TryRemove((sourceId, path), out _);
            }
        }

        await SyncPagesAsync(reader, run, warnings, seen, progress, cancellationToken);

        // The rules of the categories may change without a change of the files, so each run gives every document its category again.
        // The rules may change while the run reads the files, so the run takes the current rules.
        await documents.ApplyCategoriesAsync(await sources.GetAsync(source.SourceId, null, cancellationToken) ?? source, cancellationToken);

        // An empty folder is more often a moment of a branch switch or an unplugged drive than a real deletion.
        // The vectors of a source take minutes to rebuild, so the indexer keeps the documents and warns.
        int storedFiles = reader.Known.Keys.Count(static path => !DocumentReader.IsWebPage(path));
        if (source.RootPath.Length > 0 && paths.Count == 0 && storedFiles > 0)
            warnings.Add($"The folder {source.RootPath} holds no matching files. The indexer kept the {storedFiles} stored documents. Delete the source to remove them.");
        else
        {
            changes.Removed.AddRange(reader.Known.Keys.Where(path => !DocumentReader.IsWebPage(path) && !seen.Contains(path)));
            run.DocumentsRemoved = await documents.DeleteMissingAsync(source.SourceId, seen, cancellationToken);
        }

        if (skipped.Count > 0)
        {
            warnings.Add(string.Create(CultureInfo.InvariantCulture,
                $"{skipped.Count} files are larger than {options.MaxFileBytes} bytes. The indexer skipped them and removed their old documents, for example {skipped[0]}."));
        }
    }

    // A page goes to its server at most once in the interval of the refresh. The time of the document is the time of the last request.
    // A new version of the parser or of the replacements requests the page at once. A page that fails keeps its old document.
    private async Task SyncPagesAsync(Reader reader, IndexRun run, List<string> warnings, HashSet<string> seen, Action<IndexRun>? progress, CancellationToken cancellationToken)
    {
        TimeSpan refresh = TimeSpan.FromHours(Math.Max(0, indexing.CurrentValue.PageRefreshHours));
        foreach (string url in reader.Source.Urls)
        {
            cancellationToken.ThrowIfCancellationRequested();
            seen.Add(url);
            DateTime now = Microseconds(time.GetUtcNow().UtcDateTime);
            (DocumentFileState? state, string fileVersion) = reader.State(url);
            if (state is not null && state.ParserVersion == fileVersion && now - state.ModifiedAt < refresh)
            {
                run.DocumentsUnchanged++;
                continue;
            }

            WebPage page;
            try
            {
                page = await pages.GetAsync(new Uri(url), chunking.CurrentValue.MaxFileBytes, cancellationToken);
            }
            catch (Exception exception) when (exception is HttpRequestException or InvalidDataException || (exception is TaskCanceledException && !cancellationToken.IsCancellationRequested))
            {
                logger.LogWarning("The indexer cannot get the page {Url} of the source {Source}: {Reason}", url, reader.Source.Name, exception.Message);
                warnings.Add($"The page {url} did not load: {exception.Message} The search keeps its last version.");
                run.DocumentsFailed++;
                continue;
            }

            await StoreAsync(reader, url, page.Bytes, now, run, warnings, cancellationToken, page.Address);
            progress?.Invoke(run);
        }
    }

    // Reads a file of a code source. A file with a NUL byte in its first 8 KB is binary and gives null.
    private static async Task<byte[]?> ReadCodeFileAsync(string fullName, CancellationToken cancellationToken)
    {
        await using FileStream stream = new(fullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] head = new byte[CodeText.BinaryProbeBytes];
        int read = await stream.ReadAtLeastAsync(head, head.Length, throwOnEndOfStream: false, cancellationToken);
        if (CodeText.IsBinary(head.AsSpan(0, read)))
            return null;

        using MemoryStream all = new();
        all.Write(head, 0, read);
        await stream.CopyToAsync(all, cancellationToken);
        return all.ToArray();
    }

    // Writes a file of a code source with its exact text. The result is false for a text that PostgreSQL cannot store.
    private async Task<bool> StoreCodeAsync(Reader reader, string path, byte[] bytes, DateTime modifiedAt, IndexRun run, CodeFileChanges changes, CancellationToken cancellationToken)
    {
        (DocumentFileState? state, string fileVersion) = reader.State(path);
        string hash = Hash(bytes);
        if (state is not null && state.ContentHash == hash && state.ParserVersion == fileVersion)
        {
            await documents.TouchAsync(state.DocumentId, bytes.LongLength, modifiedAt, cancellationToken);
            run.DocumentsUnchanged++;
            return true;
        }

        string content = CodeText.Decode(bytes);
        if (content.Contains('\0', StringComparison.Ordinal))
            return false;

        string language = CodeLanguages.FromPath(path);
        DocumentFile file = new(hash, fileVersion, bytes.LongLength, modifiedAt, SourceScanner.Category(reader.Source, path), language);
        List<CodeChunkRow> chunks = SplitCode(reader, path, language, content);
        run.ChunksReused += await documents.SaveCodeAsync(reader.Source.SourceId, path, Path.GetFileName(path), content, file, chunks, cancellationToken);
        if (state is null)
        {
            run.DocumentsAdded++;
            changes.Added.Add(path);
        }
        else
        {
            run.DocumentsUpdated++;
            if (state.ContentHash != hash)
                changes.Updated[path] = state.ContentHash;
        }

        logger.LogDebug("The indexer read {Path}: {Language}, {Lines} lines, {Chunks} chunks.", path, language, CodeText.SplitLines(content).Count, chunks.Count);
        return true;
    }

    // A chunker that fails on a file must not fail the run, so the lines of the file still reach the search.
    private List<CodeChunkRow> SplitCode(Reader reader, string path, string language, string content)
    {
        ICodeChunker chunker = reader.Chunkers.For(language);
        IReadOnlyList<CodeChunk> parts;
#pragma warning disable CA1031
        try
        {
            parts = chunker.Split(path, content, reader.CodeOptions);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
#pragma warning restore CA1031
        {
            logger.LogWarning(exception, "The chunker {Chunker} failed on the file {Path} of the source {Source}. The chunker of lines splits it.", chunker.GetType().Name, path, reader.Source.Name);
            parts = reader.Chunkers.Fallback.Split(path, content, reader.CodeOptions);
        }

        string kind = CodeLanguages.ChunkKind(language);
        List<CodeChunkRow> rows = new(parts.Count);
        foreach (CodeChunk part in parts)
        {
            int start = Math.Clamp(part.Start, 0, content.Length);
            int end = Math.Clamp(part.End, start, content.Length);
            if (end == start)
                continue;

            string body = content[start..end];
            rows.Add(new CodeChunkRow(
                rows.Count, part.Heading, body, start, end, part.LineStart, Math.Max(part.LineStart, part.LineEnd), part.OverlapChars, kind,
                Hash(Encoding.UTF8.GetBytes(TextChunk.Compose(part.Heading, body))), IdentifierTokens.Split(part.Heading), IdentifierTokens.Split(body)));
        }

        return rows;
    }

    // Reads a file or a page into chunks and writes them. Bytes with the stored hash only get their new time.
    private async Task StoreAsync(Reader reader, string path, byte[] bytes, DateTime modifiedAt, IndexRun run, List<string> warnings, CancellationToken cancellationToken, Uri? address = null)
    {
        (DocumentFileState? state, string fileVersion) = reader.State(path);
        IReadOnlyList<TextReplacement> replacements = SourceScanner.Replacements(reader.Source, path);
        if (state is not null && state.ContentHash == Hash(bytes) && state.ParserVersion == fileVersion)
        {
            await documents.TouchAsync(state.DocumentId, bytes.LongLength, modifiedAt, cancellationToken);
            run.DocumentsUnchanged++;
            return;
        }

        DocumentStructure structure;
        IReadOnlyList<TextChunk> chunks;
        try
        {
            // Every format becomes Markdown first. The structure and the chunks come from the Markdown.
            DocumentText text = DocumentReader.Read(path, bytes, address, address is null ? reader.Source.RootPath : null);
            structure = MarkdownStructureReader.Read(text with { Content = TextReplacement.ApplyAll(text.Content, replacements) }, path);
            chunks = reader.Chunker.Split(structure);
        }
        // A replacement that runs too long or breaks a mark fails its file with a warning that names the replacement.
        catch (Exception exception) when (exception is RegexMatchTimeoutException or InvalidOperationException && replacements.Count > 0)
        {
            logger.LogWarning(exception, "A replacement of the source {Source} failed on the file {Path}.", reader.Source.Name, path);
            warnings.Add($"A replacement failed on {path}: {exception.Message} Fix the replacements of the source.");
            run.DocumentsFailed++;
            return;
        }
        // A broken file must not stop the run. PdfPig throws many kinds of exceptions on a damaged PDF.
#pragma warning disable CA1031
        catch (Exception exception) when (exception is not (OperationCanceledException or Npgsql.NpgsqlException))
#pragma warning restore CA1031
        {
            logger.LogWarning(exception, "The indexer cannot read the file {Path} of the source {Source}.", path, reader.Source.Name);
            run.DocumentsFailed++;
            return;
        }

        DocumentFile file = new(Hash(bytes), fileVersion, bytes.LongLength, modifiedAt, SourceScanner.Category(reader.Source, path),
            reader.Source.Kind == SourceKind.Code ? CodeLanguages.Markdown : null);
        List<(TextChunk Chunk, string Hash)> hashed = [.. chunks.Select(static chunk => (chunk, Hash(Encoding.UTF8.GetBytes(chunk.EmbeddingText))))];
        run.ChunksReused += await documents.SaveAsync(reader.Source.SourceId, path, structure, file, hashed, cancellationToken);
        if (state is null)
            run.DocumentsAdded++;
        else
            run.DocumentsUpdated++;

        logger.LogDebug("The indexer read {Path}: {Chunks} chunks.", path, chunks.Count);
    }

    private static DateTime Microseconds(DateTime time) => new(time.Ticks - (time.Ticks % 10), DateTimeKind.Utc);

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

    private readonly record struct UnreadableFile(long SizeBytes, DateTime ModifiedAt);

    // The state of one run: the source, the chunker and the stored documents with their versions.
    private sealed record Reader(Source Source, DocumentChunker Chunker, string ParserVersion, Dictionary<string, DocumentFileState> Known, CodeChunkers Chunkers, CodeChunkingOptions CodeOptions)
    {
        public (DocumentFileState? State, string FileVersion) State(string path)
        {
            string language = CodeLanguages.FromPath(path);
            if (Source.Kind == SourceKind.Code && language != CodeLanguages.Markdown)
                return (Known.GetValueOrDefault(path), CodeFileVersion(CodeOptions, Chunkers.For(language)));

            return (Known.GetValueOrDefault(path), FileVersion(ParserVersion, SourceScanner.Replacements(Source, path)));
        }
    }
}
