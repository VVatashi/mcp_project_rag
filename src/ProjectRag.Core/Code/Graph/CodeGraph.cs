namespace ProjectRag.Core.Code.Graph;

/// <summary>
/// What the extractor of a code graph reads.
/// </summary>
/// <param name="RootPath">The absolute path of the folder of the code base.</param>
/// <param name="ProjectPaths">The project files of the code base, relative to the folder, with forward slashes. The extractor opens each of them.</param>
/// <param name="SolutionPaths">The solution files of the code base, relative to the folder, with forward slashes. They are metadata only.</param>
/// <param name="KnownFiles">The files of the code base that the index keeps, relative to the folder, with forward slashes.
/// The graph holds declarations and references in these files only. Generated files and files outside the code base give no rows.</param>
/// <param name="Properties">The global properties of MSBuild: the view of the build.</param>
public sealed record CodeGraphInput(
    string RootPath,
    IReadOnlyList<string> ProjectPaths,
    IReadOnlyList<string> SolutionPaths,
    IReadOnlySet<string> KnownFiles,
    IReadOnlyDictionary<string, string> Properties);

/// <summary>
/// The map of a code base as the compiler sees it.
/// </summary>
/// <remarks>
/// The items point to each other by their index in their list. A path is relative to the folder of the code base, with forward slashes.
/// Lines and columns count from 1, and a column counts UTF-16 units, as the exact search does.
/// </remarks>
/// <param name="SdkVersion">The version of the .NET SDK that loaded the projects.</param>
/// <param name="Projects">The projects.</param>
/// <param name="Solutions">The solutions.</param>
/// <param name="FileProjects">The projects of each file.</param>
/// <param name="Symbols">The symbols that the files of the code base declare.</param>
/// <param name="Declarations">The places of the declarations. A partial type has several.</param>
/// <param name="References">The references to the symbols of the code base.</param>
/// <param name="TypeEdges">The uses of a type by another type of the code base, summed by kind.</param>
/// <param name="ExternalEdges">The uses of external types by a type of the code base, summed by kind.</param>
/// <param name="MemberLinks">The overrides and the implementations of interface members.</param>
/// <param name="Diagnostics">The problems of the load and the compiler errors.</param>
public sealed record CodeGraph(
    string SdkVersion,
    IReadOnlyList<GraphProject> Projects,
    IReadOnlyList<GraphSolution> Solutions,
    IReadOnlyList<GraphFileProject> FileProjects,
    IReadOnlyList<GraphSymbol> Symbols,
    IReadOnlyList<GraphDeclaration> Declarations,
    IReadOnlyList<GraphReference> References,
    IReadOnlyList<GraphTypeEdge> TypeEdges,
    IReadOnlyList<GraphExternalEdge> ExternalEdges,
    IReadOnlyList<GraphMemberLink> MemberLinks,
    IReadOnlyList<GraphDiagnostic> Diagnostics);

/// <summary>
/// A project of the code base.
/// </summary>
/// <param name="Path">The project file.</param>
/// <param name="Name">The name of the assembly.</param>
/// <param name="TargetFramework">The target framework of the loaded view, or <see langword="null"/> when the project file names none.</param>
/// <param name="IsTest">Tells that the project holds tests: <c>IsTestProject</c> is true, or it references a test framework.</param>
/// <param name="ProjectReferences">The indexes of the projects that this project references.</param>
/// <param name="DocumentCount">The number of its files that belong to the code base.</param>
/// <param name="ErrorCount">The number of compiler errors.</param>
public sealed record GraphProject(string Path, string Name, string? TargetFramework, bool IsTest, IReadOnlyList<int> ProjectReferences, int DocumentCount, int ErrorCount);

/// <summary>
/// A solution of the code base.
/// </summary>
/// <param name="Path">The solution file.</param>
/// <param name="Projects">The indexes of its projects that belong to the code base.</param>
public sealed record GraphSolution(string Path, IReadOnlyList<int> Projects);

/// <summary>
/// A project that compiles a file.
/// </summary>
/// <param name="Path">The file.</param>
/// <param name="Project">The index of the project.</param>
/// <param name="IsOwner">Tells that the project owns the file: the symbols and the references of the file come from its compilation. A file has one owner.</param>
public sealed record GraphFileProject(string Path, int Project, bool IsOwner);

