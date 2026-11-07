using ProjectRag.Core.Code.Graph;

namespace ProjectRag.Code.Tests;

/// <summary>
/// Applies a patch to a graph in memory, with the rules of the contract of <see cref="CodeGraphPatch"/>.
/// </summary>
internal static class PatchMerger
{
    public static CodeGraph Apply(CodeGraph old, CodeGraphPatch patch)
    {
        CodeGraph delta = patch.Graph;
        HashSet<string> files = [.. patch.Files];

        List<string> oldKeys = [.. old.Symbols.Select(symbol => Key(old, symbol))];
        List<string> deltaKeys = [.. delta.Symbols.Select(symbol => Key(delta, symbol))];

        HashSet<string> declared = [];
        foreach (GraphDeclaration declaration in old.Declarations.Where(declaration => !files.Contains(declaration.Path)))
            declared.Add(oldKeys[declaration.Symbol]);

        foreach (GraphDeclaration declaration in delta.Declarations)
            declared.Add(deltaKeys[declaration.Symbol]);

        Dictionary<string, int> deltaIndexes = [];
        for (int i = 0; i < delta.Symbols.Count; i++)
        {
            if (!patch.Stubs.Contains(i))
                deltaIndexes[deltaKeys[i]] = i;
        }

        List<(string Key, GraphSymbol Symbol, string? Parent)> merged = [];
        HashSet<string> seen = [];
        for (int i = 0; i < old.Symbols.Count; i++)
        {
            if (!declared.Contains(oldKeys[i]) || !seen.Add(oldKeys[i]))
                continue;

            if (deltaIndexes.TryGetValue(oldKeys[i], out int replacement))
                merged.Add((oldKeys[i], delta.Symbols[replacement], ParentKey(delta, deltaKeys, replacement)));
            else
                merged.Add((oldKeys[i], old.Symbols[i], ParentKey(old, oldKeys, i)));
        }

        foreach ((string key, int index) in deltaIndexes.OrderBy(pair => pair.Value))
        {
            if (seen.Add(key))
                merged.Add((key, delta.Symbols[index], ParentKey(delta, deltaKeys, index)));
        }

        Dictionary<string, int> indexes = [];
        for (int i = 0; i < merged.Count; i++)
            indexes[merged[i].Key] = i;

        List<GraphSymbol> symbols = [.. merged.Select(item => item.Symbol with { Parent = item.Parent is not null && indexes.TryGetValue(item.Parent, out int parent) ? parent : null })];

        int? Map(IReadOnlyList<string> keys, int symbol) => indexes.TryGetValue(keys[symbol], out int index) ? index : null;

        List<GraphDeclaration> declarations = [];
        foreach (GraphDeclaration declaration in old.Declarations.Where(declaration => !files.Contains(declaration.Path)))
        {
            if (Map(oldKeys, declaration.Symbol) is { } symbol)
                declarations.Add(declaration with { Symbol = symbol });
        }

        foreach (GraphDeclaration declaration in delta.Declarations)
            declarations.Add(declaration with { Symbol = Map(deltaKeys, declaration.Symbol)!.Value });

        List<GraphReference> references = [];
        foreach (GraphReference reference in old.References.Where(reference => !files.Contains(reference.Path)))
        {
            if (Map(oldKeys, reference.Symbol) is { } target)
                references.Add(reference with { Symbol = target, From = reference.From is { } from ? Map(oldKeys, from) : null });
        }

        foreach (GraphReference reference in delta.References)
        {
            if (Map(deltaKeys, reference.Symbol) is { } target)
                references.Add(reference with { Symbol = target, From = reference.From is { } from ? Map(deltaKeys, from) : null });
        }

        List<GraphTypeEdge> typeEdges = [];
        AddEdges(typeEdges, old.TypeEdges.Where(edge => !files.Contains(edge.Path)), oldKeys);
        AddEdges(typeEdges, delta.TypeEdges, deltaKeys);

        List<GraphExternalEdge> externalEdges = [];
        foreach (GraphExternalEdge edge in old.ExternalEdges.Where(edge => !files.Contains(edge.Path)))
        {
            if (Map(oldKeys, edge.From) is { } from)
                externalEdges.Add(edge with { From = from });
        }

        foreach (GraphExternalEdge edge in delta.ExternalEdges)
        {
            if (Map(deltaKeys, edge.From) is { } from)
                externalEdges.Add(edge with { From = from });
        }

        List<GraphMemberLink> links = [];
        AddLinks(links, old.MemberLinks.Where(link => !files.Contains(link.Path)), oldKeys);
        AddLinks(links, delta.MemberLinks, deltaKeys);

        List<GraphProject> projects = [.. old.Projects.Select((project, index) => patch.CheckedProjects.Contains(index) ? project with { ErrorCount = delta.Projects[index].ErrorCount } : project)];
        List<GraphDiagnostic> diagnostics = [.. old.Diagnostics.Where(diagnostic => diagnostic.Project is not { } project || !patch.CheckedProjects.Contains(project)), .. delta.Diagnostics];

        return old with
        {
            Projects = projects,
            Symbols = symbols,
            Declarations = declarations,
            References = references,
            TypeEdges = typeEdges,
            ExternalEdges = externalEdges,
            MemberLinks = links,
            Diagnostics = diagnostics,
        };

        void AddEdges(List<GraphTypeEdge> target, IEnumerable<GraphTypeEdge> source, IReadOnlyList<string> keys)
        {
            foreach (GraphTypeEdge edge in source)
            {
                if (Map(keys, edge.From) is { } from && Map(keys, edge.To) is { } to)
                    target.Add(edge with { From = from, To = to });
            }
        }

        void AddLinks(List<GraphMemberLink> target, IEnumerable<GraphMemberLink> source, IReadOnlyList<string> keys)
        {
            foreach (GraphMemberLink link in source)
            {
                if (Map(keys, link.Member) is { } member && Map(keys, link.Target) is { } to)
                    target.Add(link with { Member = member, Target = to });
            }
        }
    }

    private static string Key(CodeGraph graph, GraphSymbol symbol) => graph.Projects[symbol.Project].Path + "|" + symbol.DocId;

    private static string? ParentKey(CodeGraph graph, List<string> keys, int symbol) => graph.Symbols[symbol].Parent is { } parent ? keys[parent] : null;
}
