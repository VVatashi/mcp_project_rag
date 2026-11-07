namespace ProjectRag.Core.Code.Graph;

/// <summary>
/// Reads the code graph of a code base with a compiler.
/// </summary>
/// <remarks>
/// The indexer runs the extractor when it is registered. Without it a code source has the exact search only.
/// </remarks>
public interface ICodeGraphExtractor
{
    /// <summary>
    /// Gets the version of the extractor. A new version builds every graph again.
    /// </summary>
    int Version { get; }

    /// <summary>
    /// Loads the projects of a code base and reads its graph.
    /// </summary>
    /// <param name="input">The projects, the files and the view of the build.</param>
    /// <param name="cancellationToken">The token that stops the work.</param>
    /// <returns>The graph. A project that fails to load gives a diagnostic, not an exception.</returns>
    /// <exception cref="CodeGraphException">No project could load, for example because the SDK is missing.</exception>
    Task<CodeGraph> ExtractAsync(CodeGraphInput input, CancellationToken cancellationToken);
}

/// <summary>
/// The extractor could not read the graph of a code base.
/// </summary>
public sealed class CodeGraphException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="CodeGraphException"/> class.
    /// </summary>
    public CodeGraphException()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="CodeGraphException"/> class.
    /// </summary>
    /// <param name="message">The message.</param>
    public CodeGraphException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="CodeGraphException"/> class.
    /// </summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The cause.</param>
    public CodeGraphException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