/// <summary>
/// A type or a member that a file of the code base declares.
/// </summary>
/// <param name="Project">The index of the project that owns the declaring file.</param>
/// <param name="Parent">The index of the containing type, or <see langword="null"/> for a top-level type.</param>
/// <param name="Kind">The kind, one of <see cref="CodeSymbolKinds"/>.</param>
/// <param name="Name">The simple name, such as <c>Update</c> or <c>List</c>, without type parameters.</param>
/// <param name="FullName">The name with the namespace and the containing types, without parameters, such as <c>Axion.Core.Scene.SceneHost.Update</c>.</param>
/// <param name="DocId">The documentation comment ID, such as <c>M:Axion.Core.Scene.SceneHost.Update(System.Double)</c>. It is unique within a project.</param>
/// <param name="Signature">The declaration as C# shows it, such as <c>public void Update(double time)</c>.</param>
/// <param name="Namespace">The namespace, or an empty text for the global namespace.</param>
/// <param name="Accessibility">The accessibility: public, internal, protected, protected internal, private protected or private.</param>
/// <param name="Modifiers">The modifiers: static, abstract, sealed, virtual, override, readonly, async, partial, extern, new, const, required.</param>
/// <param name="Summary">The text of the summary of the XML documentation, with the tags removed, or <see langword="null"/>.</param>
public sealed record GraphSymbol(
    int Project,
    int? Parent,
    string Kind,
    string Name,
    string FullName,
    string DocId,
    string Signature,
    string Namespace,
    string Accessibility,
    IReadOnlyList<string> Modifiers,
    string? Summary);

/// <summary>
/// The place of a declaration of a symbol.
/// </summary>
/// <param name="Symbol">The index of the symbol.</param>
/// <param name="Path">The file.</param>
/// <param name="StartLine">The first line of the declaration, with its attributes and documentation comment.</param>
/// <param name="EndLine">The last line of the declaration.</param>
/// <param name="NameLine">The line of the name.</param>
/// <param name="NameColumn">The column of the name.</param>
public sealed record GraphDeclaration(int Symbol, string Path, int StartLine, int EndLine, int NameLine, int NameColumn);

/// <summary>
/// A reference to a symbol of the code base.
/// </summary>
/// <param name="Symbol">The index of the symbol that the reference points to.</param>
/// <param name="Path">The file of the reference.</param>
/// <param name="Line">The line.</param>
/// <param name="Column">The column of the name.</param>
/// <param name="Length">The length of the name.</param>
/// <param name="Kind">The kind, one of <see cref="CodeUseKinds"/>.</param>
/// <param name="From">The index of the innermost declared symbol around the reference, a member or a type, or <see langword="null"/>.</param>
public sealed record GraphReference(int Symbol, string Path, int Line, int Column, int Length, string Kind, int? From);

/// <summary>
/// The uses of one type of the code base by another, of one kind.
/// </summary>
/// <param name="From">The index of the type that uses.</param>
/// <param name="To">The index of the type that is used: the referenced type, or the containing type of a referenced member.</param>
/// <param name="Kind">The kind, one of <see cref="CodeUseKinds"/>.</param>
/// <param name="Count">The number of references.</param>
/// <param name="Path">The file of the references. A pair of types used in several files has one edge for each file, so a patch can replace the edges of a file.</param>
public sealed record GraphTypeEdge(int From, int To, string Kind, int Count, string Path);

/// <summary>
/// The uses of an external type by a type of the code base, of one kind.
/// </summary>
/// <param name="From">The index of the type that uses.</param>
/// <param name="Target">The full name of the external type, such as <c>System.Collections.Generic.List&lt;T&gt;</c>.</param>
/// <param name="Assembly">The name of its assembly.</param>
/// <param name="Kind">The kind, one of <see cref="CodeUseKinds"/>.</param>
/// <param name="Count">The number of references.</param>
/// <param name="Path">The file of the references.</param>
public sealed record GraphExternalEdge(int From, string Target, string Assembly, string Kind, int Count, string Path);

