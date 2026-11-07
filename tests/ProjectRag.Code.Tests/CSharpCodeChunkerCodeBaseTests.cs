using ProjectRag.Code.Chunks;
using ProjectRag.Core.Code.Chunks;

namespace ProjectRag.Code.Tests;

public sealed class CSharpCodeChunkerCodeBaseTests
{
    private const string RootVariable = "PROJECTRAG_AXION_ROOT";

    [Fact]
    public void Split_EveryFileOfCodeBase_KeepsTheInvariants()
    {
        string? root = Environment.GetEnvironmentVariable(RootVariable);
        Assert.SkipUnless(!string.IsNullOrEmpty(root) && Directory.Exists(root), $"Set {RootVariable} to the folder of a C# code base.");

        CSharpCodeChunker chunker = new();
        CodeChunkingOptions options = new();
        EnumerationOptions enumeration = new() { RecurseSubdirectories = true };
        foreach (string file in Directory.EnumerateFiles(root!, "*.cs", enumeration))
        {
            string path = Path.GetRelativePath(root!, file).Replace('\\', '/');
            if (path.StartsWith('.') || path.Contains("/.", StringComparison.Ordinal) || path.Contains("/obj/", StringComparison.Ordinal) || path.Contains("/bin/", StringComparison.Ordinal))
                continue;

            string content = File.ReadAllText(file).Replace("\r\n", "\n", StringComparison.Ordinal);
            IReadOnlyList<CodeChunk> chunks = chunker.Split(path, content, options);
            ChunkAssertions.AssertInvariants(path, content, chunks, options);
        }
    }
}
