using System.ComponentModel;
using System.Diagnostics;
using System.Text;

using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using ProjectRag.Core.Sources;

namespace ProjectRag.Core.Code;

/// <summary>
/// Lists the files of a code source.
/// </summary>
/// <remarks>
/// In a work tree of Git the list comes from <c>git ls-files</c>, so it follows every rule of Git: nested files, <c>.git/info/exclude</c> and the global excludes.
/// Elsewhere, or when Git does not work, a walk over the folder reads the <c>.gitignore</c> files itself.
/// Both lists then pass the same rules: no <c>bin</c>, <c>obj</c> or <c>node_modules</c>, no dot files except the dot patterns of the source, and the patterns of the source.
/// </remarks>
/// <param name="options">The options of the code sources.</param>
/// <param name="logger">The logger.</param>
public sealed class CodeFileLister(IOptionsMonitor<CodeOptions> options, ILogger<CodeFileLister> logger)
{
    private static readonly string[] BuiltInSkippedFolders = ["bin", "obj", "node_modules"];

    /// <summary>
    /// Lists the files of a code source.
    /// </summary>
    /// <param name="source">The source.</param>
    /// <param name="cancellationToken">The token that stops the call to Git.</param>
    /// <returns>The paths relative to the folder, with forward slashes, in ordinal order.</returns>
    /// <exception cref="DirectoryNotFoundException">The folder does not exist.</exception>
    public async Task<IReadOnlyList<string>> ListAsync(Source source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!Directory.Exists(source.RootPath))
            throw new DirectoryNotFoundException($"The folder {source.RootPath} of the source {source.Name} does not exist.");

