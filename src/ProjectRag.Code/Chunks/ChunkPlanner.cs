using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ProjectRag.Code.Chunks;

/// <summary>
/// Reads the syntax tree of a C# file and plans the ranges of its chunks.
/// </summary>
/// <param name="lines">The lines of the text.</param>
/// <param name="maxChars">The most characters of a chunk.</param>
/// <param name="minChars">The size under which neighbor ranges merge.</param>
internal sealed class ChunkPlanner(LineIndex lines, int maxChars, int minChars)
{
    private const string TopLevelHeading = "(top level)";
    private const int HeaderLimit = 200;

    private readonly List<ChunkUnit> _units = [];
    private int _nextGroup;
    private int _runFirst = -1;
    private int _runLast = -1;

    /// <summary>
    /// Plans the units of a file in the order of the text.
    /// </summary>
    /// <param name="root">The root of the syntax tree.</param>
    /// <returns>The units, merged when they are small.</returns>
    public List<ChunkUnit> Plan(CompilationUnitSyntax root)
    {
        foreach (AttributeListSyntax attributes in root.AttributeLists)
            AddToRun(attributes);

        WalkNamespace(root.Members, string.Empty);
        FlushRun();
        if (_units.Count == 0)
            PlanLines(root);

        return Merge();
    }

    private void WalkNamespace(SyntaxList<MemberDeclarationSyntax> members, string container)
    {
        int group = -1;
        foreach (MemberDeclarationSyntax member in members)
        {
            if (member is GlobalStatementSyntax)
            {
                AddToRun(member);
                continue;
            }

            FlushRun();
            if (member is BaseNamespaceDeclarationSyntax space)
            {
                WalkNamespace(space.Members, Join(container, HeadingText.Collapse(space.Name.ToString(), 1000)));
                group = -1;
                continue;
            }

            if (member is BaseTypeDeclarationSyntax large && !IsTiny(large))
            {
                WalkType(large, container);
                group = -1;
                continue;
            }

            if (group < 0)
                group = _nextGroup++;

            AddUnit(member is BaseTypeDeclarationSyntax tiny ? TypeUnit(tiny, container, group) : MemberUnit(member, container, group));
        }
    }

    private void WalkType(BaseTypeDeclarationSyntax type, string container)
    {
        string full = Join(container, TypeName(type));
        int first = StartLine(type);
        int last = EndLine(type);
        if (lines.Length(first, last) <= maxChars)
        {
            AddUnit(new ChunkUnit(full, first, last, _nextGroup++, ChunkUnitKind.Type));
            return;
        }

        int group = _nextGroup++;
        int headerLast = type.OpenBraceToken.IsMissing || type.OpenBraceToken.IsKind(SyntaxKind.None)
            ? first
            : Math.Min(last, lines.LineOf(type.OpenBraceToken.SpanStart));
        AddUnit(new ChunkUnit(full, first, headerLast, group, ChunkUnitKind.Header));

        IEnumerable<MemberDeclarationSyntax> members = type is EnumDeclarationSyntax enumeration
            ? enumeration.Members.Cast<MemberDeclarationSyntax>()
            : ((TypeDeclarationSyntax)type).Members;
        foreach (MemberDeclarationSyntax member in members)
        {
            if (member is BaseTypeDeclarationSyntax nested)
            {
                if (IsTiny(nested))
                {
                    AddUnit(TypeUnit(nested, full, group));
                }
                else
                {
                    WalkType(nested, full);
                    group = _nextGroup++;
                }

                continue;
            }

            AddUnit(MemberUnit(member, full, group));
        }
    }

    private ChunkUnit TypeUnit(BaseTypeDeclarationSyntax type, string container, int group) =>
        new(Join(container, TypeName(type)), StartLine(type), EndLine(type), group, ChunkUnitKind.Type);

    private ChunkUnit MemberUnit(MemberDeclarationSyntax member, string container, int group)
    {
        string heading = container.Length == 0 ? Header(member) : container + " > " + Header(member);
        return new ChunkUnit(heading, StartLine(member), EndLine(member), group, ChunkUnitKind.Member) { Node = member };
    }

