using Microsoft.Build.Locator;

using ProjectRag.Core.Code.Graph;

namespace ProjectRag.Code;

/// <summary>
/// Registers MSBuild once for the process. It must run before any MSBuild type loads.
/// </summary>
public static class MsBuildRegistration
{
    private static readonly Lock Gate = new();

    private static string? s_version;

    /// <summary>
    /// Registers MSBuild, or returns the version of the earlier registration.
    /// </summary>
    /// <param name="msbuildPath">The folder of the MSBuild, or <see langword="null"/> for the newest installed .NET SDK.</param>
    /// <returns>The version of the SDK.</returns>
    /// <exception cref="CodeGraphException">No .NET SDK is installed.</exception>
    public static string Register(string? msbuildPath)
    {
        lock (Gate)
        {
            if (s_version is not null)
                return s_version;

            if (!string.IsNullOrWhiteSpace(msbuildPath))
            {
                if (!MSBuildLocator.IsRegistered)
                    MSBuildLocator.RegisterMSBuildPath(msbuildPath);

                s_version = Path.GetFileName(Path.TrimEndingDirectorySeparator(msbuildPath));
                return s_version;
            }

            if (MSBuildLocator.IsRegistered)
            {
                s_version = "unknown";
                return s_version;
            }

            VisualStudioInstanceQueryOptions query = new() { DiscoveryTypes = DiscoveryType.DotNetSdk };
            VisualStudioInstance? best = MSBuildLocator.QueryVisualStudioInstances(query).OrderByDescending(instance => instance.Version).FirstOrDefault()
                ?? throw new CodeGraphException("No .NET SDK is installed. Install the SDK of the code base or set CodeGraph:MsBuildPath.");

            MSBuildLocator.RegisterInstance(best);
            s_version = best.Version.ToString();
            return s_version;
        }
    }
}
