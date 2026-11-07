namespace ProjectRag.Core.Indexing;

/// <summary>
/// The options of the watcher of the source folders.
/// </summary>
public sealed class WatchOptions
{
    /// <summary>
    /// The configuration section that holds these options.
    /// </summary>
    public const string SectionName = "Watch";

    /// <summary>
    /// Gets or sets a value that tells whether the server watches the folders of the sources. The change is read at the start of the server.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Gets or sets the seconds between two checks of the list of sources. A check starts the watchers of new sources and stops the watchers of deleted sources.
    /// </summary>
    public int RefreshSeconds { get; set; } = 60;

    /// <summary>
    /// Gets or sets the milliseconds without a change in a folder that the watcher waits for before it asks for a run of the indexer.
    /// </summary>
    public int QuietMilliseconds { get; set; } = 2000;

    /// <summary>
    /// Gets or sets the most seconds from the first change to the request of a run, also when changes go on.
    /// </summary>
    public int MaxDelaySeconds { get; set; } = 30;
}