    private void AddUnit(ChunkUnit unit)
    {
        Finish(unit);
        if (_units.Count > 0)
        {
            ChunkUnit last = _units[^1];
            if (unit.FirstLine <= last.LastLine)
            {
                if (unit.Group == last.Group)
                {
                    last.Absorb(unit, lines);
                    return;
                }

                unit.FirstLine = last.LastLine + 1;
                if (unit.FirstLine > unit.LastLine)
                    return;

                Finish(unit);
            }
        }

        _units.Add(unit);
    }

    private void Finish(ChunkUnit unit)
    {
        unit.Length = lines.Length(unit.FirstLine, unit.LastLine);
        unit.Small = unit.Length < minChars;
    }

    private void AddToRun(SyntaxNode node)
    {
        if (_runFirst < 0)
            _runFirst = StartLine(node);

        _runLast = EndLine(node);
    }

    private void FlushRun()
    {
        if (_runFirst >= 0)
            AddUnit(new ChunkUnit(TopLevelHeading, _runFirst, _runLast, _nextGroup++, ChunkUnitKind.TopLevel));

        _runFirst = -1;
        _runLast = -1;
    }

    private void PlanLines(CompilationUnitSyntax root)
    {
        HashSet<int> skipped = [];
        foreach (SyntaxNode node in root.DescendantNodes(static node => node is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax))
        {
            switch (node)
            {
                case UsingDirectiveSyntax or ExternAliasDirectiveSyntax:
                    Mark(skipped, node.SpanStart, node.Span.End);
                    break;
                case NamespaceDeclarationSyntax block:
                    Mark(skipped, block.NamespaceKeyword.SpanStart, block.Name.Span.End);
                    Mark(skipped, block.OpenBraceToken.SpanStart, block.OpenBraceToken.Span.End);
                    Mark(skipped, block.CloseBraceToken.SpanStart, block.CloseBraceToken.Span.End);
                    break;
                case FileScopedNamespaceDeclarationSyntax scoped:
                    Mark(skipped, scoped.NamespaceKeyword.SpanStart, scoped.Name.Span.End);
                    Mark(skipped, scoped.SemicolonToken.SpanStart, scoped.SemicolonToken.Span.End);
                    break;
            }
        }

        int line = 0;
        while (line < lines.Count)
        {
            if (skipped.Contains(line) || lines.IsBlank(line))
            {
                line++;
                continue;
            }

            int first = line;
            int last = line;
            while (line < lines.Count && !skipped.Contains(line))
            {
                if (!lines.IsBlank(line))
                    last = line;

                line++;
            }

            AddUnit(new ChunkUnit(TopLevelHeading, first, last, _nextGroup++, ChunkUnitKind.TopLevel));
        }
    }

    private void Mark(HashSet<int> skipped, int start, int end)
    {
        if (start >= lines.Text.Length)
            return;

        int last = lines.LineOf(Math.Max(start, end - 1));
        for (int line = lines.LineOf(start); line <= last; line++)
            skipped.Add(line);
    }

    private List<ChunkUnit> Merge()
    {
        List<ChunkUnit> merged = new(_units.Count);
        foreach (ChunkUnit unit in _units)
        {
            if (merged.Count > 0)
            {
                ChunkUnit last = merged[^1];
                if (unit.Small && (last.Small || last.Kind == ChunkUnitKind.Header) && CanJoin(last, unit))
                {
                    last.Absorb(unit, lines);
                    continue;
                }
            }

            merged.Add(unit);
        }

        for (int index = 0; index < merged.Count;)
        {
            ChunkUnit unit = merged[index];
            if (unit.Length >= minChars)
            {
                index++;
            }
            else if (index + 1 < merged.Count && CanJoin(unit, merged[index + 1]))
            {
                unit.Absorb(merged[index + 1], lines);
                merged.RemoveAt(index + 1);
            }
            else if (index > 0 && CanJoin(merged[index - 1], unit))
            {
                merged[index - 1].Absorb(unit, lines);
                merged.RemoveAt(index);
                index--;
            }
            else
            {
                index++;
            }
        }

        return merged;
    }

