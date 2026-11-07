using ProjectRag.Core.Code.Graph;

namespace ProjectRag.Code;

/// <summary>
/// The rows of the symbols, declarations, references, edges and links of some projects.
/// </summary>
/// <param name="Symbols">The symbols. The stubs of an update come last.</param>
/// <param name="Declarations">The declarations.</param>
/// <param name="References">The references.</param>
/// <param name="TypeEdges">The edges between types.</param>
/// <param name="ExternalEdges">The edges to external types.</param>
/// <param name="Links">The member links.</param>
/// <param name="Stubs">The indexes of the stubs in <paramref name="Symbols"/>.</param>
internal sealed record AssembledGraph(
    List<GraphSymbol> Symbols,
    List<GraphDeclaration> Declarations,
    List<GraphReference> References,
    List<GraphTypeEdge> TypeEdges,
    List<GraphExternalEdge> ExternalEdges,
    List<GraphMemberLink> Links,
    HashSet<int> Stubs);

/// <summary>
/// Turns the results of the analysis of projects into the rows of the graph. A full build and an update use it alike.
/// </summary>
/// <param name="patchFiles">The files of an update, or <see langword="null"/> for a full build. An update turns a symbol outside these files into a stub.</param>
internal sealed class GraphAssembler(IReadOnlySet<string>? patchFiles)
{
    public AssembledGraph Assemble(IReadOnlyList<ProjectUnit> units, IReadOnlyList<ProjectResult> results)
    {
        Dictionary<SymbolKey, int> keys = [];
        Dictionary<int, int> offsets = [];
        int total = 0;
        for (int i = 0; i < units.Count; i++)
        {
            offsets[units[i].Index] = total;
            foreach (LocalSymbol local in results[i].Symbols)
                keys[new SymbolKey(units[i].Index, local.DocId)] = total++;
        }

        Dictionary<SymbolKey, StubSymbol> stubInfos = [];
        foreach (ProjectResult result in results)
        {
            foreach ((SymbolKey key, StubSymbol stub) in result.Stubs)
                stubInfos.TryAdd(key, stub);
        }

        List<GraphSymbol> stubs = [];
        Dictionary<SymbolKey, int> stubIndexes = [];

        int? Resolve(SymbolKey key)
        {
            if (keys.TryGetValue(key, out int index))
                return index;

            if (patchFiles is null)
                return null;

            if (stubIndexes.TryGetValue(key, out index))
                return index;

            if (!stubInfos.TryGetValue(key, out StubSymbol? stub))
                return null;

            index = total + stubs.Count;
            stubIndexes[key] = index;
            stubs.Add(new GraphSymbol(key.Project, null, stub.Kind, stub.Name, stub.FullName, key.DocId, stub.Signature, stub.Namespace, stub.Accessibility, [], null));
            return index;
        }

        List<GraphSymbol> symbols = new(total);
        List<GraphDeclaration> declarations = [];
        for (int i = 0; i < units.Count; i++)
        {
            List<LocalSymbol> locals = results[i].Symbols;
            for (int j = 0; j < locals.Count; j++)
            {
                LocalSymbol local = locals[j];
                int global = offsets[units[i].Index] + j;
                int? parent = local.Parent is { } parentKey && Resolve(parentKey) is { } parentIndex && parentIndex != global ? parentIndex : null;
                symbols.Add(new GraphSymbol(units[i].Index, parent, local.Kind, local.Name, local.FullName, local.DocId, local.Signature, local.Namespace, local.Accessibility, ProjectAnalyzer.ModifierList(local), local.Summary));
                foreach (LocalDeclaration declaration in local.Declarations.OrderBy(d => d.Path, StringComparer.Ordinal).ThenBy(d => d.StartLine).ThenBy(d => d.NameColumn))
                    declarations.Add(new GraphDeclaration(global, declaration.Path, declaration.StartLine, declaration.EndLine, declaration.NameLine, declaration.NameColumn));
            }
        }

        int TypeOf(int symbol) => CodeSymbolKinds.IsType(symbols[symbol].Kind) ? symbol : symbols[symbol].Parent ?? -1;

        List<GraphReference> references = [];
        Dictionary<(int From, int To, string Kind, string Path), int> typeEdges = [];
        Dictionary<(int From, string Target, string Assembly, string Kind, string Path), int> externalEdges = [];
        HashSet<GraphMemberLink> links = [];
        for (int i = 0; i < units.Count; i++)
        {
            int offset = offsets[units[i].Index];
            ProjectResult result = results[i];
            foreach (RawReference raw in result.References)
            {
                if (Resolve(raw.Target) is not { } target)
                    continue;

                int? from = raw.From >= 0 ? offset + raw.From : null;
                references.Add(new GraphReference(target, raw.Path, raw.Line, raw.Column, raw.Length, raw.Kind, from));
                if (from is not { } fromSymbol || raw.TypeKey is not { } typeKey || Resolve(typeKey) is not { } toType)
                    continue;

                int fromType = TypeOf(fromSymbol);
                if (fromType >= 0 && fromType != toType)
                {
                    (int, int, string, string) edge = (fromType, toType, raw.Kind, raw.Path);
                    typeEdges[edge] = typeEdges.GetValueOrDefault(edge) + 1;
                }
            }

            foreach ((ExternalUse use, int count) in result.External)
            {
                int fromType = TypeOf(offset + use.From);
                if (fromType < 0)
                    continue;

                (int, string, string, string, string) edge = (fromType, use.Target, use.Assembly, use.Kind, use.Path);
                externalEdges[edge] = externalEdges.GetValueOrDefault(edge) + count;
            }

            foreach (RawLink link in result.Links)
            {
                if (Resolve(link.Target) is { } target)
                    links.Add(new GraphMemberLink(offset + link.Member, target, link.Kind, link.Path));
            }
        }

        symbols.AddRange(stubs);
        return new AssembledGraph(
            symbols,
            declarations,
            references,
            [.. typeEdges.OrderBy(pair => pair.Key.From).ThenBy(pair => pair.Key.To).ThenBy(pair => pair.Key.Kind, StringComparer.Ordinal).ThenBy(pair => pair.Key.Path, StringComparer.Ordinal).Select(pair => new GraphTypeEdge(pair.Key.From, pair.Key.To, pair.Key.Kind, pair.Value, pair.Key.Path))],
            [.. externalEdges.OrderBy(pair => pair.Key.From).ThenBy(pair => pair.Key.Target, StringComparer.Ordinal).ThenBy(pair => pair.Key.Assembly, StringComparer.Ordinal).ThenBy(pair => pair.Key.Kind, StringComparer.Ordinal).ThenBy(pair => pair.Key.Path, StringComparer.Ordinal).Select(pair => new GraphExternalEdge(pair.Key.From, pair.Key.Target, pair.Key.Assembly, pair.Key.Kind, pair.Value, pair.Key.Path))],
            [.. links.OrderBy(link => link.Member).ThenBy(link => link.Target).ThenBy(link => link.Kind, StringComparer.Ordinal)],
            [.. Enumerable.Range(total, stubs.Count)]);
    }
}
