namespace ProjectRag.Core.Code.Graph;

/// <summary>
/// A symbol of the code graph without its relations.
/// </summary>
/// <param name="SymbolId">The key of the symbol. It changes when the graph is built again.</param>
/// <param name="SourceId">The key of the code source.</param>
/// <param name="Source">The name of the code source.</param>
/// <param name="ProjectId">The key of the project that owns the symbol.</param>
/// <param name="Project">The name of the assembly of the project.</param>
/// <param name="Kind">The kind, one of <see cref="CodeSymbolKinds"/>.</param>
/// <param name="Name">The simple name.</param>
/// <param name="FullName">The name with the namespace and the containing types, without parameters.</param>
/// <param name="DocId">The documentation comment ID.</param>
/// <param name="Signature">The declaration as C# shows it.</param>
/// <param name="Accessibility">The accessibility.</param>
/// <param name="Summary">The first 300 characters of the summary of the XML documentation, or <see langword="null"/>.</param>
/// <param name="DocumentId">The file of the first declaration, or <see langword="null"/>.</param>
/// <param name="Path">The path of that file, or <see langword="null"/>.</param>
/// <param name="Line">The line of the name in the first declaration, or <see langword="null"/>.</param>
public record SymbolSummary(
    int SymbolId,
    int SourceId,
    string Source,
    int ProjectId,
    string Project,
    string Kind,
    string Name,
    string FullName,
    string DocId,
    string Signature,
    string Accessibility,
    string? Summary,
    long? DocumentId,
    string? Path,
    int? Line);

/// <summary>
/// A symbol that matches a query of the symbol search. The JSON holds the fields of the symbol and the class of the match.
/// </summary>
public sealed record SymbolMatch : SymbolSummary
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SymbolMatch"/> class.
    /// </summary>
    /// <param name="symbol">The symbol.</param>
    /// <param name="match">The class of the match, one of <see cref="CodeMatchKinds"/>.</param>
    public SymbolMatch(SymbolSummary symbol, string match)
        : base(symbol)
    {
        Match = match;
    }

    /// <summary>
    /// Gets the class of the match, one of <see cref="CodeMatchKinds"/>.
    /// </summary>
    public string Match { get; init; }
}
/// <summary>
/// The classes of the matches of the symbol search, in the order of the results.
/// </summary>
public static class CodeMatchKinds
{
    /// <summary>The name equals the query, with the case.</summary>
    public const string Exact = "exact";

    /// <summary>The name equals the query without the case.</summary>
    public const string ExactIgnoreCase = "exact_ci";

    /// <summary>The name starts with the query, without the case.</summary>
    public const string Prefix = "prefix";

    /// <summary>The query holds the first letters of the words of the name, such as <c>SSync</c> for <c>SceneSynchronizer</c>.</summary>
    public const string Humps = "humps";

    /// <summary>The full name ends with the query at a dot.</summary>
    public const string Qualified = "qualified";

    /// <summary>The name holds the query, without the case.</summary>
    public const string Substring = "substring";

    /// <summary>The name is similar to the query by its trigrams.</summary>
    public const string Fuzzy = "fuzzy";

    /// <summary>All classes in their order.</summary>
    public static IReadOnlyList<string> All { get; } = [Exact, ExactIgnoreCase, Prefix, Humps, Qualified, Substring, Fuzzy];
}

/// <summary>
/// The number of uses of one kind.
/// </summary>
/// <param name="Kind">The kind, one of <see cref="CodeUseKinds"/>.</param>
/// <param name="Count">The number of uses.</param>
public sealed record UseCount(string Kind, int Count);

/// <summary>
/// The uses between a symbol and another symbol.
/// </summary>
/// <param name="Symbol">The other side.</param>
/// <param name="Kinds">The uses by kind, in the order of <see cref="CodeUseKinds.All"/>.</param>
/// <param name="Total">The sum of the counts.</param>
public sealed record EdgeSummary(SymbolSummary Symbol, IReadOnlyList<UseCount> Kinds, int Total);

/// <summary>
/// The uses of an external type by a type of the code base.
/// </summary>
/// <param name="Target">The full name of the external type.</param>
/// <param name="Assembly">The name of its assembly.</param>
/// <param name="Kinds">The uses by kind, in the order of <see cref="CodeUseKinds.All"/>.</param>
/// <param name="Total">The sum of the counts.</param>
public sealed record ExternalUse(string Target, string Assembly, IReadOnlyList<UseCount> Kinds, int Total);

/// <summary>
/// The place of a declaration.
/// </summary>
/// <param name="DocumentId">The key of the file.</param>
/// <param name="Path">The path of the file.</param>
/// <param name="StartLine">The first line, with the attributes and the documentation comment.</param>
/// <param name="EndLine">The last line.</param>
/// <param name="NameLine">The line of the name.</param>
/// <param name="NameColumn">The column of the name.</param>
public sealed record CodeDeclaration(long DocumentId, string Path, int StartLine, int EndLine, int NameLine, int NameColumn);

