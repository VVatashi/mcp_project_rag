using ProjectRag.Core.Code;
using ProjectRag.Core.Sources;

namespace ProjectRag.Core.Tests.Code;

public sealed class SourceLinkGuardTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("project-rag-links-").FullName;
    private readonly string _outside = Directory.CreateTempSubdirectory("project-rag-outside-").FullName;

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
        Directory.Delete(_outside, recursive: true);
    }

    [Fact]
    public void IsInside_RejectsALinkToAFileOutsideAndKeepsALinkInside()
    {
        string secret = Path.Combine(_outside, "key.txt");
        File.WriteAllText(secret, "secret");
        File.WriteAllText(Path.Combine(_root, "real.txt"), "real");
        CreateFileLink("docs/key.txt", secret);
        CreateFileLink("alias.txt", "real.txt");
        SourceLinkGuard guard = new(_root);

        Assert.True(guard.IsInside("real.txt"));
        Assert.True(guard.IsInside("alias.txt"));
        Assert.False(guard.IsInside("docs/key.txt"));
    }

    [Fact]
    public void IsInside_RejectsAFileUnderALinkedFolderOutside()
    {
        File.WriteAllText(Path.Combine(_outside, "key.txt"), "secret");
        CreateFolderLink("linked", _outside);
        SourceLinkGuard guard = new(_root);

        Assert.False(guard.IsInside("linked/key.txt"));
    }

    [Fact]
    public void IsInside_RejectsAChainOfLinksThatEndsOutside()
    {
        string secret = Path.Combine(_outside, "key.txt");
        File.WriteAllText(secret, "secret");
        CreateFileLink("first.txt", secret);
        CreateFileLink("second.txt", "first.txt");
        SourceLinkGuard guard = new(_root);

        Assert.False(guard.IsInside("second.txt"));
    }

    [Fact]
    public void Walk_DropsALinkToAFileOutsideAndKeepsALinkInside()
    {
        string secret = Path.Combine(_outside, "key.txt");
        File.WriteAllText(secret, "secret");
        File.WriteAllText(Path.Combine(_root, "real.txt"), "real");
        CreateFileLink("docs/key.txt", secret);
        CreateFileLink("alias.txt", "real.txt");

        Assert.Equal(["alias.txt", "real.txt"], CodeFileLister.Walk(_root).Order(StringComparer.Ordinal));
    }

    private void CreateFileLink(string path, string target) => Create(path, full => File.CreateSymbolicLink(full, target));

    private void CreateFolderLink(string path, string target) => Create(path, full => Directory.CreateSymbolicLink(full, target));

    // Windows denies symbolic links to a process without the right of the developer mode.
    private void Create(string path, Action<string> create)
    {
        string full = Path.Combine(_root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        try
        {
            create(full);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            Assert.Skip($"The system does not allow a symbolic link: {exception.Message}");
        }
    }
}
