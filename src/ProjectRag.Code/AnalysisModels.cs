using Microsoft.CodeAnalysis;

using ProjectRag.Core.Code.Graph;

namespace ProjectRag.Code;

/// <summary>
/// The key of a symbol in the graph: the owner project and the documentation comment ID.
/// </summary>
internal readonly record struct SymbolKey(int Project, string DocId);

/// <summary>
/// A file that a project owns.
/// </summary>
internal sealed record OwnedFile(Document Document, string Path);

/// <summary>
/// A project of the graph with the files that it owns.
/// </summary>
internal sealed record ProjectUnit(int Index, string Path, Project Project, IReadOnlyList<OwnedFile> Files);

/// <summary>
/// The place of a declaration in a file.
/// </summary>
internal readonly record struct LocalDeclaration(string Path, int StartLine, int EndLine, int NameLine, int NameColumn);

/// <summary>
/// A symbol that a project declares. The index in the list of the project is its identity during the analysis.
/// </summary>
internal sealed class LocalSymbol
{
    public required string DocId { get; init; }

    public required string Kind { get; init; }

    public required string Name { get; init; }

    public required string FullName { get; init; }

    public required string Signature { get; init; }

    public required string Namespace { get; init; }

    public required string Accessibility { get; init; }

    public required string? Summary { get; init; }

    public required SymbolKey? Parent { get; init; }

    public required ISymbol? Symbol { get; set; }

    public HashSet<string> Modifiers { get; } = new(StringComparer.Ordinal);

    public List<LocalDeclaration> Declarations { get; } = [];
}

/// <summary>
/// A reference whose target is not mapped to a symbol index yet.
/// </summary>
internal readonly record struct RawReference(SymbolKey Target, SymbolKey? TypeKey, string Path, int Line, int Column, int Length, string Kind, int From);

/// <summary>
/// An override or an implementation whose target is not mapped to a symbol index yet.
/// </summary>
internal readonly record struct RawLink(int Member, SymbolKey Target, string Kind, string Path);

/// <summary>
/// A use of an external type by a local symbol, in a file.
/// </summary>
internal readonly record struct ExternalUse(int From, string Target, string Assembly, string Kind, string Path);

/// <summary>
/// A symbol that an update points to but does not declare in its files.
/// </summary>
internal sealed record StubSymbol(string Kind, string Name, string FullName, string Signature, string Namespace, string Accessibility, string Path);

/// <summary>
/// What the analysis of one project found.
/// </summary>
internal sealed class ProjectResult
{
    public List<LocalSymbol> Symbols { get; } = [];

    public List<RawReference> References { get; } = [];

    public Dictionary<ExternalUse, int> External { get; } = [];

    public HashSet<RawLink> Links { get; } = [];

    public Dictionary<SymbolKey, StubSymbol> Stubs { get; } = [];

    public List<GraphDiagnostic> Diagnostics { get; } = [];

    public int ErrorCount { get; set; }
}

/// <summary>
/// What a referenced symbol means for the graph.
/// </summary>
internal sealed class TargetInfo
{
    public static TargetInfo Skipped { get; } = new();

    public SymbolKey? Key { get; init; }

    public SymbolKey? TypeKey { get; init; }

    public string? ExternalName { get; init; }

    public string? ExternalAssembly { get; init; }

    public bool IsSkipped => Key is null && TypeKey is null && ExternalName is null;
}
