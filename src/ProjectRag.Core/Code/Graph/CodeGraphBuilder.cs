using System.Diagnostics;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using ProjectRag.Core.Sources;

namespace ProjectRag.Core.Code.Graph;

/// <summary>
/// The step of the indexer that builds the code graph of a code source with the registered extractor.
/// </summary>
/// <remarks>
/// <para>
/// The step needs the files of the source in the database, so it runs after the files are synced.
/// A source without a project file has no graph. The step skips the build when the input hash, the version of the extractor and a clean state say that nothing changed.
/// A failure of the extractor keeps the old graph, records the error and gives a warning. It never fails the run.
/// The same input builds again after an hour, and a changed input builds at the next run.
/// </para>
/// <para>
/// An extractor that implements <see cref="IIncrementalCodeGraphExtractor"/> updates the graph instead of a full build when the run changed the content of existing C# files only,
/// the stored graph matches the state before the run, and the changed files and the files that refer to their symbols number at most <see cref="CodeOptions.IncrementalMaxFiles"/>.
/// A new, deleted or renamed C# file, a change of a project, props, targets or solution file, of <c>global.json</c> or of <c>nuget.config</c>, a new version of the extractor
/// or new build properties need a full build. An update that fails or finds no kept workspace falls back to a full build in the same run.
/// </para>
/// </remarks>
/// <param name="extractors">The extractors. The first one builds the graphs. None means that code sources keep the exact search only.</param>
/// <param name="store">The store of the graph.</param>
/// <param name="code">The options of the code sources.</param>
/// <param name="logger">The logger.</param>
public sealed class CodeGraphBuilder(IEnumerable<ICodeGraphExtractor> extractors, CodeGraphStore store, IOptionsMonitor<CodeOptions> code, ILogger<CodeGraphBuilder> logger)
{
    private static readonly string[] SolutionExtensions = [".sln", ".slnx", ".slnf"];

    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromHours(1);

    /// <summary>
    /// Builds or updates the graph of a source when its input changed.
    /// </summary>
    /// <param name="source">The source. A source that is not a code source is skipped.</param>
    /// <param name="changes">The changes that the run made to the documents, or <see langword="null"/> when they are not known. Then the step builds the whole graph.</param>
    /// <param name="warnings">Gets the warning of a failed build.</param>
    /// <param name="cancellationToken">The token that stops the run. A cancel stops the step.</param>
    /// <returns>A task that completes after the step.</returns>
    public async Task BuildAsync(Source source, CodeFileChanges? changes, List<string> warnings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(warnings);
        if (source.Kind != SourceKind.Code || extractors.FirstOrDefault() is not { } extractor)
            return;

        IReadOnlyList<CodeGraphFile> files = await store.ListFilesAsync(source.SourceId, cancellationToken);
        string[] projectPaths = [.. files.Select(static file => file.Path).Where(static path => path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal)];
        CodeGraphState? state = await store.GetStateAsync(source.SourceId, cancellationToken);
        if (projectPaths.Length == 0)
        {
            if (state is not null)
            {
                await store.ClearAsync(source.SourceId, cancellationToken);
                logger.LogInformation("The code source {Source} has no project file. The indexer removed its old graph.", source.Name);
            }

            return;
        }

        string hash = CodeGraphInputHash.Compute(extractor.Version, source.BuildProperties, files.Select(static file => (file.Path, file.ContentHash, file.Language)));
        if (state is { Error: null, BuiltAt: not null } && state.InputHash == hash && state.ExtractorVersion == extractor.Version)
        {
            logger.LogDebug("The graph of the code source {Source} is current.", source.Name);
            return;
        }

        // A failure such as a missing SDK or a timeout repeats with the same input. The scan every few minutes must not rebuild in a loop.
        if (state is { Error: not null, FailedAt: { } failedAt } && state.InputHash == hash && DateTime.UtcNow - failedAt < RetryAfterFailure)
        {
            logger.LogDebug("The graph of the code source {Source} failed with the same input at {FailedAt}. The indexer tries again after a change or an hour.", source.Name, failedAt);
            return;
        }

        CodeGraphInput input = new(
            source.RootPath,
            projectPaths,
            [.. files.Select(static file => file.Path).Where(static path => SolutionExtensions.Any(extension => path.EndsWith(extension, StringComparison.OrdinalIgnoreCase))).Order(StringComparer.Ordinal)],
            files.Select(static file => file.Path).ToHashSet(StringComparer.Ordinal),
            source.BuildProperties);
        if (extractor is IIncrementalCodeGraphExtractor incremental
            && PlanUpdate(extractor, state, source, files, changes) is { } changed
            && await TryUpdateAsync(source, incremental, input, changed, hash, cancellationToken))
        {
            return;
        }

        if (await BuildFullAsync(source, extractor, input, hash, cancellationToken) is { } error)
        {
            await store.SaveErrorAsync(source.SourceId, error, hash, cancellationToken);
            warnings.Add($"The code graph of {source.Name} failed: {error} The old graph, if any, stays.");
        }
    }

