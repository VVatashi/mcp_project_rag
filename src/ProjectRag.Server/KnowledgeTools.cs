using System.ComponentModel;
using System.Globalization;
using System.Text;

using ModelContextProtocol;
using ModelContextProtocol.Server;

using ProjectRag.Core.Code;
using ProjectRag.Core.Code.Graph;
using ProjectRag.Core.Documents;
using ProjectRag.Core.Embeddings;
using ProjectRag.Core.Indexing;
using ProjectRag.Core.Search;
using ProjectRag.Core.Sources;

namespace ProjectRag.Server;

/// <summary>
/// The tools of the MCP endpoint <c>/mcp</c>: the search, the lists of the categories and the sources, the search of documents by name, the read of a document and of a chunk with its links,
/// the exact search and the read of lines in the code sources, and the symbol tools over the code graph of a code source.
/// </summary>
/// <param name="search">The search.</param>
/// <param name="sources">The store of the sources.</param>
/// <param name="documents">The store of the documents.</param>
/// <param name="structure">The store of the sections and the links.</param>
/// <param name="models">The registry of the embedding model.</param>
/// <param name="scheduler">The scheduler of the indexer.</param>
/// <param name="code">The exact search of the code sources.</param>
/// <param name="graph">The queries over the code graph.</param>
/// <param name="codeSearch">The search in tiers over the code sources.</param>
[McpServerToolType]
internal sealed class KnowledgeTools(
    SearchService search, SourceStore sources, DocumentStore documents, StructureStore structure, EmbeddingModelRegistry models, IndexScheduler scheduler, CodeSearchService code, CodeGraphService graph, CodeTierSearch codeSearch)
{
    /// <summary>
    /// The text that the server gives an agent at the start of the session.
    /// </summary>
    public const string Instructions = """
        This server is a local knowledge base of the technical and project documentation of the user, and of the code bases that the user added.
        The tool search covers documentation and Markdown only. It never returns source code. For source code call code_search, see below.
        Call list_categories first to learn the topics, such as csharp, vulkan or opengl. Call search with a question or keywords in English or Russian,
        and narrow it with categories when the question names a topic. list_sources gives the folders and the web pages that hold the documents, with their categories.
        A hit gives the source, the path of the document, the path of its section and the text.
        The copies of a hit are chunks of other places with almost the same text, such as the core and the compatibility profile of OpenGL.
        A copy with a similarity below 1 differs in a few words. Read it with read_chunk when the difference matters.
        To follow a hit, call read_chunk with its chunkId: it gives the section, the pages, the previous and the next chunk, the links of the chunk with their target chunks, and the chunks that link to it.
        To read more of a document, call read_document with the source, the path and an offset a little before charStart.
        Call find_documents when you know a part of a file name or a title.
        The texts are Markdown. Formulas are LaTeX between $ or $$, and code is in backticks or fenced blocks.
        A source of the kind code is a local code base. Its Markdown files appear in search like other documents. Its other files are not in search.
        For an open question about a code base, such as "where is X done" or "how does Y work", call code_search first. It answers in tiers:
        symbols whose name matches, lines that hold the exact text, and code that is similar in meaning. Use the tool that fits a narrow question instead:
        code_find_symbol when you know a name, code_grep when you know an exact string or a regular expression.
        To find an exact string or a regular expression in a code base, call code_grep. It gives the files and the lines of each match, in the order of the paths.
        To read the lines around a match, call code_read with the codebase, the path and a range of lines. Use code_grep for names of types and methods, and search for questions about concepts in documents.
        A C# code source also has a code graph from the compiler, when the indexer built it. Prefer its tools to code_grep for questions about symbols:
        code_find_symbol finds a type or a member by its name, a part of it or its humps such as SSync, and gives the signature and the place. Use it first to learn the exact name.
        code_outline gives the symbols of a file, or the members of a type with its bases and derived types. Use it instead of reading a whole file.
        code_relations gives what a type uses and what uses it, the hierarchy, or the callers and callees of a method. Use it to judge the effect of a change.
        code_references lists every use of a symbol with its line, grouped by file. Unlike code_grep it does not mix in other symbols with the same name or text in comments.
        code_map gives the projects, the namespaces of a project and the types of a namespace. Use it to get a first picture of an unknown code base.
        Use code_grep for text that is no symbol: strings, comments, settings, other languages. A graph shows the default build of the code base, so code under an inactive #if is only in code_grep.
        If a graph tool says that the graph is missing, use code_grep and code_read.
        """;

    private const int DefaultReadLength = 20_000;
    private const int DefaultCodeReadLines = 200;
    private const int MaxCodeReadLines = 500;

    [McpServerTool(Name = "list_sources", Title = "List the sources", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Lists the sources of the knowledge base: folders and web pages. A source gives its kind (docs or code), its description, the categories of its documents, its urls and its counts.")]
    public async Task<IReadOnlyList<SourceInfo>> ListSourcesAsync(CancellationToken cancellationToken)
    {
        short? modelId = (await models.GetStatusAsync(cancellationToken)).Model?.ModelId;
        IReadOnlyList<Source> list = await sources.ListAsync(modelId, cancellationToken);
        return [.. list.Select(static source => new SourceInfo(source.Name, source.Kind, source.Description, source.Categories, source.Urls, source.DocumentCount, source.ChunkCount, source.EmbeddedCount, source.IndexedAt))];
    }

    [McpServerTool(Name = "list_categories", Title = "List the categories", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Lists the categories of the documents, such as csharp, vulkan or opengl, with the numbers of documents and chunks. A category is a topic of the documentation.")]
    public async Task<IReadOnlyList<CategoryInfo>> ListCategoriesAsync(CancellationToken cancellationToken)
    {
        short? modelId = (await models.GetStatusAsync(cancellationToken)).Model?.ModelId;
        return await documents.GetCategoriesAsync(modelId, cancellationToken);
    }

    [McpServerTool(Name = "search", Title = "Search the documentation", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Searches the documentation and the Markdown files of the code sources with a hybrid of vector and full text search, then reorders the hits with a reranker. Returns chunks of documents, the best first. "
        + "A text that several places share, maybe with a few other words, appears once, with the other places in its copies. It does not search source code or configuration files: call code_search for them.")]
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

    [McpServerTool(Name = "code_grep", Title = "Find exact text in code", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Finds the lines of the files of the code sources that hold an exact text or match a regular expression of .NET. A line has one match. "
        + "The answer groups the lines by file, in the order of the paths: the path, then lines like `  42: text`. Use it to find a name, a call, a setting or a message. "
        + "A line longer than 400 characters shows 400 characters around the match, and a cut start shows as `…`. Call code_read to see the lines around a match.")]
    public async Task<string> CodeGrepAsync(
        [Description("The text to find, or the regular expression when regex is true. It is one line.")] string pattern,
        [Description("The name of a code source, as list_sources gives it. Leave it out to search all code sources. Then a path in the answer starts with the name and a colon.")] string? codebase = null,
        [Description("True when the pattern is a regular expression of .NET. The default is false: a literal text.")] bool regex = false,
        [Description("False ignores the case of letters. The default is true.")] bool caseSensitive = true,
        [Description("A pattern of the paths, such as src/**/*.cs. A pattern without a slash, such as *.cs, matches the name of a file in any folder.")] string? path = null,
        [Description("The language of the files, such as csharp, json, yaml, markdown or glsl.")] string? language = null,
        [Description("The most matches, from 1 to 1000. The default is 50.")] int? limit = null,
        CancellationToken cancellationToken = default)
    {
        int max = Math.Clamp(limit ?? 50, 1, CodeSearchService.MaxLimit);
        CodeGrepResult result;
        try
        {
            result = await code.GrepAsync(new CodeGrepRequest { Pattern = pattern, Source = codebase, IsRegex = regex, CaseSensitive = caseSensitive, Path = path, Language = language, Limit = max }, cancellationToken);
        }
        catch (CodeSearchException exception)
        {
            throw new McpException(exception.Message, exception);
        }

        if (result.Matches.Count == 0)
            return "No matches.";

        StringBuilder text = new();
        long documentId = -1;
        foreach (CodeMatch match in result.Matches)
        {
            if (match.DocumentId != documentId)
            {
                documentId = match.DocumentId;
                text.AppendLine(codebase is null ? $"{match.Source}:{match.Path}" : match.Path);
            }

            text.AppendLine(CultureInfo.InvariantCulture, $"  {match.Line}: {(match.TextStart > 0 ? "…" : string.Empty)}{match.Text}");
        }

        if (result.Truncated)
            text.AppendLine(CultureInfo.InvariantCulture, $"The answer stops at {max} matches. Narrow the pattern, or set path or language.");

        return text.ToString();
    }

    [McpServerTool(Name = "code_read", Title = "Read lines of a code file", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Reads lines of a file of a code source, as the indexer saw it. The answer starts with `path (language), lines a-b of N`. Then each line follows as `n| text`. "
        + "It gives 200 lines by default and 500 at most. Use the paths and the line numbers from code_grep.")]
    public async Task<string> CodeReadAsync(
        [Description("The name of the code source.")] string codebase,
        [Description("The path of the file relative to the folder of the source, as code_grep returns it.")] string path,
        [Description("The number of the first line, from 1. The default is 1.")] int? startLine = null,
        [Description("The number of the last line. The default is 199 lines after the first.")] int? endLine = null,
        CancellationToken cancellationToken = default)
    {
        DocumentSummary document = await documents.FindAsync(codebase, path, cancellationToken)
            ?? throw new McpException($"The source {codebase} has no file {path}. Call code_grep or find_documents.");
        if (document.Language is null)
            throw new McpException($"The source {codebase} is not a code source. Call read_document for its documents.");

        int start = Math.Max(1, startLine ?? 1);
        int end = (int)Math.Min(Math.Min(endLine ?? (long)start + DefaultCodeReadLines - 1, (long)start + MaxCodeReadLines - 1), int.MaxValue);
        CodeLines lines = await code.ReadLinesAsync(document.DocumentId, start, end, cancellationToken)
            ?? throw new McpException($"The source {codebase} has no file {path}.");

        StringBuilder text = new();
        if (lines.Lines.Count == 0)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"{document.Path} ({document.Language}), no lines from {start}, the file has {lines.LineCount}");
            return text.ToString();
        }

        text.AppendLine(CultureInfo.InvariantCulture, $"{document.Path} ({document.Language}), lines {lines.Start}-{lines.End} of {lines.LineCount}");
        for (int i = 0; i < lines.Lines.Count; i++)
            text.AppendLine(CultureInfo.InvariantCulture, $"{lines.Start + i}| {lines.Lines[i]}");

        if (lines.End < lines.LineCount)
            text.AppendLine(CultureInfo.InvariantCulture, $"The file goes on. Call code_read with startLine {lines.End + 1}.");

        return text.ToString();
    }

    [McpServerTool(Name = "code_search", Title = "Search a code base", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Searches the files of the code sources for an open question or a name, in three tiers. Use it first for questions such as `where is the scene synchronized` or `how does the frame loop pick a swapchain`. "
        + "Use code_find_symbol instead when you know the name of a type or a member, and code_grep when you know an exact string or a regular expression. "
        + "The answer has up to three sections, and a section without items stays out. `Symbols:` lists the symbols of the code graph whose name matches the query, one line each as code_find_symbol shows them. "
        + "It runs for a query without spaces. `Exact matches:` lists the lines that hold the query as a text, grouped by file like code_grep. It tries the exact case first. "
        + "`Similar code:` lists chunks of code or configuration that are similar in meaning or share identifiers with the query, each as `path:lineStart-lineEnd  heading  (score)` with its first 12 lines as `  n| text`, and `  …` when it goes on. "
        + "Call code_read with the lines of a chunk to read more. The query may be a description in words, because identifiers such as SceneHost match their parts.")]
    public async Task<string> CodeSearchAsync(
        [Description("A name, a text of code or a description in words. It holds at most 500 characters.")] string query,
        [Description("The name of a code source, as list_sources gives it. Leave it out to search all code sources. Then a path in the answer starts with the name and a colon.")] string? codebase = null,
        [Description("A pattern of the paths, such as src/**/*.cs. A pattern without a slash, such as *.cs, matches the name of a file in any folder.")] string? path = null,
        [Description("The language of the files, such as csharp, json, yaml or glsl.")] string? language = null,
        [Description("False drops the files of test projects. The default is true.")] bool includeTests = true,
        [Description("The number of chunks of similar code, from 1 to 50. The default is 8.")] int? limit = null,
        [Description("True reorders the chunks with the reranker, false keeps the order of the search. Leave it out for the setting of the server.")] bool? rerank = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            CodeSearchResult result = await codeSearch.SearchAsync(
                new CodeSearchRequest { Query = query, Source = codebase, Path = path, Language = language, IncludeTests = includeTests, Limit = limit ?? 8, Rerank = rerank }, cancellationToken);
            return CodeSearchFormatter.Format(result, string.IsNullOrWhiteSpace(codebase));
        }
        catch (CodeSearchException exception)
        {
            throw new McpException(exception.Message, exception);
        }
    }

    [McpServerTool(Name = "code_find_symbol", Title = "Find a symbol in a code base", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Finds types and members of a C# code base by name, as Go to symbol of an IDE does. The query is a name, a part of a name, the humps of a name (SSync finds SceneSynchronizer) or a qualified name (SceneHost.Update). "
        + "The answer has a line for each match: `kind fullName — signature — project — path:line`, then the first sentence of its documentation. The best matches come first: exact name, prefix, humps, qualified name, part of a name, similar name. "
        + "Use it to learn the exact name and the place of a symbol before code_outline, code_relations or code_references.")]
    public async Task<string> CodeFindSymbolAsync(
        [Description("A name, a part of a name, camel humps such as SSync, or a qualified suffix such as SceneHost.Update.")] string query,
        [Description("The name of a code source. Leave it out when the knowledge base has one code source.")] string? codebase = null,
        [Description("A kind: class, record, struct, record_struct, interface, enum, delegate, method, constructor, property, indexer, field, constant, event or enum_member. `type` means all kinds of types.")] string? kind = null,
        [Description("The name of an assembly to keep. Leave it out for all projects.")] string? project = null,
        [Description("The most symbols, from 1 to 200. The default is 20.")] int? limit = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            Source source = await graph.RequireGraphAsync(codebase, cancellationToken);
            return CodeGraphFormatter.Symbols(await graph.SearchSymbolsAsync(query, source.SourceId, kind, project, Math.Clamp(limit ?? 20, 1, CodeGraphService.MaxSearchLimit), cancellationToken));
        }
        catch (CodeSearchException exception)
        {
            throw new McpException(exception.Message, exception);
        }
    }

    [McpServerTool(Name = "code_outline", Title = "Outline a file or a type", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Shows the structure of C# code without its bodies. With a path it lists the symbols of the file as an indented tree, each as `line: signature`. "
        + "With a symbol, which is a type, it lists the members of the type as `line: signature — first sentence of the documentation`, then its base types and derived types. Give a path or a symbol, not both. "
        + "A member from another file of a partial type shows its path. Use code_read with the line to read the body.")]
    public async Task<string> CodeOutlineAsync(
        [Description("The name of a code source. Leave it out when the knowledge base has one code source.")] string? codebase = null,
        [Description("The path of a C# file relative to the folder of the source.")] string? path = null,
        [Description("A type: a full name, a name, or a documentation ID such as T:Ns.Type.")] string? symbol = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path) == string.IsNullOrWhiteSpace(symbol))
            throw new McpException("Give either a path or a symbol.");

        try
        {
            Source source = await graph.RequireGraphAsync(codebase, cancellationToken);
            if (!string.IsNullOrWhiteSpace(path))
            {
                DocumentSummary document = await documents.FindAsync(source.Name, path.Trim().Replace('\\', '/'), cancellationToken)
                    ?? throw new McpException($"The source {source.Name} has no file {path}. Call code_grep or find_documents.");
                return CodeGraphFormatter.OutlineFile(document.Path, await graph.GetOutlineAsync(document.DocumentId, cancellationToken));
            }

            (_, CodeSymbolGroup group) = await graph.ResolveOneAsync(source.Name, symbol!, cancellationToken);
            StringBuilder text = new();
            foreach (SymbolSummary item in group.Symbols)
            {
                SymbolDetail detail = await graph.GetDetailAsync(item.SymbolId, cancellationToken) ?? throw new McpException($"The symbol {symbol} is gone. Build the graph again.");
                text.AppendLine(group.IsType
                    ? CodeGraphFormatter.OutlineType(detail)
                    : $"{item.Kind} {item.FullName} — {item.Project} — {CodeGraphFormatter.Place(item)}{Environment.NewLine}{item.Signature}{Environment.NewLine}{CodeGraphFormatter.FirstSentence(item.Summary)}");
            }

            return text.ToString();
        }
        catch (CodeSearchException exception)
        {
            throw new McpException(exception.Message, exception);
        }
    }

    [McpServerTool(Name = "code_relations", Title = "Show the relations of a symbol", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Shows how a symbol connects to the rest of a C# code base. For a type: the types that it uses and the types that use it, each with the kinds of the uses and their counts, and its hierarchy (base types, interfaces, derived types). "
        + "For a member: its callers and callees, and the members that it overrides or implements and that override or implement it. The overloads of a method count as one symbol. "
        + "The kinds of uses are inherit, implement, signature, create, call, member, type and attribute. A depth of 2 or 3 follows the edges between types further and stops at 60 nodes. "
        + "Use it to judge what a change affects.")]
    public async Task<string> CodeRelationsAsync(
        [Description("A type or a member: a full name, a qualified suffix such as SceneHost.Update, a name, or a documentation ID such as M:Ns.Type.Update(System.Double).")] string symbol,
        [Description("The name of a code source. Leave it out when the knowledge base has one code source.")] string? codebase = null,
        [Description("uses (what the symbol uses), usedBy (what uses it) or both. The default is both.")] string? direction = null,
        [Description("The kinds of uses to keep: inherit, implement, signature, create, call, member, type or attribute. Leave it out for all kinds.")] string[]? kinds = null,
        [Description("The depth of the walk over types, from 1 to 3. The default is 1. A member always uses 1.")] int? depth = null,
        [Description("True lists the external types that a type uses, such as System.Collections.Generic.List<T>. The default is false.")] bool includeExternal = false,
        [Description("False drops the symbols of test projects. The default is true.")] bool includeTests = true,
        CancellationToken cancellationToken = default)
    {
        string mode = (direction ?? "both").Trim().Replace("_", string.Empty, StringComparison.Ordinal).Replace("-", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
        CodeRelationDirection parsed = mode switch
        {
            "both" => CodeRelationDirection.Both,
            "uses" => CodeRelationDirection.Uses,
            "usedby" => CodeRelationDirection.UsedBy,
            _ => throw new McpException("The direction is uses, usedBy or both."),
        };
        HashSet<string>? kindSet = null;
        if (kinds is { Length: > 0 })
        {
            kindSet = [.. kinds.Select(static kind => kind.Trim().ToLowerInvariant())];
            if (kindSet.FirstOrDefault(static kind => !CodeUseKinds.All.Contains(kind)) is { } wrong)
                throw new McpException($"The kind {wrong} is unknown. The kinds are {string.Join(", ", CodeUseKinds.All)}.");
        }

        try
        {
            (_, CodeSymbolGroup group) = await graph.ResolveOneAsync(codebase, symbol, cancellationToken);
            CodeRelations relations = await graph.GetRelationsAsync(group, new CodeRelationOptions(parsed, kindSet, depth ?? 1, includeExternal, includeTests), cancellationToken);
            return CodeGraphFormatter.Relations(relations);
        }
        catch (CodeSearchException exception)
        {
            throw new McpException(exception.Message, exception);
        }
    }

    [McpServerTool(Name = "code_references", Title = "List the references to a symbol", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Lists every reference to a type or a member of a C# code base that the compiler resolved, so it ignores other symbols with the same name and text in comments or strings. "
        + "The answer groups the references by file: the path, then lines like `  42:17 [call] text   (in Type.Member)`. The kind is inherit, implement, signature, create, call, member, type or attribute, and `in` names the member around the reference. "
        + "The overloads of a method count as one symbol. Use code_grep for text that is no symbol.")]
    public async Task<string> CodeReferencesAsync(
        [Description("A type or a member: a full name, a qualified suffix such as SceneHost.Update, a name, or a documentation ID.")] string symbol,
        [Description("The name of a code source. Leave it out when the knowledge base has one code source.")] string? codebase = null,
        [Description("One kind of use to keep: inherit, implement, signature, create, call, member, type or attribute.")] string? kind = null,
        [Description("A file or a folder (with a final slash) relative to the folder of the source. Leave it out for all files.")] string? path = null,
        [Description("The most references, from 1 to 500. The default is 100.")] int? limit = null,
        CancellationToken cancellationToken = default)
    {
        int max = Math.Clamp(limit ?? 100, 1, CodeGraphService.MaxReferenceLimit);
        try
        {
            (_, CodeSymbolGroup group) = await graph.ResolveOneAsync(codebase, symbol, cancellationToken);
            return CodeGraphFormatter.References(await graph.GetReferencesAsync(group.Ids, kind, path, 0, max, cancellationToken), max);
        }
        catch (CodeSearchException exception)
        {
            throw new McpException(exception.Message, exception);
        }
    }

    [McpServerTool(Name = "code_map", Title = "Map a code base", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Gives a map of a C# code base. Without arguments it lists the projects with their target framework, test flag, files, types, compiler errors and project references, the solutions, and the state of the graph (time of the build, SDK, last error). "
        + "With a project it lists the namespaces of the project with their numbers of types and the strongest dependencies between them. With a namespace it lists its types and, for each, the 10 types that it uses most and the 10 types that use it most. "
        + "Use it first when the code base is new to you.")]
    public async Task<string> CodeMapAsync(
        [Description("The name of a code source. Leave it out when the knowledge base has one code source.")] string? codebase = null,
        [Description("The name of an assembly, as the map without arguments shows it.")] string? project = null,
        [Description("A namespace, such as Axion.Core.Scene. The project is optional with it.")] string? @namespace = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            Source source = await graph.RequireGraphAsync(codebase, cancellationToken);
            if (!string.IsNullOrWhiteSpace(@namespace))
                return CodeGraphFormatter.NamespaceMap(await graph.GetNamespaceMapAsync(source.SourceId, @namespace, project, cancellationToken));

            if (!string.IsNullOrWhiteSpace(project))
            {
                CodeProjectMap map = await graph.GetProjectMapAsync(source.SourceId, project, cancellationToken)
                    ?? throw new McpException($"The code source {source.Name} has no project {project}. Call code_map without arguments to list the projects.");
                return CodeGraphFormatter.ProjectMap(map);
            }

            return CodeGraphFormatter.MapOverview(await graph.GetMapOverviewAsync(source.SourceId, cancellationToken) ?? throw new McpException($"The code source {source.Name} is gone."));
        }
        catch (CodeSearchException exception)
        {
            throw new McpException(exception.Message, exception);
        }
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
    /// <param name="Kind">The kind: docs for documentation, code for a local code base. The tools code_grep and code_read read the sources of the kind code.</param>
    /// <param name="Description">What the documents are about.</param>
    /// <param name="Categories">The categories of the documents of the source. A search narrows to them with its categories.</param>
    /// <param name="Urls">The addresses of the web pages of the source. The address of a page is the path of its document.</param>
    /// <param name="Documents">The number of documents.</param>
    /// <param name="Chunks">The number of chunks.</param>
    /// <param name="EmbeddedChunks">The number of chunks with a vector. Fewer than <paramref name="Chunks"/> means the indexer is not done.</param>
    /// <param name="IndexedAt">The end of the last successful run of the indexer.</param>
    internal sealed record SourceInfo(string Name, string Kind, string Description, IReadOnlyList<string> Categories, IReadOnlyList<string> Urls, long Documents, long Chunks, long EmbeddedChunks, DateTime? IndexedAt);

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
