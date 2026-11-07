namespace ProjectRag.Core.Code;

/// <summary>
/// The options of the code sources.
/// </summary>
public sealed class CodeOptions
{
    /// <summary>
    /// The configuration section that holds these options.
    /// </summary>
    public const string SectionName = "Code";

    /// <summary>
    /// Gets or sets the Git program that lists the files of a work tree. The default finds <c>git</c> on the path.
    /// </summary>
    public string GitPath { get; set; } = "git";

    /// <summary>
    /// Gets or sets the most seconds that a Git command may run.
    /// </summary>
    public int GitTimeoutSeconds { get; set; } = 60;

    /// <summary>
    /// Gets or sets the size of the largest file of a code source in bytes. The indexer skips a larger file.
    /// </summary>
    public long MaxFileBytes { get; set; } = 1024 * 1024;

    /// <summary>
    /// Gets or sets the size of the largest file of JSON, XML, YAML or TOML in bytes. Such files hold data more often than code.
    /// </summary>
    public long MaxConfigFileBytes { get; set; } = 256 * 1024;

    /// <summary>
    /// Gets or sets the length of a line in characters over which a file of JSON, XML, YAML, TOML or text is a data file.
    /// The indexer stores the document of a data file but makes no chunks and no vectors for it.
    /// </summary>
    public int DataLineChars { get; set; } = 1000;

    /// <summary>
    /// Gets or sets the most minutes that the build of the code graph of one code source may run.
    /// </summary>
    public int GraphTimeoutMinutes { get; set; } = 15;

    /// <summary>
    /// Gets or sets the most files that one incremental update of the code graph reads: the changed files and the files that refer to their symbols.
    /// A larger change builds the whole graph again. 0 turns the incremental updates off.
    /// </summary>
    public int IncrementalMaxFiles { get; set; } = 300;
}
