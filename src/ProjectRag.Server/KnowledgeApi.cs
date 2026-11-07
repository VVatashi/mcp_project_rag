using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

using Npgsql;

using ProjectRag.Core.Code;
using ProjectRag.Core.Code.Diagrams;
using ProjectRag.Core.Code.Graph;
using ProjectRag.Core.Documents;
using ProjectRag.Core.Embeddings;
using ProjectRag.Core.Indexing;
using ProjectRag.Core.Search;
using ProjectRag.Core.Sources;
using ProjectRag.Core.Status;

namespace ProjectRag.Server;

/// <summary>
/// The web API of the knowledge base under <c>/api</c>. The dashboard uses it, and so can scripts and agents without MCP.
/// </summary>
internal static class KnowledgeApi
{
    private const int MaxPageSize = 500;

    /// <summary>
    /// Maps the endpoints.
    /// </summary>
    /// <param name="routes">The routes of the application.</param>
    public static void MapKnowledgeApi(this IEndpointRouteBuilder routes)
    {
        RouteGroupBuilder api = routes.MapGroup("/api");

        api.MapGet("/status", static (StatusService status, CancellationToken cancellationToken) => status.GetAsync(cancellationToken));

        api.MapGet("/sources", static async (SourceStore sources, EmbeddingModelRegistry models, CancellationToken cancellationToken) =>
            await sources.ListAsync(await CurrentModelIdAsync(models, cancellationToken), cancellationToken));

        api.MapGet("/sources/{sourceId:int}", static async Task<Results<Ok<Source>, NotFound>> (int sourceId, SourceStore sources, EmbeddingModelRegistry models, CancellationToken cancellationToken) =>
            await sources.GetAsync(sourceId, await CurrentModelIdAsync(models, cancellationToken), cancellationToken) is { } source ? TypedResults.Ok(source) : TypedResults.NotFound());

        api.MapPost("/sources", static async Task<Results<Created<Source>, ValidationProblem, Conflict<string>>> (SourceInput input, SourceStore sources, IndexScheduler scheduler, CancellationToken cancellationToken) =>
        {
            Normalize(input);
            if (input.Validate() is { Count: > 0 } errors)
                return TypedResults.ValidationProblem(errors);

            try
            {
                int sourceId = await sources.AddAsync(input, cancellationToken);
                scheduler.Enqueue(sourceId);
                Source source = (await sources.GetAsync(sourceId, null, cancellationToken))!;
                return TypedResults.Created($"/api/sources/{sourceId}", source);
            }
            catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                return TypedResults.Conflict($"A source with the name {input.Name} exists.");
            }
        });

