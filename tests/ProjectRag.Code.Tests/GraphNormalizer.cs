using ProjectRag.Core.Code.Graph;

namespace ProjectRag.Code.Tests;

/// <summary>
/// A graph as sorted lists of texts. A symbol is named by the path of its project and its documentation ID, so two graphs with other indexes compare equal.
/// </summary>
internal sealed record NormalizedGraph(
    IReadOnlyList<string> Projects,
    IReadOnlyList<string> Symbols,
    IReadOnlyList<string> Declarations,
    IReadOnlyList<string> References,
    IReadOnlyList<string> TypeEdges,
    IReadOnlyList<string> ExternalEdges,
    IReadOnlyList<string> MemberLinks,
    IReadOnlyList<string> Diagnostics,
    IReadOnlyList<string> FileProjects,
    IReadOnlyList<string> Solutions)
{
    public static NormalizedGraph Of(CodeGraph graph)
    {
        string Name(int symbol) => graph.Projects[graph.Symbols[symbol].Project].Path + "|" + graph.Symbols[symbol].DocId;

        string? NameOf(int? symbol) => symbol is { } value ? Name(value) : null;

        string Project(int? project) => project is { } value ? graph.Projects[value].Path : "-";

        return new NormalizedGraph(
            Sorted(graph.Projects.Select(project => string.Join('|', project.Path, project.Name, project.TargetFramework, project.IsTest, string.Join(',', project.ProjectReferences.Select(reference => graph.Projects[reference].Path)), project.DocumentCount, project.ErrorCount))),
            Sorted(graph.Symbols.Select((symbol, index) => string.Join('|', Name(index), NameOf(symbol.Parent), symbol.Kind, symbol.Name, symbol.FullName, symbol.Signature, symbol.Namespace, symbol.Accessibility, string.Join(',', symbol.Modifiers), symbol.Summary))),
            Sorted(graph.Declarations.Select(declaration => string.Join('|', Name(declaration.Symbol), declaration.Path, declaration.StartLine, declaration.EndLine, declaration.NameLine, declaration.NameColumn))),
            Sorted(graph.References.Select(reference => string.Join('|', Name(reference.Symbol), reference.Path, reference.Line, reference.Column, reference.Length, reference.Kind, NameOf(reference.From)))),
            Sorted(graph.TypeEdges.Select(edge => string.Join('|', Name(edge.From), Name(edge.To), edge.Kind, edge.Count, edge.Path))),
            Sorted(graph.ExternalEdges.Select(edge => string.Join('|', Name(edge.From), edge.Target, edge.Assembly, edge.Kind, edge.Count, edge.Path))),
            Sorted(graph.MemberLinks.Select(link => string.Join('|', Name(link.Member), Name(link.Target), link.Kind, link.Path))),
            Sorted(graph.Diagnostics.Select(diagnostic => string.Join('|', Project(diagnostic.Project), diagnostic.Severity, diagnostic.Code, diagnostic.Message, diagnostic.Path, diagnostic.Line))),
            Sorted(graph.FileProjects.Select(row => string.Join('|', row.Path, graph.Projects[row.Project].Path, row.IsOwner))),
            Sorted(graph.Solutions.Select(solution => string.Join('|', solution.Path, string.Join(',', solution.Projects.Select(project => graph.Projects[project].Path))))));
    }

    public static void AssertEqual(NormalizedGraph expected, NormalizedGraph actual)
    {
        Assert.Equal(expected.Projects, actual.Projects);
        Assert.Equal(expected.Symbols, actual.Symbols);
        Assert.Equal(expected.Declarations, actual.Declarations);
        Assert.Equal(expected.References, actual.References);
        Assert.Equal(expected.TypeEdges, actual.TypeEdges);
        Assert.Equal(expected.ExternalEdges, actual.ExternalEdges);
        Assert.Equal(expected.MemberLinks, actual.MemberLinks);
        Assert.Equal(expected.Diagnostics, actual.Diagnostics);
        Assert.Equal(expected.FileProjects, actual.FileProjects);
        Assert.Equal(expected.Solutions, actual.Solutions);
    }

    private static List<string> Sorted(IEnumerable<string> rows) => [.. rows.Order(StringComparer.Ordinal)];
}