/// <summary>
/// A symbol with its relations.
/// </summary>
/// <param name="Symbol">The symbol.</param>
/// <param name="Modifiers">The modifiers.</param>
/// <param name="Parent">The containing type, or <see langword="null"/>.</param>
/// <param name="Declarations">The places of the declarations.</param>
/// <param name="Members">For a type: its members and nested types in the order of their lines. Else empty.</param>
/// <param name="Bases">For a type: the base class and the interfaces in the code base.</param>
/// <param name="Derived">For a type: the types of the code base that inherit or implement it, direct only.</param>
/// <param name="Overrides">For a member: the members it overrides or implements.</param>
/// <param name="OverriddenBy">For a member: the members that override or implement it.</param>
/// <param name="Uses">What the symbol uses, by total descending, at most 200.</param>
/// <param name="UsedBy">What uses the symbol, by total descending, at most 200.</param>
/// <param name="ExternalUses">For a type: the external types it uses, by total descending, at most 100. Else empty.</param>
/// <param name="ReferenceCount">The number of references to the symbol.</param>
public sealed record SymbolDetail(
    SymbolSummary Symbol,
    IReadOnlyList<string> Modifiers,
    SymbolSummary? Parent,
    IReadOnlyList<CodeDeclaration> Declarations,
    IReadOnlyList<SymbolSummary> Members,
    IReadOnlyList<SymbolSummary> Bases,
    IReadOnlyList<SymbolSummary> Derived,
    IReadOnlyList<SymbolSummary> Overrides,
    IReadOnlyList<SymbolSummary> OverriddenBy,
    IReadOnlyList<EdgeSummary> Uses,
    IReadOnlyList<EdgeSummary> UsedBy,
    IReadOnlyList<ExternalUse> ExternalUses,
    int ReferenceCount);

/// <summary>
/// A reference to a symbol with the text of its line.
/// </summary>
/// <param name="DocumentId">The key of the file.</param>
/// <param name="Path">The path of the file.</param>
/// <param name="Line">The line.</param>
/// <param name="Column">The column of the name.</param>
/// <param name="Length">The length of the name.</param>
/// <param name="Kind">The kind of the use.</param>
/// <param name="Text">The line, cut to at most 400 characters around the name.</param>
/// <param name="TextStart">The index of <paramref name="Text"/> in the line, from 0.</param>
/// <param name="From">The innermost declared symbol around the reference, or <see langword="null"/>.</param>
public sealed record CodeReference(long DocumentId, string Path, int Line, int Column, int Length, string Kind, string Text, int TextStart, SymbolSummary? From);

/// <summary>
/// A page of references.
/// </summary>
/// <param name="Total">The number of all matching references.</param>
/// <param name="Items">The references of the page.</param>
public sealed record CodeReferencePage(long Total, IReadOnlyList<CodeReference> Items);

/// <summary>
/// A node of the outline of a file.
/// </summary>
/// <param name="Symbol">The symbol. Its place is the first declaration in this file.</param>
/// <param name="Children">The members of a type.</param>
public sealed record OutlineNode(SymbolSummary Symbol, IReadOnlyList<OutlineNode> Children);

/// <summary>
/// A project of a code base.
/// </summary>
/// <param name="ProjectId">The key of the project.</param>
/// <param name="Path">The project file.</param>
/// <param name="Name">The name of the assembly.</param>
/// <param name="TargetFramework">The target framework, or <see langword="null"/>.</param>
/// <param name="IsTest">Tells that the project holds tests.</param>
/// <param name="DocumentCount">The number of its files.</param>
/// <param name="ErrorCount">The number of compiler errors.</param>
/// <param name="SymbolCount">The number of symbols.</param>
/// <param name="References">The keys of the projects it references.</param>
public sealed record CodeProject(int ProjectId, string Path, string Name, string? TargetFramework, bool IsTest, int DocumentCount, int ErrorCount, int SymbolCount, IReadOnlyList<int> References);

/// <summary>
/// A solution of a code base.
/// </summary>
/// <param name="SolutionId">The key of the solution.</param>
/// <param name="Path">The solution file.</param>
/// <param name="ProjectIds">The keys of its projects.</param>
public sealed record CodeSolution(int SolutionId, string Path, IReadOnlyList<int> ProjectIds);

/// <summary>
/// A problem of the load of a project or a compiler error.
/// </summary>
/// <param name="Project">The name of the project, or <see langword="null"/>.</param>
/// <param name="Severity"><c>error</c> or <c>warning</c>.</param>
/// <param name="Code">The identifier.</param>
/// <param name="Message">The message.</param>
/// <param name="Path">The file, or <see langword="null"/>.</param>
/// <param name="Line">The line, or <see langword="null"/>.</param>
public sealed record CodeDiagnostic(string? Project, string Severity, string Code, string Message, string? Path, int? Line);

/// <summary>
/// The state of the graph of a code source with its projects.
/// </summary>
/// <param name="SourceId">The key of the source.</param>
/// <param name="Source">The name of the source.</param>
/// <param name="BuiltAt">The end of the last successful full build, or <see langword="null"/> when there is no graph.</param>
/// <param name="DurationMs">The time of that build in milliseconds.</param>
/// <param name="UpdatedAt">The end of the last incremental update, or <see langword="null"/> when no update followed the last full build.</param>
/// <param name="UpdateCount">The number of incremental updates since the last full build.</param>
/// <param name="UpdateDurationMs">The time of the last incremental update in milliseconds, or <see langword="null"/> when there is none.</param>
/// <param name="SdkVersion">The version of the SDK that loaded the projects.</param>
/// <param name="Error">The error of the last build. The old graph stays.</param>
/// <param name="ProjectCount">The number of projects.</param>
/// <param name="SymbolCount">The number of symbols.</param>
/// <param name="ReferenceCount">The number of references.</param>
/// <param name="Projects">The projects.</param>
/// <param name="Solutions">The solutions.</param>
/// <param name="Diagnostics">The problems, at most 200.</param>
public sealed record CodeGraphStatus(
    int SourceId,
    string Source,
    DateTime? BuiltAt,
    long? DurationMs,
    DateTime? UpdatedAt,
    int UpdateCount,
    long? UpdateDurationMs,
    string? SdkVersion,
    string? Error,
    int ProjectCount,
    int SymbolCount,
    int ReferenceCount,
    IReadOnlyList<CodeProject> Projects,
    IReadOnlyList<CodeSolution> Solutions,
    IReadOnlyList<CodeDiagnostic> Diagnostics);