    private bool CanJoin(ChunkUnit first, ChunkUnit second) =>
        first.Group == second.Group && first.Kind != ChunkUnitKind.TopLevel && lines.Length(first.FirstLine, second.LastLine) <= maxChars;

    private bool IsTiny(BaseTypeDeclarationSyntax type) =>
        type.OpenBraceToken.IsKind(SyntaxKind.None)
        || type.OpenBraceToken.IsMissing
        || lines.LineOf(type.SpanStart) == lines.LineOf(Math.Max(type.SpanStart, type.Span.End - 1));

    private static string Join(string container, string name) => container.Length == 0 ? name : container + "." + name;

    private static string TypeName(BaseTypeDeclarationSyntax type)
    {
        string name = type.Identifier.Text;
        if (name.Length == 0)
            name = type.GetFirstToken().Text;

        return type is TypeDeclarationSyntax { TypeParameterList: { } parameters }
            ? name + HeadingText.Collapse(parameters.ToString(), HeaderLimit)
            : name;
    }

    private int StartLine(SyntaxNode node)
    {
        int start = node.SpanStart;
        SyntaxTriviaList trivia = node.GetLeadingTrivia();
        int lineEnds = 0;
        for (int index = trivia.Count - 1; index >= 0; index--)
        {
            SyntaxTrivia item = trivia[index];
            if (item.IsKind(SyntaxKind.WhitespaceTrivia))
                continue;

            if (item.IsKind(SyntaxKind.EndOfLineTrivia))
            {
                if (++lineEnds > 1)
                    break;

                continue;
            }

            if (!IsDocumentation(item))
                break;

            start = item.SpanStart;
            lineEnds = 0;
        }

        return lines.LineOf(start);
    }

    private int EndLine(SyntaxNode node) => lines.LineOf(Math.Max(node.SpanStart, node.Span.End - 1));

    private bool IsDocumentation(SyntaxTrivia trivia)
    {
        switch (trivia.Kind())
        {
            case SyntaxKind.SingleLineDocumentationCommentTrivia:
            case SyntaxKind.MultiLineDocumentationCommentTrivia:
                return true;
            case SyntaxKind.SingleLineCommentTrivia:
                return lines.Text.AsSpan(trivia.SpanStart, trivia.Span.Length).StartsWith("///", StringComparison.Ordinal);
            case SyntaxKind.MultiLineCommentTrivia:
                return trivia.Span.Length > 4 && lines.Text.AsSpan(trivia.SpanStart, trivia.Span.Length).StartsWith("/**", StringComparison.Ordinal);
            default:
                return false;
        }
    }

    private string Header(MemberDeclarationSyntax member)
    {
        int start = member.AttributeLists.Count > 0
            ? member.AttributeLists[^1].GetLastToken().GetNextToken().SpanStart
            : member.SpanStart;
        int end = member switch
        {
            MethodDeclarationSyntax { ConstraintClauses.Count: > 0 } method => method.ConstraintClauses[^1].Span.End,
            BaseMethodDeclarationSyntax method => method.ParameterList.Span.End,
            PropertyDeclarationSyntax property => property.Identifier.Span.End,
            IndexerDeclarationSyntax indexer => indexer.ParameterList.Span.End,
            EventDeclarationSyntax declaration => declaration.Identifier.Span.End,
            BaseFieldDeclarationSyntax { Declaration.Variables.Count: > 0 } field => field.Declaration.Variables[0].Identifier.Span.End,
            DelegateDeclarationSyntax { ConstraintClauses.Count: > 0 } delegateDeclaration => delegateDeclaration.ConstraintClauses[^1].Span.End,
            DelegateDeclarationSyntax delegateDeclaration => delegateDeclaration.ParameterList.Span.End,
            EnumMemberDeclarationSyntax enumMember => enumMember.Span.End,
            _ => -1,
        };

        if (end <= start || end > lines.Text.Length)
            end = lines.End(lines.LineOf(start));

        return HeadingText.Collapse(lines.Text.AsSpan(start, end - start), HeaderLimit);
    }
}
