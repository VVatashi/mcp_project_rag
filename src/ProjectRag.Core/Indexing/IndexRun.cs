namespace ProjectRag.Core.Indexing;

/// <summary>
/// A run of the indexer over a source and its counters.
/// </summary>
public sealed class IndexRun
{
    /// <summary>The status of a run that has not ended.</summary>
    public const string Running = "running";

    /// <summary>The status of a run that ended without an error. It may still hold a warning.</summary>
    public const string Succeeded = "succeeded";

    /// <summary>The status of a run that ended with an error.</summary>
    public const string Failed = "failed";

    /// <summary>The status of a run that the stop of the application ended.</summary>
    public const string Canceled = "canceled";

    /// <summary>Gets or sets the key of the run.</summary>
    public long RunId { get; set; }

    /// <summary>Gets or sets the key of the source.</summary>
    public int SourceId { get; set; }

    /// <summary>Gets or sets the name of the source.</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>Gets or sets the status: <see cref="Running"/>, <see cref="Succeeded"/>, <see cref="Failed"/> or <see cref="Canceled"/>.</summary>
    public string Status { get; set; } = Running;

    /// <summary>Gets or sets the start time.</summary>
    public DateTime StartedAt { get; set; }

    /// <summary>Gets or sets the end time, or <see langword="null"/> while the run goes on.</summary>
    public DateTime? FinishedAt { get; set; }

    /// <summary>Gets or sets the number of files that match the patterns of the source.</summary>
    public int FilesSeen { get; set; }

    /// <summary>Gets or sets the number of new documents.</summary>
    public int DocumentsAdded { get; set; }

    /// <summary>Gets or sets the number of changed documents.</summary>
    public int DocumentsUpdated { get; set; }

    /// <summary>Gets or sets the number of documents whose files are gone.</summary>
    public int DocumentsRemoved { get; set; }

    /// <summary>Gets or sets the number of documents whose files did not change.</summary>
    public int DocumentsUnchanged { get; set; }

    /// <summary>Gets or sets the number of files that the indexer could not read.</summary>
    public int DocumentsFailed { get; set; }

    /// <summary>Gets or sets the number of vectors that new chunks took over from stored chunks with the same text.</summary>
    public int ChunksReused { get; set; }

    /// <summary>Gets or sets the number of chunks that the embedding model embedded, locally and remotely.</summary>
    public int ChunksEmbedded { get; set; }

    /// <summary>Gets or sets the part of <see cref="ChunksEmbedded"/> that the remote backend embedded.</summary>
    public int ChunksEmbeddedRemote { get; set; }

    /// <summary>Gets or sets the cost of the remote backend in this run, in USD.</summary>
    public decimal RemoteCost { get; set; }

    /// <summary>Gets or sets a warning: files that the indexer skipped, or chunks that wait for vectors.</summary>
    public string? Warning { get; set; }

    /// <summary>Gets or sets the error of a failed run.</summary>
    public string? Error { get; set; }
}
