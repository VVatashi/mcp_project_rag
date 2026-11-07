namespace ProjectRag.Code;

/// <summary>
/// The options of the Roslyn extractor of the code graph.
/// </summary>
public sealed class CodeGraphOptions
{
    /// <summary>
    /// The configuration section that holds these options.
    /// </summary>
    public const string SectionName = "CodeGraph";

    /// <summary>
    /// Gets or sets the folder of the MSBuild that loads the projects. The default is null: the newest installed .NET SDK.
    /// </summary>
    public string? MsBuildPath { get; set; }

    /// <summary>
    /// Gets or sets the number of projects that the extractor reads at the same time. The default is half of the processors.
    /// </summary>
    public int MaxDegreeOfParallelism { get; set; } = Math.Max(1, Environment.ProcessorCount / 2);

    /// <summary>
    /// Gets or sets the most compiler errors that the graph keeps for one project.
    /// </summary>
    public int MaxErrorsPerProject { get; set; } = 20;

    /// <summary>
    /// Gets or sets the minutes that the extractor keeps the loaded projects of a code base after its last use. The default is 30. The value 0 keeps nothing.
    /// </summary>
    public int KeepWorkspaceMinutes { get; set; } = 30;
}