        api.MapPut("/sources/{sourceId:int}", static async Task<Results<Ok<Source>, NotFound, ValidationProblem, Conflict<string>>> (int sourceId, SourceInput input, SourceStore sources, DocumentStore documents, IndexScheduler scheduler, CodeGraphBuilder graphBuilder, CancellationToken cancellationToken) =>
        {
            Normalize(input);
            if (await sources.GetAsync(sourceId, null, cancellationToken) is not { } existing)
                return TypedResults.NotFound();

            // An update that leaves the kind out keeps the kind of the source.
            input.Kind ??= existing.Kind;
            Dictionary<string, string[]> errors = input.Validate();
            if (input.EffectiveKind() != existing.Kind)
                errors[nameof(SourceInput.Kind)] = [$"The kind of a source cannot change. The source is a {existing.Kind} source."];

            if (errors.Count > 0)
                return TypedResults.ValidationProblem(errors);

            try
            {
                if (!await sources.UpdateAsync(sourceId, input, cancellationToken))
                    return TypedResults.NotFound();
            }
            catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                return TypedResults.Conflict($"A source with the name {input.Name} exists.");
            }

            // The new rules give the stored documents their categories at once. A run of the indexer may wait for hours behind the vectors of a large source.
            Source source = (await sources.GetAsync(sourceId, null, cancellationToken))!;
            if (existing.RootPath != source.RootPath)
                graphBuilder.Release(existing);

            await documents.ApplyCategoriesAsync(source, cancellationToken);
            scheduler.Enqueue(sourceId);
            return TypedResults.Ok(source);
        });

        api.MapDelete("/sources/{sourceId:int}", static async Task<Results<NoContent, NotFound>> (int sourceId, SourceStore sources, CodeGraphBuilder graphBuilder, Indexer indexer, CancellationToken cancellationToken) =>
        {
            Source? existing = await sources.GetAsync(sourceId, null, cancellationToken);
            if (!await sources.DeleteAsync(sourceId, cancellationToken))
                return TypedResults.NotFound();

            indexer.Forget(sourceId);

            // The extractor keeps the loaded projects of a code base in memory until it is told to drop them.
            if (existing is not null)
                graphBuilder.Release(existing);

            return TypedResults.NoContent();
        });

        api.MapPost("/sources/{sourceId:int}/index", static async Task<Results<Accepted<IndexRequestResult>, NotFound>> (int sourceId, SourceStore sources, IndexScheduler scheduler, CancellationToken cancellationToken) =>
        {
            if (await sources.GetAsync(sourceId, null, cancellationToken) is null)
                return TypedResults.NotFound();

            return TypedResults.Accepted((string?)null, new IndexRequestResult(scheduler.Enqueue(sourceId)));
        });

        api.MapGet("/runs", static (int? sourceId, int? limit, IndexRunStore runs, CancellationToken cancellationToken) =>
            runs.ListAsync(sourceId, Math.Clamp(limit ?? 20, 1, MaxPageSize), cancellationToken));

        api.MapGet("/categories", static async (DocumentStore documents, EmbeddingModelRegistry models, CancellationToken cancellationToken) =>
            await documents.GetCategoriesAsync(await CurrentModelIdAsync(models, cancellationToken), cancellationToken));

        api.MapGet("/documents", static async (int? sourceId, string? category, string? q, int? offset, int? limit, DocumentStore documents, CancellationToken cancellationToken) =>
        {
            (IReadOnlyList<DocumentSummary> items, long total) = await documents.ListAsync(
                sourceId is { } id ? [id] : [], category ?? string.Empty, q ?? string.Empty, Math.Max(0, offset ?? 0), Math.Clamp(limit ?? 50, 1, MaxPageSize), cancellationToken);
            return new DocumentPage(items, total);
        });

        api.MapGet("/documents/{documentId:long}", static async Task<Results<Ok<DocumentSummary>, NotFound>> (long documentId, DocumentStore documents, CancellationToken cancellationToken) =>
            await documents.GetAsync(documentId, cancellationToken) is { } document ? TypedResults.Ok(document) : TypedResults.NotFound());

        api.MapGet("/documents/{documentId:long}/content", static async Task<Results<Ok<DocumentContent>, NotFound>> (long documentId, int? offset, int? length, DocumentStore documents, CancellationToken cancellationToken) =>
        {
            if (await documents.GetAsync(documentId, cancellationToken) is not { } document)
                return TypedResults.NotFound();

            return TypedResults.Ok(await documents.GetContentAsync(document, offset ?? 0, length ?? DocumentStore.MaxContentLength, cancellationToken));
        });

        api.MapGet("/documents/{documentId:long}/chunks", static async (long documentId, long? sectionId, int? offset, int? limit, DocumentStore documents, EmbeddingModelRegistry models, CancellationToken cancellationToken) =>
            await documents.GetChunksAsync(documentId, sectionId, Math.Max(0, offset ?? 0), Math.Clamp(limit ?? 50, 1, MaxPageSize), await CurrentModelIdAsync(models, cancellationToken), cancellationToken));

        api.MapGet("/documents/{documentId:long}/boundaries", static (long documentId, DocumentStore documents, CancellationToken cancellationToken) =>
            documents.GetBoundariesAsync(documentId, cancellationToken));

        api.MapGet("/documents/{documentId:long}/formulas", static (long documentId, DocumentStore documents, CancellationToken cancellationToken) =>
            documents.GetFormulasAsync(documentId, cancellationToken));

        api.MapGet("/documents/{documentId:long}/sections", static (long documentId, StructureStore structure, CancellationToken cancellationToken) =>
            structure.GetSectionsAsync(documentId, cancellationToken));

        api.MapGet("/documents/{documentId:long}/anchors/{name}", static async Task<Results<Ok<AnchorView>, NotFound>> (long documentId, string name, StructureStore structure, CancellationToken cancellationToken) =>
            await structure.GetAnchorAsync(documentId, name, cancellationToken) is { } anchor ? TypedResults.Ok(anchor) : TypedResults.NotFound());

        // The original file, so the dashboard can show a PDF page next to its Markdown.
        api.MapGet("/documents/{documentId:long}/file", static async Task<Results<PhysicalFileHttpResult, NotFound>> (long documentId, StructureStore structure, CancellationToken cancellationToken) =>
        {
            if (await structure.GetFilePathAsync(documentId, cancellationToken) is not { } path || !File.Exists(path))
                return TypedResults.NotFound();

            string type = Path.GetExtension(path).ToUpperInvariant() switch
            {
                ".PDF" => "application/pdf",
                ".MD" or ".MARKDOWN" => "text/markdown; charset=utf-8",
                _ => "text/plain; charset=utf-8",
            };
            return TypedResults.PhysicalFile(path, type, enableRangeProcessing: true);
        });

        api.MapGet("/chunks/{chunkId:long}", static async Task<Results<Ok<ChunkDetail>, NotFound>> (long chunkId, StructureStore structure, EmbeddingModelRegistry models, CancellationToken cancellationToken) =>
            await structure.GetChunkAsync(chunkId, await CurrentModelIdAsync(models, cancellationToken), cancellationToken) is { } chunk ? TypedResults.Ok(chunk) : TypedResults.NotFound());

        api.MapGet("/code/grep", static async Task<Results<Ok<CodeGrepResult>, ValidationProblem>> (
            string? pattern, int? sourceId, bool? regex, bool? caseSensitive, string? path, string? language, int? limit, CodeSearchService code, CancellationToken cancellationToken) =>
        {
            try
            {
                return TypedResults.Ok(await code.GrepAsync(
                    new CodeGrepRequest { Pattern = pattern ?? string.Empty, SourceId = sourceId, IsRegex = regex ?? false, CaseSensitive = caseSensitive ?? true, Path = path, Language = language, Limit = limit },
                    cancellationToken));
            }
            catch (CodeSearchException exception)
            {
                return TypedResults.ValidationProblem(new Dictionary<string, string[]> { [exception.Field] = [exception.Message] });
            }
        });

        api.MapGet("/code/lines", static async Task<Results<Ok<CodeLines>, NotFound>> (long documentId, int? start, int? end, CodeSearchService code, CancellationToken cancellationToken) =>
            await code.ReadLinesAsync(documentId, start ?? 1, end, cancellationToken) is { } lines ? TypedResults.Ok(lines) : TypedResults.NotFound());

        api.MapPost("/code/search", static async Task<Results<Ok<CodeSearchResult>, ValidationProblem>> (CodeSearchRequest request, CodeTierSearch search, CancellationToken cancellationToken) =>
        {
            try
            {
                return TypedResults.Ok(await search.SearchAsync(request, cancellationToken));
            }
            catch (CodeSearchException exception)
            {
                return TypedResults.ValidationProblem(new Dictionary<string, string[]> { [exception.Field] = [exception.Message] });
            }
        });

        api.MapGet("/code/graph", static async Task<Results<Ok<CodeGraphStatus>, NotFound, ValidationProblem>> (int? sourceId, CodeGraphService graph, CancellationToken cancellationToken) =>
        {
            if (sourceId is not { } id)
                return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["sourceId"] = ["The source is required."] });

            try
            {
                return await graph.GetStatusAsync(id, cancellationToken) is { } status ? TypedResults.Ok(status) : TypedResults.NotFound();
            }
            catch (CodeSearchException exception)
            {
                return TypedResults.ValidationProblem(new Dictionary<string, string[]> { [exception.Field] = [exception.Message] });
            }
        });

        api.MapGet("/code/symbols", static async Task<Results<Ok<IReadOnlyList<SymbolMatch>>, ValidationProblem>> (
            string? q, int? sourceId, string? kind, string? project, int? limit, CodeGraphService graph, CancellationToken cancellationToken) =>
        {
            try
            {
                return TypedResults.Ok(await graph.SearchSymbolsAsync(q ?? string.Empty, sourceId, kind, project, limit ?? 50, cancellationToken));
            }
            catch (CodeSearchException exception)
            {
                return TypedResults.ValidationProblem(new Dictionary<string, string[]> { [exception.Field] = [exception.Message] });
            }
        });

        api.MapGet("/code/symbols/{symbolId:int}", static async Task<Results<Ok<SymbolDetail>, NotFound>> (int symbolId, CodeGraphService graph, CancellationToken cancellationToken) =>
            await graph.GetDetailAsync(symbolId, cancellationToken) is { } detail ? TypedResults.Ok(detail) : TypedResults.NotFound());

        api.MapGet("/code/symbol", static async Task<Results<Ok<SymbolDetail>, NotFound, ValidationProblem>> (int? sourceId, string? docId, string? project, CodeGraphService graph, CancellationToken cancellationToken) =>
        {
            Dictionary<string, string[]> errors = [];
            if (sourceId is null)
                errors["sourceId"] = ["The source is required."];

            if (string.IsNullOrWhiteSpace(docId))
                errors["docId"] = ["The documentation comment ID is required."];

            if (errors.Count > 0)
                return TypedResults.ValidationProblem(errors);

            return await graph.GetDetailAsync(sourceId!.Value, docId!.Trim(), project, cancellationToken) is { } detail ? TypedResults.Ok(detail) : TypedResults.NotFound();
        });

        api.MapGet("/code/symbols/{symbolId:int}/references", static async Task<Results<Ok<CodeReferencePage>, NotFound>> (int symbolId, int? offset, int? limit, string? kind, CodeGraphService graph, CancellationToken cancellationToken) =>
            await graph.GetReferencesAsync(symbolId, kind, Math.Max(0, offset ?? 0), Math.Clamp(limit ?? 100, 1, CodeGraphService.MaxReferenceLimit), cancellationToken) is { } page ? TypedResults.Ok(page) : TypedResults.NotFound());

        api.MapGet("/code/outline", static async Task<Results<Ok<IReadOnlyList<OutlineNode>>, NotFound, ValidationProblem>> (long? documentId, DocumentStore documents, CodeGraphService graph, CancellationToken cancellationToken) =>
        {
            if (documentId is not { } id)
                return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["documentId"] = ["The document is required."] });

            return await documents.GetAsync(id, cancellationToken) is null ? TypedResults.NotFound() : TypedResults.Ok(await graph.GetOutlineAsync(id, cancellationToken));
        });

        api.MapGet("/code/diagrams/projects", static (int? sourceId, bool? includeTests, string? format, CodeDiagramService diagrams, CancellationToken cancellationToken) =>
            DiagramAsync(format, () => diagrams.GetProjectsAsync(new ProjectsDiagramRequest(sourceId, includeTests), cancellationToken)));

        api.MapGet("/code/diagrams/namespaces", static (
            int? sourceId, string? project, bool? includeTests, int? minWeight, int? maxNodes, int? maxEdges, string? format, CodeDiagramService diagrams, CancellationToken cancellationToken) =>
            DiagramAsync(format, () => diagrams.GetNamespacesAsync(new NamespacesDiagramRequest(sourceId, project, includeTests, minWeight, maxNodes, maxEdges), cancellationToken)));

        api.MapGet("/code/diagrams/namespace", static (
            int? sourceId, [FromQuery(Name = "namespace")] string? ns, string? project, bool? includeExternal, string? members, int? maxNodes, int? maxEdges, string? format, CodeDiagramService diagrams, CancellationToken cancellationToken) =>
            DiagramAsync(format, () => diagrams.GetNamespaceAsync(new NamespaceDiagramRequest(sourceId, ns, project, includeExternal, members, maxNodes, maxEdges), cancellationToken)));

        api.MapGet("/code/diagrams/type", static (
            int? sourceId, string? docId, string? project, int? depth, string? direction, string? kinds, bool? includeTests, bool? includeExternal, string? members, bool? neighborMembers, int? maxNodes, bool? neighborEdges, string? format,
            CodeDiagramService diagrams, CancellationToken cancellationToken) =>
            DiagramAsync(format, () => diagrams.GetTypeAsync(
                new TypeDiagramRequest(sourceId, docId, project, depth, direction, kinds, includeTests, includeExternal, members, neighborMembers, maxNodes, neighborEdges), cancellationToken)));

        api.MapPost("/search", static async Task<Results<Ok<SearchResult>, ValidationProblem>> (SearchRequest request, SearchService search, CancellationToken cancellationToken) =>
        {
            try
            {
                return TypedResults.Ok(await search.SearchAsync(request, cancellationToken));
            }
            catch (ArgumentException exception)
            {
                return TypedResults.ValidationProblem(new Dictionary<string, string[]> { [nameof(SearchRequest.Query)] = [exception.Message] });
            }
        });
    }

    private static async Task<IResult> DiagramAsync(string? format, Func<Task<CodeDiagram>> build)
    {
        string kind = string.IsNullOrWhiteSpace(format) ? "json" : format.Trim().ToLowerInvariant();
        if (kind is not ("json" or "dot" or "plantuml"))
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["format"] = ["The format must be json, dot or plantuml."] });

        try
        {
            CodeDiagram diagram = await build();
            return kind switch
            {
                "dot" => TypedResults.Text(diagram.Dot, "text/plain; charset=utf-8"),
                "plantuml" => TypedResults.Text(PlantUmlWriter.Write(diagram), "text/plain; charset=utf-8"),
                _ => TypedResults.Ok(diagram),
            };
        }
        catch (CodeSearchException exception)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { [exception.Field] = [exception.Message] });
        }
        catch (CodeDiagramNotFoundException exception)
        {
            return TypedResults.Problem(exception.Message, statusCode: StatusCodes.Status404NotFound, title: "Not Found");
        }
    }

    private static async Task<short?> CurrentModelIdAsync(EmbeddingModelRegistry models, CancellationToken cancellationToken) =>
        (await models.GetStatusAsync(cancellationToken)).Model?.ModelId;

    private static Dictionary<string, string>? NormalizeProperties(IReadOnlyDictionary<string, string>? properties)
    {
        if (properties is null)
            return null;

        Dictionary<string, string> result = new(StringComparer.Ordinal);
        foreach ((string key, string? value) in properties)
            result[key?.Trim() ?? string.Empty] = value ?? string.Empty;

        return result;
    }

    private static void Normalize(SourceInput input)
    {
        // A JSON null reaches the properties despite their types.
        input.Name = input.Name?.Trim() ?? string.Empty;
        input.RootPath = input.RootPath?.Trim() ?? string.Empty;
        // "C:/docs/" and "C:\docs" name one folder, so the source stores one form.
        if (Path.IsPathFullyQualified(input.RootPath))
            input.RootPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(input.RootPath));

        input.Kind = string.IsNullOrWhiteSpace(input.Kind) ? null : input.Kind.Trim().ToLowerInvariant();
        input.DotGlobs = input.DotGlobs?.Select(static glob => glob?.Trim() ?? string.Empty).ToList();
        input.Description = input.Description?.Trim();
        // A JSON null as a value reaches the dictionary despite its type.
        input.BuildProperties = NormalizeProperties(input.BuildProperties);
        input.CategoryRules = input.CategoryRules?.Where(static rule => rule is not null)
            .Select(static rule => new CategoryRule { Pattern = rule.Pattern?.Trim() ?? string.Empty, Category = rule.Category?.Trim() ?? string.Empty }).ToList();
        // The expression and the replacement keep their spaces: a space may be a part of the fix.
        input.Replacements = input.Replacements?.Where(static replacement => replacement is not null)
            .Select(static replacement => new TextReplacement { Pattern = replacement.Pattern?.Trim() ?? string.Empty, Find = replacement.Find ?? string.Empty, Replace = replacement.Replace ?? string.Empty }).ToList();
    }
}

/// <summary>
/// The answer to a request to index a source.
/// </summary>
/// <param name="Queued"><see langword="true"/> when the source went into the queue, <see langword="false"/> when it was there already.</param>
internal sealed record IndexRequestResult(bool Queued);

/// <summary>
/// A page of documents.
/// </summary>
/// <param name="Items">The documents of the page.</param>
/// <param name="Total">The number of all matches.</param>
internal sealed record DocumentPage(IReadOnlyList<DocumentSummary> Items, long Total);