        IEnumerable<string> paths = await ListWithGitAsync(source.RootPath, cancellationToken) ?? Walk(source.RootPath);
        return Filter(source, paths);
    }

    /// <summary>
    /// Applies the rules of the source to a list of paths: the built-in rule for <c>bin</c>, <c>obj</c> and <c>node_modules</c>, the dot rule and the patterns.
    /// </summary>
    /// <param name="source">The source with its patterns.</param>
    /// <param name="paths">The paths relative to the folder.</param>
    /// <returns>The kept paths with forward slashes, in ordinal order.</returns>
    public static IReadOnlyList<string> Filter(Source source, IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(paths);
        Matcher dots = new(StringComparison.OrdinalIgnoreCase);
        dots.AddIncludePatterns(source.DotGlobs);
        List<string> kept = [];
        foreach (string raw in paths)
        {
            string path = raw.Replace('\\', '/');
            string[] segments = path.Split('/');
            if (segments.Any(static segment => BuiltInSkippedFolders.Contains(segment, StringComparer.OrdinalIgnoreCase)))
                continue;

            if (segments.Any(static segment => segment.StartsWith('.')) && !dots.Match(path).HasMatches)
                continue;

            kept.Add(path);
        }

        // The patterns of the users come from Windows and from Linux, so the case of a name does not matter.
        Matcher matcher = new(StringComparison.OrdinalIgnoreCase);
        matcher.AddIncludePatterns(source.IncludeGlobs);
        matcher.AddExcludePatterns(source.ExcludeGlobs);
        return [.. matcher.Match(kept).Files.Select(static file => file.Path.Replace('\\', '/')).Order(StringComparer.Ordinal)];
    }

    /// <summary>
    /// Walks a folder that is not a work tree of Git and applies its <c>.gitignore</c> files.
    /// </summary>
    /// <remarks>
    /// The walk skips <c>.git</c>, the folders <c>bin</c>, <c>obj</c> and <c>node_modules</c>, links to folders, links to files outside the folder and every folder with a <c>.git</c> of its own, such as a submodule.
    /// An ignored folder is not entered, so a rule cannot bring back a file in it.
    /// </remarks>
    /// <param name="root">The folder.</param>
    /// <returns>The paths of the files relative to the folder, with forward slashes.</returns>
    public static IReadOnlyList<string> Walk(string root)
    {
        ArgumentNullException.ThrowIfNull(root);
        List<string> files = [];
        List<(GitIgnore Rules, string Folder)> stack = [];
        // The file of the repository that is not under version control has the lowest priority.
        TryAddRules(stack, Path.Combine(root, ".git", "info", "exclude"), string.Empty);
        WalkFolder(new DirectoryInfo(root), string.Empty, stack, files, new SourceLinkGuard(root));
        return files;
    }

    private static void WalkFolder(DirectoryInfo folder, string relative, List<(GitIgnore Rules, string Folder)> stack, List<string> files, SourceLinkGuard guard)
    {
        int depth = stack.Count;
        TryAddRules(stack, Path.Combine(folder.FullName, ".gitignore"), relative);
        try
        {
            foreach (FileSystemInfo entry in folder.EnumerateFileSystemInfos())
            {
                string path = relative.Length == 0 ? entry.Name : $"{relative}/{entry.Name}";
                if (entry is DirectoryInfo directory)
                {
                    if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint)
                        || entry.Name == ".git"
                        || BuiltInSkippedFolders.Contains(entry.Name, StringComparer.OrdinalIgnoreCase)
                        || File.Exists(Path.Combine(directory.FullName, ".git"))
                        || Directory.Exists(Path.Combine(directory.FullName, ".git"))
                        || IsIgnored(stack, path, true))
                    {
                        continue;
                    }

                    WalkFolder(directory, path, stack, files, guard);
                }
                else if (entry.Name != ".git" && !IsIgnored(stack, path, false) && guard.IsInside(path))
                {
                    files.Add(path);
                }
            }
        }
        // A folder that the process cannot read, or that goes away during the walk, only misses its files.
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
        finally
        {
            stack.RemoveRange(depth, stack.Count - depth);
        }
    }

    private static void TryAddRules(List<(GitIgnore Rules, string Folder)> stack, string file, string folder)
    {
        try
        {
            if (File.Exists(file))
                stack.Add((GitIgnore.Load(file), folder));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    // A deeper file takes precedence over a file above it.
    private static bool IsIgnored(List<(GitIgnore Rules, string Folder)> stack, string path, bool isDirectory)
    {
        for (int i = stack.Count - 1; i >= 0; i--)
        {
            (GitIgnore rules, string folder) = stack[i];
            string local = folder.Length == 0 ? path : path[(folder.Length + 1)..];
            if (rules.Match(local, isDirectory) is { } ignored)
                return ignored;
        }

        return false;
    }

    // Returns null when the folder is not a work tree or Git does not work. Both cases fall back to the walk.
    private async Task<List<string>?> ListWithGitAsync(string root, CancellationToken cancellationToken)
    {
        CodeOptions current = options.CurrentValue;
        try
        {
            GitResult inside = await RunGitAsync(current, root, ["rev-parse", "--is-inside-work-tree"], cancellationToken);
            if (inside.ExitCode != 0 || inside.Output.Trim() != "true")
            {
                logger.LogDebug("The folder {Root} is not a work tree of Git. The indexer walks it.", root);
                return null;
            }

            GitResult listed = await RunGitAsync(current, root, ["ls-files", "-z", "--cached", "--others", "--exclude-standard"], cancellationToken);
            if (listed.ExitCode != 0)
            {
                logger.LogWarning("Git failed to list the files of {Root}: {Error} The indexer walks the folder.", root, listed.Error.Trim());
                return null;
            }

            // A deleted file stays in the index, and a nested repository shows as a folder. Neither is a file.
            // A tracked link to a file outside the folder is no file of the source.
            SourceLinkGuard guard = new(root);
            List<string> files = [];
            foreach (string path in listed.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal))
            {
                if (!File.Exists(Path.Combine(root, path)))
                    continue;

                if (guard.IsInside(path))
                    files.Add(path);
                else
                    logger.LogDebug("The lister skipped the file {Path} in {Root}: it is a link to a place outside the folder.", path, root);
            }

            return files;
        }
        catch (Exception exception) when (exception is Win32Exception or IOException or TimeoutException or InvalidOperationException)
        {
            logger.LogWarning("Git does not work for {Root}: {Reason} The indexer walks the folder.", root, exception.Message);
            return null;
        }
    }

    private static async Task<GitResult> RunGitAsync(CodeOptions options, string root, string[] arguments, CancellationToken cancellationToken)
    {
        ProcessStartInfo start = new(options.GitPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("-C");
        start.ArgumentList.Add(root);
        foreach (string argument in arguments)
            start.ArgumentList.Add(argument);

        using Process process = Process.Start(start) ?? throw new InvalidOperationException("The system did not start Git.");
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, options.GitTimeoutSeconds)));
        try
        {
            Task<string> output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            Task<string> error = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            return new GitResult(process.ExitCode, await output, await error);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException($"Git ran longer than {options.GitTimeoutSeconds} seconds.");
        }
    }

    private sealed record GitResult(int ExitCode, string Output, string Error);
}
