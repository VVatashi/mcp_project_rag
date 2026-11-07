using System.Security.Cryptography;
using System.Text;

namespace ProjectRag.Core.Code.Graph;

/// <summary>
/// Hashes what the code graph depends on, so the indexer can skip a build when nothing changed.
/// </summary>
public static class CodeGraphInputHash
{
    /// <summary>
    /// Checks whether a change of a file can change the graph.
    /// </summary>
    /// <param name="path">The path of the file.</param>
    /// <param name="language">The language of the file.</param>
    /// <returns><see langword="true"/> for C#, MSBuild and solution files, <c>global.json</c> and <c>nuget.config</c>.</returns>
    public static bool Matters(string path, string? language)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (language is "csharp" or "msbuild" or "solution")
            return true;

        string name = Path.GetFileName(path);
        return name.Equals("global.json", StringComparison.OrdinalIgnoreCase)
            || name.Equals("nuget.config", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Directory.Packages.props", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Computes the hash.
    /// </summary>
    /// <param name="extractorVersion">The version of the extractor.</param>
    /// <param name="properties">The build properties of the source.</param>
    /// <param name="files">The files of the source with their content hashes and languages. Files that do not matter are skipped.</param>
    /// <returns>The SHA-256 in lowercase hexadecimal.</returns>
    public static string Compute(int extractorVersion, IReadOnlyDictionary<string, string> properties, IEnumerable<(string Path, string ContentHash, string? Language)> files)
    {
        ArgumentNullException.ThrowIfNull(properties);
        ArgumentNullException.ThrowIfNull(files);
        StringBuilder text = new();
        text.Append("extractor\0").Append(extractorVersion).Append('\n');
        foreach (KeyValuePair<string, string> property in properties.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
            text.Append("property\0").Append(property.Key).Append('\0').Append(property.Value).Append('\n');

        foreach ((string path, string contentHash, _) in files.Where(static file => Matters(file.Path, file.Language)).OrderBy(static file => file.Path, StringComparer.Ordinal))
            text.Append("file\0").Append(path).Append('\0').Append(contentHash).Append('\n');

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }
}
