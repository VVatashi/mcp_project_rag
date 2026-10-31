using Microsoft.AspNetCore.Http.HttpResults;

using Npgsql;

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

        api.MapPut("/sources/{sourceId:int}", static async Task<Results<Ok<Source>, NotFound, ValidationProblem, Conflict<string>>> (int sourceId, SourceInput input, SourceStore sources, IndexScheduler scheduler, CancellationToken cancellationToken) =>
        {
            Normalize(input);
            if (input.Validate() is { Count: > 0 } errors)
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

            scheduler.Enqueue(sourceId);
            return TypedResults.Ok((await sources.GetAsync(sourceId, null, cancellationToken))!);
        });

        api.MapDelete("/sources/{sourceId:int}", static async Task<Results<NoContent, NotFound>> (int sourceId, SourceStore sources, CancellationToken cancellationToken) =>
            await sources.DeleteAsync(sourceId, cancellationToken) ? TypedResults.NoContent() : TypedResults.NotFound());

        api.MapPost("/sources/{sourceId:int}/index", static async Task<Results<Accepted<IndexRequestResult>, NotFound>> (int sourceId, SourceStore sources, IndexScheduler scheduler, CancellationToken cancellationToken) =>
        {
            if (await sources.GetAsync(sourceId, null, cancellationToken) is null)
                return TypedResults.NotFound();

            return TypedResults.Accepted((string?)null, new IndexRequestResult(scheduler.Enqueue(sourceId)));
        });

        api.MapGet("/runs", static (int? sourceId, int? limit, IndexRunStore runs, CancellationToken cancellationToken) =>
            runs.ListAsync(sourceId, Math.Clamp(limit ?? 20, 1, MaxPageSize), cancellationToken));

        api.MapGet("/documents", static async (int? sourceId, string? q, int? offset, int? limit, DocumentStore documents, CancellationToken cancellationToken) =>
        {
            (IReadOnlyList<DocumentSummary> items, long total) = await documents.ListAsync(
                sourceId is { } id ? [id] : [], q ?? string.Empty, Math.Max(0, offset ?? 0), Math.Clamp(limit ?? 50, 1, MaxPageSize), cancellationToken);
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

        api.MapGet("/documents/{documentId:long}/chunks", static async (long documentId, DocumentStore documents, EmbeddingModelRegistry models, CancellationToken cancellationToken) =>
            await documents.GetChunksAsync(documentId, await CurrentModelIdAsync(models, cancellationToken), cancellationToken));

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

    private static async Task<short?> CurrentModelIdAsync(EmbeddingModelRegistry models, CancellationToken cancellationToken) =>
        (await models.GetStatusAsync(cancellationToken)).Model?.ModelId;

    private static void Normalize(SourceInput input)
    {
        input.Name = input.Name.Trim();
        input.RootPath = input.RootPath.Trim();
        // "C:/docs/" and "C:\docs" name one folder, so the source stores one form.
        if (Path.IsPathFullyQualified(input.RootPath))
            input.RootPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(input.RootPath));

        input.Description = input.Description?.Trim();
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
