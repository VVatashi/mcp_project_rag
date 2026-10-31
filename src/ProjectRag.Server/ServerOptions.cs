namespace ProjectRag.Server;

/// <summary>
/// The options of the web host.
/// </summary>
internal sealed class ServerOptions
{
    /// <summary>
    /// The configuration section that holds these options.
    /// </summary>
    public const string SectionName = "Server";

    /// <summary>
    /// Gets or sets the token that a client sends in the header <c>Authorization: Bearer token</c> to the web API and to the MCP endpoint.
    /// An empty text turns the check off. The server listens on the loopback address only, so the token guards against other local programs.
    /// </summary>
    public string Token { get; set; } = string.Empty;
}