/// <summary>
/// A member that overrides another member or implements a member of an interface.
/// </summary>
/// <param name="Member">The index of the member that overrides or implements.</param>
/// <param name="Target">The index of the overridden or implemented member.</param>
/// <param name="Kind"><c>override</c> or <c>implement</c>.</param>
/// <param name="Path">The file of the first declaration of <paramref name="Member"/>.</param>
public sealed record GraphMemberLink(int Member, int Target, string Kind, string Path);

/// <summary>
/// A problem of the load of a project or a compiler error.
/// </summary>
/// <param name="Project">The index of the project, or <see langword="null"/> for a problem of the workspace.</param>
/// <param name="Severity"><c>error</c> or <c>warning</c>.</param>
/// <param name="Code">The identifier, such as <c>CS0246</c>, or an empty text.</param>
/// <param name="Message">The message.</param>
/// <param name="Path">The file, or <see langword="null"/>.</param>
/// <param name="Line">The line, or <see langword="null"/>.</param>
public sealed record GraphDiagnostic(int? Project, string Severity, string Code, string Message, string? Path, int? Line);

/// <summary>
/// The kinds of the symbols of a code graph.
/// </summary>
public static class CodeSymbolKinds
{
    /// <summary>A class.</summary>
    public const string Class = "class";

    /// <summary>A record class.</summary>
    public const string Record = "record";

    /// <summary>A struct.</summary>
    public const string Struct = "struct";

    /// <summary>A record struct.</summary>
    public const string RecordStruct = "record_struct";

    /// <summary>An interface.</summary>
    public const string Interface = "interface";

    /// <summary>An enum.</summary>
    public const string Enum = "enum";

    /// <summary>A delegate.</summary>
    public const string Delegate = "delegate";

    /// <summary>A method, including a local method of a type but not a local function.</summary>
    public const string Method = "method";

    /// <summary>A constructor or a static constructor.</summary>
    public const string Constructor = "constructor";

    /// <summary>A finalizer.</summary>
    public const string Destructor = "destructor";

    /// <summary>A user-defined operator.</summary>
    public const string Operator = "operator";

    /// <summary>A user-defined conversion.</summary>
    public const string Conversion = "conversion";

    /// <summary>A property.</summary>
    public const string Property = "property";

    /// <summary>An indexer.</summary>
    public const string Indexer = "indexer";

    /// <summary>A field.</summary>
    public const string Field = "field";

    /// <summary>A constant.</summary>
    public const string Constant = "constant";

    /// <summary>An event.</summary>
    public const string Event = "event";

    /// <summary>A member of an enum.</summary>
    public const string EnumMember = "enum_member";

    /// <summary>The kinds of types.</summary>
    public static IReadOnlyList<string> Types { get; } = [Class, Record, Struct, RecordStruct, Interface, Enum, Delegate];

    /// <summary>
    /// Checks whether a kind is a kind of type.
    /// </summary>
    /// <param name="kind">The kind.</param>
    /// <returns><see langword="true"/> for a type.</returns>
    public static bool IsType(string kind) => Types.Contains(kind, StringComparer.Ordinal);
}

/// <summary>
/// The kinds of the references and of the edges between types.
/// </summary>
public static class CodeUseKinds
{
    /// <summary>A base class in the base list.</summary>
    public const string Inherit = "inherit";

    /// <summary>An interface in the base list.</summary>
    public const string Implement = "implement";

    /// <summary>A type in a declaration: the type of a field, a property or an event, a parameter, a return type or a constraint.</summary>
    public const string Signature = "signature";

    /// <summary>A creation with <c>new</c>.</summary>
    public const string Create = "create";

    /// <summary>A call of a method, a constructor through <c>base</c> or <c>this</c>, or a delegate.</summary>
    public const string Call = "call";

    /// <summary>An access to a field, a property, an event or a member of an enum.</summary>
    public const string Member = "member";

    /// <summary>Another use of a type in code: a local variable, a generic argument, <c>typeof</c>, a cast, a pattern or a static access.</summary>
    public const string Type = "type";

    /// <summary>An attribute.</summary>
    public const string Attribute = "attribute";

    /// <summary>All kinds in their order.</summary>
    public static IReadOnlyList<string> All { get; } = [Inherit, Implement, Signature, Create, Call, Member, Type, Attribute];
}
