using System.Security.Cryptography;
using System.Text;

using Microsoft.Extensions.Options;

namespace ProjectRag.Server;

/// <summary>
/// Checks the bearer token of the requests to the web API and to the MCP endpoint, see <see cref="ServerOptions.Token"/>.
/// </summary>
internal static class TokenCheck
{
    private const string BearerPrefix = "Bearer ";

    /// <summary>
    /// Adds the check to the pipeline. The static files of the dashboard stay open: the dashboard asks for the token itself.
    /// </summary>
    /// <param name="app">The application.</param>
    public static void UseTokenCheck(this WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            string token = context.RequestServices.GetRequiredService<IOptionsMonitor<ServerOptions>>().CurrentValue.Token;
            PathString path = context.Request.Path;
            if (token.Length > 0 && (path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase) || path.StartsWithSegments("/mcp", StringComparison.OrdinalIgnoreCase)) && !HasToken(context.Request, token))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.Headers.WWWAuthenticate = "Bearer";
                return;
            }

            await next(context);
        });
    }

    private static bool HasToken(HttpRequest request, string token)
    {
        string header = request.Headers.Authorization.ToString();
        if (!header.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
            return false;

        // The comparison takes the same time for any wrong token.
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(header[BearerPrefix.Length..].Trim()), Encoding.UTF8.GetBytes(token));
    }
}
