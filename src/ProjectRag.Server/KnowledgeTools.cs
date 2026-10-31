using System.ComponentModel;

using ModelContextProtocol;
using ModelContextProtocol.Server;

using ProjectRag.Core.Documents;
using ProjectRag.Core.Embeddings;
using ProjectRag.Core.Indexing;
using ProjectRag.Core.Search;
using ProjectRag.Core.Sources;

namespace ProjectRag.Server;

/// <summary>
/// The tools of the MCP endpoint <c>/mcp</c>: the search, the lists of the categories and the sources, the search of documents by name, the read of a document and of a chunk with its links.
/// </summary>
/// <param name="search">The search.</param>
/// <param name="sources">The store of the sources.</param>
/// <param name="documents">The store of the documents.</param>
/// <param name="structure">The store of the sections and the links.</param>
/// <param name="models">The registry of the embedding model.</param>
/// <param name="scheduler">The scheduler of the indexer.</param>
[McpServerToolType]
internal sealed class KnowledgeTools(SearchService search, SourceStore sources, DocumentStore documents, StructureStore structure, EmbeddingModelRegistry models, IndexScheduler scheduler)
{
    /// <summary>
    /// The text that the server gives an agent at the start of the session.
    /// </summary>
    public const string Instructions = """
        This server is a local knowledge base of the technical and project documentation of the user.
        Call list_categories first to learn the topics, such as csharp, vulkan or opengl. Call search with a question or keywords in English or Russian,
        and narrow it with categories when the question names a topic. list_sources gives the folders that hold the documents.
        A hit gives the source, the path of the document, the path of its section and the text.
        To follow a hit, call read_chunk with its chunkId: it gives the section, the pages, the previous and the next chunk, the links of the chunk with their target chunks, and the chunks that link to it.
        To read more of a document, call read_document with the source, the path and an offset a little before charStart.
        Call find_documents when you know a part of a file name or a title.
        The texts are Markdown. Formulas are LaTeX between $ or $$, and code is in backticks or fenced blocks.
        """;

    private const int DefaultReadLength = 20_000;

