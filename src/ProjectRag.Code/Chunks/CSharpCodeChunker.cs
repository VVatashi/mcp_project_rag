using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using ProjectRag.Core.Code.Chunks;

namespace ProjectRag.Code.Chunks;

/// <summary>
/// Splits C# files into chunks of members with the syntax tree of Roslyn. It needs no project and no semantic model.
/// </summary>
/// <remarks>
/// A small type is one chunk. A large type gives a header chunk and a chunk for each large member, and small neighbors merge.
/// A member that is too long splits between statements. The preprocessor symbols are none, so code of an inactive branch stays as text inside its range.
/// </remarks>
public sealed class CSharpCodeChunker : ICodeChunker
{
    private const string Language = "csharp";

    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Latest, DocumentationMode.None);

    /// <inheritdoc />
    public int Version => 1;

    /// <inheritdoc />
    public bool Supports(string language) => string.Equals(language, Language, StringComparison.Ordinal);

    /// <inheritdoc />
    public IReadOnlyList<CodeChunk> Split(string path, string content, CodeChunkingOptions options)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(content))
            return [];

        CompilationUnitSyntax root = CSharpSyntaxTree.ParseText(content, ParseOptions).GetCompilationUnitRoot();
        LineIndex lines = new(content);
        List<ChunkUnit> units = new ChunkPlanner(lines, Math.Max(1, options.MaxChars), Math.Max(0, options.MinChars)).Plan(root);
        ChunkSplitter splitter = new(lines, path, options);
        List<CodeChunk> chunks = new(units.Count);
        foreach (ChunkUnit unit in units)
            splitter.Emit(unit, chunks);

        return chunks;
    }
}