    /// <summary>
    /// Drops the kept workspace of a code source that the user deleted or moved.
    /// </summary>
    /// <param name="source">The source as it was before the change.</param>
    public void Release(Source source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Kind == SourceKind.Code && source.RootPath.Length > 0 && extractors.FirstOrDefault() is IIncrementalCodeGraphExtractor incremental)
            incremental.Release(source.RootPath);
    }

    // Tells whether the run allows an update and gives the changed C# files. The stored hash must equal the hash of the files before the run.
    // So the graph matches the state that the update starts from, and the build properties and the version of the extractor did not change.
    private IReadOnlyList<string>? PlanUpdate(ICodeGraphExtractor extractor, CodeGraphState? state, Source source, IReadOnlyList<CodeGraphFile> files, CodeFileChanges? changes)
    {
        if (changes is null || state is not { Error: null, BuiltAt: not null } || state.ExtractorVersion != extractor.Version || code.CurrentValue.IncrementalMaxFiles <= 0)
            return null;

        if (changes.Added.Any(Matters) || changes.Removed.Any(Matters))
            return null;

        List<string> changed = [];
        foreach (string path in changes.Updated.Keys.Where(Matters))
        {
            if (!path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                return null;

            changed.Add(path);
        }

        if (changed.Count == 0)
            return null;

        string before = CodeGraphInputHash.Compute(extractor.Version, source.BuildProperties, files.Select(file => (file.Path, changes.Updated.GetValueOrDefault(file.Path, file.ContentHash), file.Language)));
        if (before != state.InputHash)
        {
            logger.LogDebug("The stored graph of the code source {Source} does not match the state before the run. The indexer builds the whole graph.", source.Name);
            return null;
        }

        return [.. changed.Order(StringComparer.Ordinal)];
    }

    private static bool Matters(string path) => CodeGraphInputHash.Matters(path, CodeLanguages.FromPath(path));

    private async Task<bool> TryUpdateAsync(
        Source source, IIncrementalCodeGraphExtractor extractor, CodeGraphInput input, IReadOnlyList<string> changed, string hash, CancellationToken cancellationToken)
    {
        int timeoutMinutes = Math.Max(1, code.CurrentValue.GraphTimeoutMinutes);
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(timeoutMinutes));
        Stopwatch watch = Stopwatch.StartNew();
        try
        {
            IReadOnlyList<string> affected = await store.FindAffectedFilesAsync(source.SourceId, changed, timeout.Token);
            if (affected.Count > code.CurrentValue.IncrementalMaxFiles)
            {
                logger.LogInformation(
                    "The change of {Changed} files of the code source {Source} reaches {Affected} files, over Code:IncrementalMaxFiles {Limit}. The indexer builds the whole graph.",
                    changed.Count, source.Name, affected.Count, code.CurrentValue.IncrementalMaxFiles);
                return false;
            }

            CodeGraphPatch? patch = await extractor.UpdateAsync(new CodeGraphUpdate(input, changed, affected), timeout.Token);
            if (patch is null)
            {
                logger.LogInformation("The extractor keeps no workspace for the code source {Source}. The indexer builds the whole graph.", source.Name);
                return false;
            }

            TimeSpan extracted = watch.Elapsed;
            CodeGraphCounts counts = await store.MergeAsync(source.SourceId, patch, hash, extractor.Version, watch.Elapsed, timeout.Token);
            logger.LogInformation(
                "The graph of the code source {Source} got an update of {Changed} changed and {Affected} read files: {Projects} projects, {Symbols} symbols, {References} references. Extract {Extract:F1} s, store {Store:F1} s.",
                source.Name, changed.Count, affected.Count, counts.Projects, counts.Symbols, counts.References, extracted.TotalSeconds, (watch.Elapsed - extracted).TotalSeconds);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        // Any failure of the update, also a timeout, falls back to the full build, which replaces the whole graph.
#pragma warning disable CA1031
        catch (Exception exception)
#pragma warning restore CA1031
        {
            logger.LogWarning(exception, "The update of the graph of the code source {Source} failed. The indexer builds the whole graph.", source.Name);
            return false;
        }
    }

    // Returns the error of the build, or null after a success.
    private async Task<string?> BuildFullAsync(Source source, ICodeGraphExtractor extractor, CodeGraphInput input, string hash, CancellationToken cancellationToken)
    {
        int timeoutMinutes = Math.Max(1, code.CurrentValue.GraphTimeoutMinutes);
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(timeoutMinutes));
        Stopwatch watch = Stopwatch.StartNew();
        try
        {
            CodeGraph graph = await extractor.ExtractAsync(input, timeout.Token);
            TimeSpan extracted = watch.Elapsed;
            CodeGraphCounts counts = await store.ReplaceAsync(source.SourceId, graph, hash, extractor.Version, watch.Elapsed, timeout.Token);
            logger.LogInformation(
                "The graph of the code source {Source}: {Projects} projects, {Symbols} symbols, {References} references. Extract {Extract:F1} s, store {Store:F1} s.",
                source.Name, counts.Projects, counts.Symbols, counts.References, extracted.TotalSeconds, (watch.Elapsed - extracted).TotalSeconds);
            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return $"The build of the graph ran longer than {timeoutMinutes} minutes. Raise Code:GraphTimeoutMinutes.";
        }
        // The graph is an extra. Any failure of the extractor or the write must leave the run and the old graph alone.
#pragma warning disable CA1031
        catch (Exception exception) when (exception is not OperationCanceledException)
#pragma warning restore CA1031
        {
            logger.LogWarning(exception, "The graph of the code source {Source} failed.", source.Name);
            return exception.Message;
        }
    }
}