    [McpServerTool(Name = "list_sources", Title = "List the sources", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Lists the sources of the knowledge base: one source per project, with its description and the numbers of documents and chunks.")]
    public async Task<IReadOnlyList<SourceInfo>> ListSourcesAsync(CancellationToken cancellationToken)
    {
        short? modelId = (await models.GetStatusAsync(cancellationToken)).Model?.ModelId;
        IReadOnlyList<Source> list = await sources.ListAsync(modelId, cancellationToken);
        return [.. list.Select(static source => new SourceInfo(source.Name, source.Description, source.DocumentCount, source.ChunkCount, source.EmbeddedCount, source.IndexedAt))];
    }

    [McpServerTool(Name = "list_categories", Title = "List the categories", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Lists the categories of the documents, such as csharp, vulkan or opengl, with the numbers of documents and chunks. A category is a topic of the documentation.")]
    public async Task<IReadOnlyList<CategoryInfo>> ListCategoriesAsync(CancellationToken cancellationToken)
    {
        short? modelId = (await models.GetStatusAsync(cancellationToken)).Model?.ModelId;
        return await documents.GetCategoriesAsync(modelId, cancellationToken);
    }

    [McpServerTool(Name = "search", Title = "Search the documentation", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Searches the documentation with a hybrid of vector and full text search, then reorders the hits with a reranker. Returns chunks of documents, the best first.")]
    public async Task<SearchResult> SearchAsync(
        [Description("A question or keywords, in English or Russian.")] string query,
        [Description("The categories to search, such as csharp or vulkan. Leave it out to search all categories.")] string[]? categories = null,
        [Description("The names of the sources to search. Leave it out to search all sources.")] string[]? sources = null,
        [Description("The number of hits, from 1 to 50. The default is 8.")] int? limit = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await search.SearchAsync(new SearchRequest { Query = query, Categories = categories, Sources = sources, Limit = limit }, cancellationToken);
        }
        catch (ArgumentException exception)
        {
            throw new McpException(exception.Message, exception);
        }
    }

    [McpServerTool(Name = "find_documents", Title = "Find documents by name", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Finds documents by a part of the path or the title. The comparison forgives small typos. Without a text it lists the documents.")]
    public async Task<IReadOnlyList<DocumentSummary>> FindDocumentsAsync(
        [Description("A part of the path or the title. Leave it out to list all documents.")] string? text = null,
        [Description("The name of a source. Leave it out to search all sources.")] string? source = null,
        [Description("A category, such as csharp. Leave it out to search all categories.")] string? category = null,
        [Description("The most documents, from 1 to 100. The default is 20.")] int? limit = null,
        CancellationToken cancellationToken = default)
    {
        int[] sourceIds = [];
        if (!string.IsNullOrWhiteSpace(source))
        {
            IReadOnlyDictionary<string, int> found = await sources.FindIdsAsync([source.Trim()], cancellationToken);
            if (found.Count == 0)
                throw new McpException($"The knowledge base has no source {source}. Call list_sources.");

            sourceIds = [.. found.Values];
        }

        if (!string.IsNullOrWhiteSpace(category) && !(await documents.GetCategoriesAsync(null, cancellationToken)).Any(item => item.Name == category.Trim()))
            throw new McpException($"The knowledge base has no category {category}. Call list_categories.");

        (IReadOnlyList<DocumentSummary> items, _) = await documents.ListAsync(sourceIds, category ?? string.Empty, text ?? string.Empty, 0, Math.Clamp(limit ?? 20, 1, 100), cancellationToken);
        return items;
    }

    [McpServerTool(Name = "read_document", Title = "Read a document", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Reads the text of a document from an offset. A PDF document reads as text with its bookmarks as Markdown headings. Use nextOffset to read on.")]
    public async Task<DocumentPart> ReadDocumentAsync(
        [Description("The name of the source.")] string source,
        [Description("The path of the document in the source, as search or find_documents returns it.")] string path,
        [Description("The index of the first character. The default is 0.")] int? offset = null,
        [Description("The most characters, up to 100000. The default is 20000.")] int? length = null,
        CancellationToken cancellationToken = default)
    {
        DocumentSummary document = await documents.FindAsync(source, path, cancellationToken)
            ?? throw new McpException($"The source {source} has no document {path}. Call find_documents.");
        DocumentContent content = await documents.GetContentAsync(document, offset ?? 0, length ?? DefaultReadLength, cancellationToken);
        int end = content.Offset + content.Text.Length;
        return new DocumentPart(document.Source, document.Path, document.Title, content.Offset, content.TotalLength, end < content.TotalLength ? end : null, content.Text);
    }

    [McpServerTool(Name = "read_chunk", Title = "Read a chunk with its links", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Reads a chunk by its chunkId from search: its text, its section and pages, the previous and the next chunk, its links with their target chunks, and the chunks that link to it.")]
    public async Task<ChunkDetail> ReadChunkAsync([Description("The chunkId of a hit or of a link.")] long chunkId, CancellationToken cancellationToken = default)
    {
        short? modelId = (await models.GetStatusAsync(cancellationToken)).Model?.ModelId;
        return await structure.GetChunkAsync(chunkId, modelId, cancellationToken) ?? throw new McpException($"The knowledge base has no chunk {chunkId}.");
    }

    [McpServerTool(Name = "index_source", Title = "Index a source", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Puts a source into the queue of the indexer, for example after a change of its documents. The indexer reads only the changed files.")]
    public async Task<string> IndexSourceAsync([Description("The name of the source.")] string source, CancellationToken cancellationToken = default)
    {
        IReadOnlyDictionary<string, int> found = await sources.FindIdsAsync([source.Trim()], cancellationToken);
        if (found.Count == 0)
            throw new McpException($"The knowledge base has no source {source}. Call list_sources.");

        return scheduler.Enqueue(found.Values.First())
            ? $"The source {source} is in the queue of the indexer."
            : $"The source {source} was in the queue already.";
    }

    /// <summary>
    /// A source as an agent sees it.
    /// </summary>
    /// <param name="Name">The name for the other tools.</param>
    /// <param name="Description">What the documents are about.</param>
    /// <param name="Documents">The number of documents.</param>
    /// <param name="Chunks">The number of chunks.</param>
    /// <param name="EmbeddedChunks">The number of chunks with a vector. Fewer than <paramref name="Chunks"/> means the indexer is not done.</param>
    /// <param name="IndexedAt">The end of the last successful run of the indexer.</param>
    internal sealed record SourceInfo(string Name, string Description, long Documents, long Chunks, long EmbeddedChunks, DateTime? IndexedAt);

    /// <summary>
    /// A part of a document.
    /// </summary>
    /// <param name="Source">The name of the source.</param>
    /// <param name="Path">The path of the document.</param>
    /// <param name="Title">The title.</param>
    /// <param name="Offset">The index of the first character of <paramref name="Text"/>.</param>
    /// <param name="TotalLength">The number of characters of the document.</param>
    /// <param name="NextOffset">The offset of the next part, or <see langword="null"/> at the end.</param>
    /// <param name="Text">The text.</param>
    internal sealed record DocumentPart(string Source, string Path, string Title, int Offset, int TotalLength, int? NextOffset, string Text);
}
