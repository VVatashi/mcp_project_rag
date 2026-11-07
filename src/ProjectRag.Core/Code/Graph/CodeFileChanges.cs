namespace ProjectRag.Core.Code.Graph;

/// <summary>
/// The changes that the file sync of a run made to the documents of a code source.
/// </summary>
/// <remarks>
/// The graph step uses them to choose between an incremental update and a full build.
/// A file whose bytes did not change is not a change, even when the indexer read it again.
/// </remarks>
public sealed class CodeFileChanges
{
    /// <summary>
    /// Gets the files that got a new document.
    /// </summary>
    public List<string> Added { get; } = [];

    /// <summary>
    /// Gets the files whose content changed, with the content hash of the document before the run.
    /// </summary>
    public Dictionary<string, string> Updated { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Gets the files whose documents the run deleted.
    /// </summary>
    public List<string> Removed { get; } = [];
}
