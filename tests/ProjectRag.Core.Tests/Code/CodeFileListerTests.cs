using System.Diagnostics;

using Microsoft.Extensions.Logging.Abstractions;

using ProjectRag.Core.Code;
using ProjectRag.Core.Sources;

namespace ProjectRag.Core.Tests.Code;

public sealed class CodeFileListerTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("project-rag-").FullName;

    public void Dispose()
    {
        // Git marks the files of its objects as read-only.
        foreach (string file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);

        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void Walk_AppliesTheNestedFilesWithNegationAndKeepsTheOtherRules()
    {
        Write(".gitignore", "*.log\n!keep.log\nbuild/\n");
        Write("a.cs");
        Write("a.log");
        Write("keep.log");
        Write("build/x.cs");
        Write("src/.gitignore", "!debug.log\n/local.cs\n*.tmp\n");
        Write("src/debug.log");
        Write("src/b.cs");
        Write("src/local.cs");
        Write("src/sub/local.cs");
        Write("src/z.tmp");

        Assert.Equal(["a.cs", "keep.log", "src/b.cs", "src/debug.log", "src/sub/local.cs"], Filtered());
    }

    [Fact]
    public void Walk_DoesNotReachAFileInAnIgnoredFolder()
    {
        Write(".gitignore", "build/\n");
        Write("build/.gitignore", "!keep.cs\n");
        Write("build/keep.cs");
        Write("a.cs");

        Assert.Equal(["a.cs"], Filtered());
    }

    [Fact]
    public void Walk_SkipsBinObjAndNodeModulesAtAnyDepth()
    {
        Write("a.cs");
        Write("bin/x.cs");
        Write("obj/y.cs");
        Write("node_modules/m.js");
        Write("src/bin/z.cs");
        Write("src/Obj/z.cs");
        Write("src/app/node_modules/n.js");
        Write("src/binary/ok.cs");

        Assert.Equal(["a.cs", "src/binary/ok.cs"], Filtered());
    }

    [Fact]
    public void Filter_DropsDotFilesExceptTheDotPatterns()
    {
        Write(".hidden/h.cs");
        Write(".github/workflows/ci.yml");
        Write(".github/readme.md");
        Write(".editorconfig");
        Write(".config/tools.json");
        Write(".config/other.txt");
        Write("src/.secret/s.cs");
        Write("src/.env");
        Write("a.cs");

        Assert.Equal([".config/tools.json", ".editorconfig", ".github/workflows/ci.yml", "a.cs"], Filtered());
        Assert.Contains("src/.env", Filtered(["src/.env"]));
        Assert.DoesNotContain("src/.secret/s.cs", Filtered(["src/.env"]));
    }

    [Fact]
    public void Walk_PrunesNestedRepositoriesButNotTheRoot()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".git", "info"));
        Write(".git/info/exclude", "excluded.cs\n");
        Write(".git/config");
        Write("excluded.cs");
        Write("a.cs");
        Write("sub/.git/config");
        Write("sub/s.cs");
        Write("worktree/.git", "gitdir: ../.git/worktrees/w");
        Write("worktree/t.cs");

        Assert.Equal(["a.cs"], Filtered());
    }

    [Fact]
    public void Filter_AppliesTheIncludeAndExcludePatterns()
    {
        Write("a.cs");
        Write("b.txt");
        Write("artifacts/c.cs");
        Write("lib/package-lock.json");
        Write("lib/d.cs");
        Source source = new() { Name = "t", Kind = SourceKind.Code, RootPath = _root, IncludeGlobs = ["**/*.CS", "**/*.json"], ExcludeGlobs = SourceInput.DefaultCodeExcludeGlobs };

        IReadOnlyList<string> files = CodeFileLister.Filter(source, CodeFileLister.Walk(_root));

        Assert.Equal(["a.cs", "lib/d.cs"], files);
    }

    [Fact]
    public async Task ListAsync_FallsBackToTheWalkWhenGitIsMissing()
    {
        Write(".gitignore", "*.log\n");
        Write("a.cs");
        Write("a.log");
        CodeFileLister lister = new(new FixedOptions<CodeOptions>(new CodeOptions { GitPath = "git-that-does-not-exist" }), NullLogger<CodeFileLister>.Instance);

        IReadOnlyList<string> files = await lister.ListAsync(Code(), TestContext.Current.CancellationToken);

        Assert.Equal(["a.cs"], files);
    }

    [Fact]
    public async Task ListAsync_ThrowsForAMissingFolder()
    {
        CodeFileLister lister = new(new FixedOptions<CodeOptions>(new CodeOptions()), NullLogger<CodeFileLister>.Instance);

        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => lister.ListAsync(Code(Path.Combine(_root, "missing")), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ListAsync_ReadsTheFilesFromGitInAWorkTree()
    {
        Assert.SkipUnless(GitExists(), "Git is not installed.");
        Write("a.cs");
        Write("b.cs");
        Write("c.cs");
        Write("ignored.cs");
        Write("untracked.cs");
        Write("by-gitignore.log");
        Write(".gitignore", "*.log\n");
        Write("nested/n.cs");
        RunGit(_root, "init", "--quiet");
        RunGit(_root, "add", "a.cs", "b.cs", "c.cs");
        RunGit(Path.Combine(_root, "nested"), "init", "--quiet");
        Write(".git/info/exclude", "ignored.cs\n");
        File.Delete(Path.Combine(_root, "b.cs"));
        CodeFileLister lister = new(new FixedOptions<CodeOptions>(new CodeOptions()), NullLogger<CodeFileLister>.Instance);

        IReadOnlyList<string> files = await lister.ListAsync(Code(), TestContext.Current.CancellationToken);

        Assert.Equal(["a.cs", "c.cs", "untracked.cs"], files);
    }

    [Fact]
    public async Task ListAsync_ListsOnlyTheSubfolderOfAWorkTree()
    {
        Assert.SkipUnless(GitExists(), "Git is not installed.");
        Write("top.cs");
        Write("sub/s.cs");
        RunGit(_root, "init", "--quiet");
        CodeFileLister lister = new(new FixedOptions<CodeOptions>(new CodeOptions()), NullLogger<CodeFileLister>.Instance);

        IReadOnlyList<string> files = await lister.ListAsync(Code(Path.Combine(_root, "sub")), TestContext.Current.CancellationToken);

        Assert.Equal(["s.cs"], files);
    }

    private Source Code(string? root = null, IReadOnlyList<string>? dotGlobs = null) => new()
    {
        Name = "t",
        Kind = SourceKind.Code,
        RootPath = root ?? _root,
        IncludeGlobs = SourceInput.DefaultCodeIncludeGlobs,
        ExcludeGlobs = SourceInput.DefaultCodeExcludeGlobs,
        DotGlobs = dotGlobs ?? SourceInput.DefaultCodeDotGlobs,
    };

    private IReadOnlyList<string> Filtered(IReadOnlyList<string>? dotGlobs = null) => CodeFileLister.Filter(Code(null, dotGlobs), CodeFileLister.Walk(_root));

    private void Write(string path, string text = "x")
    {
        string full = Path.Combine(_root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
    }

    private static bool GitExists()
    {
        try
        {
            RunGit(Path.GetTempPath(), "--version");
            return true;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    private static void RunGit(string directory, params string[] arguments)
    {
        ProcessStartInfo start = new("git") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (string argument in arguments)
            start.ArgumentList.Add(argument);

        using Process process = Process.Start(start)!;
        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed.");
    }
}
