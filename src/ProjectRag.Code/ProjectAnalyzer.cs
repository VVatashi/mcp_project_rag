using System.Collections.Immutable;
using System.Globalization;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

using ProjectRag.Core.Code.Graph;

namespace ProjectRag.Code;

/// <summary>
/// Reads the symbols, the references and the links of the files that one project owns.
/// </summary>
/// <param name="unit">The project with the files to read.</param>
/// <param name="catalog">The known files.</param>
/// <param name="owners">The owner project of each file.</param>
/// <param name="maxErrors">The most compiler errors to keep.</param>
/// <param name="patchFiles">The files of an update, or <see langword="null"/> for a full build. An update also collects the stubs and keeps only the links of these files.</param>
internal sealed class ProjectAnalyzer(ProjectUnit unit, FileCatalog catalog, IReadOnlyDictionary<string, int> owners, int maxErrors, IReadOnlySet<string>? patchFiles = null)
{
    private const string OverrideKind = "override";

    private static readonly string[] ModifierOrder = ["static", "abstract", "sealed", "virtual", "override", "readonly", "async", "partial", "extern", "new", "const", "required"];

    private static readonly SymbolDisplayFormat ExternalFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters);

    private readonly Dictionary<ISymbol, TargetInfo> _targets = new(SymbolEqualityComparer.Default);
    private readonly Dictionary<string, int> _byDocId = new(StringComparer.Ordinal);
    private readonly ProjectResult _result = new();

    public async Task<ProjectResult> AnalyzeAsync(bool readErrors, CancellationToken cancellationToken)
    {
        Compilation? compilation = await unit.Project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
        if (compilation is not CSharpCompilation)
        {
            _result.Diagnostics.Add(new GraphDiagnostic(unit.Index, "warning", string.Empty, "The project has no C# compilation.", null, null));
            return _result;
        }

        foreach (OwnedFile file in unit.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SyntaxTree? tree = await file.Document.GetSyntaxTreeAsync(cancellationToken).ConfigureAwait(false);
            if (tree is null || !compilation.ContainsSyntaxTree(tree))
                continue;

            SemanticModel model = compilation.GetSemanticModel(tree);
            SourceText text = await tree.GetTextAsync(cancellationToken).ConfigureAwait(false);
            SyntaxNode root = await tree.GetRootAsync(cancellationToken).ConfigureAwait(false);
            WalkFile(root, model, text, file.Path, cancellationToken);
        }

        AddMemberLinks(cancellationToken);
        if (patchFiles is not null)
            AddModifiersOfOtherParts(cancellationToken);

        foreach (LocalSymbol symbol in _result.Symbols)
            symbol.Symbol = null;

        if (readErrors)
            ReadErrors(compilation, cancellationToken);

        return _result;
    }

    private static IEnumerable<string> OrderedModifiers(HashSet<string> modifiers) => ModifierOrder.Where(modifiers.Contains);

    public static IReadOnlyList<string> ModifierList(LocalSymbol symbol) => [.. OrderedModifiers(symbol.Modifiers)];

    private static ISymbol? Normalize(ISymbol? symbol)
    {
        switch (symbol)
        {
            case IMethodSymbol method:
                if (method.MethodKind is MethodKind.LocalFunction or MethodKind.AnonymousFunction or MethodKind.BuiltinOperator or MethodKind.FunctionPointerSignature)
                    return null;

                return (method.ReducedFrom ?? method).OriginalDefinition;
            case INamedTypeSymbol type:
                return type.TypeKind is TypeKind.Error or TypeKind.Dynamic || type.IsAnonymousType ? null : type.OriginalDefinition;
            case IFieldSymbol or IPropertySymbol or IEventSymbol:
                return symbol.OriginalDefinition;
            default:
                return null;
        }
    }

    private static SyntaxNode TopName(SyntaxNode name)
    {
        SyntaxNode current = name;
        while (true)
        {
            switch (current.Parent)
            {
                case QualifiedNameSyntax qualified when qualified.Right == current:
                    current = qualified;
                    continue;
                case AliasQualifiedNameSyntax aliased when aliased.Name == current:
                    current = aliased;
                    continue;
                default:
                    return current;
            }
        }
    }

    private static string BodyKind(ISymbol target) => target switch
    {
        IMethodSymbol => CodeUseKinds.Call,
        INamedTypeSymbol => CodeUseKinds.Type,
        _ => CodeUseKinds.Member,
    };

    private static string ClassifyContext(SyntaxNode name, ISymbol target)
    {
        for (SyntaxNode? ancestor = name.Parent; ancestor is not null; ancestor = ancestor.Parent)
        {
            switch (ancestor)
            {
                case BaseTypeSyntax:
                    return CodeUseKinds.Type;
                case BlockSyntax or ArrowExpressionClauseSyntax or EqualsValueClauseSyntax or ArgumentListSyntax or AccessorListSyntax:
                    return BodyKind(target);
                case MemberDeclarationSyntax:
                    return CodeUseKinds.Signature;
            }
        }

        return CodeUseKinds.Signature;
    }

    private static bool IsDocumentationComment(SyntaxTrivia trivia) =>
        trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia)
        || trivia.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia)
        || (trivia.IsKind(SyntaxKind.SingleLineCommentTrivia) && trivia.ToString().StartsWith("///", StringComparison.Ordinal))
        || (trivia.IsKind(SyntaxKind.MultiLineCommentTrivia) && trivia.ToString().StartsWith("/**", StringComparison.Ordinal));

    private static (int Start, int End) LineRange(SyntaxNode node, SourceText text)
    {
        int start = node.SpanStart;
        foreach (SyntaxTrivia trivia in node.GetLeadingTrivia())
        {
            if (IsDocumentationComment(trivia))
            {
                start = trivia.SpanStart;
                break;
            }
        }

        return (text.Lines.GetLinePosition(start).Line + 1, text.Lines.GetLinePosition(node.Span.End).Line + 1);
    }

    private void WalkFile(SyntaxNode root, SemanticModel model, SourceText text, string path, CancellationToken cancellationToken)
    {
        Dictionary<SyntaxNode, int> declared = [];
        foreach (SyntaxNode node in root.DescendantNodes())
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (node)
            {
                case IdentifierNameSyntax or GenericNameSyntax:
                    AddName((SimpleNameSyntax)node, model, text, path, declared, cancellationToken);
                    break;
                case ImplicitObjectCreationExpressionSyntax creation:
                    AddImplicitCreation(creation, model, text, path, declared, cancellationToken);
                    break;
                case ConstructorInitializerSyntax initializer:
                    AddInitializer(initializer, model, text, path, declared, cancellationToken);
                    break;
                default:
                    Declare(node, model, text, path, declared, cancellationToken);
                    break;
            }
        }
    }

    private void Declare(SyntaxNode node, SemanticModel model, SourceText text, string path, Dictionary<SyntaxNode, int> declared, CancellationToken cancellationToken)
    {
        switch (node)
        {
            case ClassDeclarationSyntax or StructDeclarationSyntax or InterfaceDeclarationSyntax or RecordDeclarationSyntax:
                {
                    TypeDeclarationSyntax type = (TypeDeclarationSyntax)node;
                    string kind = node.Kind() switch
                    {
                        SyntaxKind.ClassDeclaration => CodeSymbolKinds.Class,
                        SyntaxKind.StructDeclaration => CodeSymbolKinds.Struct,
                        SyntaxKind.InterfaceDeclaration => CodeSymbolKinds.Interface,
                        SyntaxKind.RecordStructDeclaration => CodeSymbolKinds.RecordStruct,
                        _ => CodeSymbolKinds.Record,
                    };

                    AddDeclaration(node, node, model.GetDeclaredSymbol(type, cancellationToken), kind, type.Identifier, type.Modifiers, null, text, path, declared, cancellationToken);
                    break;
                }

            case EnumDeclarationSyntax enumeration:
                AddDeclaration(node, node, model.GetDeclaredSymbol(enumeration, cancellationToken), CodeSymbolKinds.Enum, enumeration.Identifier, enumeration.Modifiers, null, text, path, declared, cancellationToken);
                break;
            case DelegateDeclarationSyntax delegateDeclaration:
                AddDeclaration(node, node, model.GetDeclaredSymbol(delegateDeclaration, cancellationToken), CodeSymbolKinds.Delegate, delegateDeclaration.Identifier, delegateDeclaration.Modifiers, null, text, path, declared, cancellationToken);
                break;
            case MethodDeclarationSyntax method:
                AddDeclaration(node, node, model.GetDeclaredSymbol(method, cancellationToken), CodeSymbolKinds.Method, method.Identifier, method.Modifiers, null, text, path, declared, cancellationToken);
                break;
            case ConstructorDeclarationSyntax constructor:
                AddDeclaration(node, node, model.GetDeclaredSymbol(constructor, cancellationToken), CodeSymbolKinds.Constructor, constructor.Identifier, constructor.Modifiers, null, text, path, declared, cancellationToken);
                break;
            case DestructorDeclarationSyntax destructor:
                AddDeclaration(node, node, model.GetDeclaredSymbol(destructor, cancellationToken), CodeSymbolKinds.Destructor, destructor.Identifier, destructor.Modifiers, null, text, path, declared, cancellationToken);
                break;
            case OperatorDeclarationSyntax operatorDeclaration:
                AddDeclaration(node, node, model.GetDeclaredSymbol(operatorDeclaration, cancellationToken), CodeSymbolKinds.Operator, operatorDeclaration.OperatorToken, operatorDeclaration.Modifiers, "operator " + operatorDeclaration.OperatorToken.Text, text, path, declared, cancellationToken);
                break;
            case ConversionOperatorDeclarationSyntax conversion:
                AddDeclaration(node, node, model.GetDeclaredSymbol(conversion, cancellationToken), CodeSymbolKinds.Conversion, conversion.OperatorKeyword, conversion.Modifiers, conversion.ImplicitOrExplicitKeyword.Text + " operator " + conversion.Type, text, path, declared, cancellationToken);
                break;
            case PropertyDeclarationSyntax property:
                AddDeclaration(node, node, model.GetDeclaredSymbol(property, cancellationToken), CodeSymbolKinds.Property, property.Identifier, property.Modifiers, null, text, path, declared, cancellationToken);
                break;
            case IndexerDeclarationSyntax indexer:
                AddDeclaration(node, node, model.GetDeclaredSymbol(indexer, cancellationToken), CodeSymbolKinds.Indexer, indexer.ThisKeyword, indexer.Modifiers, "this[]", text, path, declared, cancellationToken);
                break;
            case EventDeclarationSyntax eventDeclaration:
                AddDeclaration(node, node, model.GetDeclaredSymbol(eventDeclaration, cancellationToken), CodeSymbolKinds.Event, eventDeclaration.Identifier, eventDeclaration.Modifiers, null, text, path, declared, cancellationToken);
                break;
            case EnumMemberDeclarationSyntax member:
                AddDeclaration(node, node, model.GetDeclaredSymbol(member, cancellationToken), CodeSymbolKinds.EnumMember, member.Identifier, default, null, text, path, declared, cancellationToken);
                break;
            case FieldDeclarationSyntax field:
                AddVariables(field, field.Declaration, field.Modifiers, isEvent: false, model, text, path, declared, cancellationToken);
                break;
            case EventFieldDeclarationSyntax eventField:
                AddVariables(eventField, eventField.Declaration, eventField.Modifiers, isEvent: true, model, text, path, declared, cancellationToken);
                break;
        }
    }

    private void AddVariables(MemberDeclarationSyntax member, VariableDeclarationSyntax declaration, SyntaxTokenList modifiers, bool isEvent, SemanticModel model, SourceText text, string path, Dictionary<SyntaxNode, int> declared, CancellationToken cancellationToken)
    {
        bool first = true;
        foreach (VariableDeclaratorSyntax variable in declaration.Variables)
        {
            ISymbol? symbol = model.GetDeclaredSymbol(variable, cancellationToken);
            string kind = isEvent ? CodeSymbolKinds.Event : symbol is IFieldSymbol { IsConst: true } ? CodeSymbolKinds.Constant : CodeSymbolKinds.Field;
            int index = AddDeclaration(variable, first ? member : variable, symbol, kind, variable.Identifier, modifiers, null, text, path, declared, cancellationToken);
            if (first && index >= 0)
                declared[member] = index;

            first = false;
        }
    }

    private int AddDeclaration(SyntaxNode lookup, SyntaxNode span, ISymbol? symbol, string kind, SyntaxToken nameToken, SyntaxTokenList modifiers, string? displayName, SourceText text, string path, Dictionary<SyntaxNode, int> declared, CancellationToken cancellationToken)
    {
        if (symbol is null || symbol.IsImplicitlyDeclared || symbol.GetDocumentationCommentId() is not { } docId)
            return -1;

        if (!_byDocId.TryGetValue(docId, out int index))
        {
            string name = displayName ?? (kind is CodeSymbolKinds.Constructor or CodeSymbolKinds.Destructor ? symbol.ContainingType.Name : nameToken.ValueText);
            index = _result.Symbols.Count;
            _byDocId[docId] = index;
            _result.Symbols.Add(new LocalSymbol
            {
                DocId = docId,
                Kind = kind,
                Name = name,
                FullName = FullName(symbol, name),
                Signature = SymbolDescriber.Signature(symbol),
                Namespace = symbol.ContainingNamespace is { IsGlobalNamespace: false } containing ? containing.ToDisplayString() : string.Empty,
                Accessibility = SymbolDescriber.Accessibility(symbol.DeclaredAccessibility),
                Summary = SymbolDescriber.Summary(symbol, cancellationToken),
                Parent = symbol.ContainingType is { } parent ? TargetFor(parent.OriginalDefinition).Key : null,
                Symbol = symbol,
            });
        }

        LocalSymbol local = _result.Symbols[index];
        foreach (SyntaxToken modifier in modifiers)
        {
            if (ModifierOrder.Contains(modifier.ValueText, StringComparer.Ordinal))
                local.Modifiers.Add(modifier.ValueText);
        }

        (int startLine, int endLine) = LineRange(span, text);
        LinePosition position = text.Lines.GetLinePosition(nameToken.SpanStart);
        local.Declarations.Add(new LocalDeclaration(path, startLine, endLine, position.Line + 1, position.Character + 1));
        declared[lookup] = index;
        return index;
    }

    private static string FullName(ISymbol symbol, string name)
    {
        List<string> parts = [name];
        for (INamedTypeSymbol? type = symbol.ContainingType; type is not null; type = type.ContainingType)
            parts.Add(type.Name);

        if (symbol.ContainingNamespace is { IsGlobalNamespace: false } containing)
            parts.Add(containing.ToDisplayString());

        parts.Reverse();
        return string.Join('.', parts);
    }

    private void AddName(SimpleNameSyntax name, SemanticModel model, SourceText text, string path, Dictionary<SyntaxNode, int> declared, CancellationToken cancellationToken)
    {
        if (name is IdentifierNameSyntax { IsVar: true })
            return;

        SymbolInfo info = model.GetSymbolInfo(name, cancellationToken);
        ISymbol? target = Normalize(info.Symbol ?? (info.CandidateSymbols.Length == 1 ? info.CandidateSymbols[0] : null));
        if (target is null)
            return;

        SyntaxNode top = TopName(name);
        string kind;
        ObjectCreationExpressionSyntax? creation = null;
        if (top.Parent is ObjectCreationExpressionSyntax created && created.Type == top)
        {
            kind = CodeUseKinds.Create;
            creation = created;
        }
        else if (top.Parent is AttributeSyntax attribute && attribute.Name == top)
        {
            kind = CodeUseKinds.Attribute;
            if (target is IMethodSymbol { ContainingType: { } attributeType })
                target = attributeType.OriginalDefinition;
        }
        else if (top.Parent is BaseTypeSyntax baseType && baseType.Type == top)
        {
            kind = target is INamedTypeSymbol { TypeKind: TypeKind.Interface } ? CodeUseKinds.Implement
                : target is INamedTypeSymbol { TypeKind: TypeKind.Class } ? CodeUseKinds.Inherit
                : CodeUseKinds.Type;
        }
        else
        {
            kind = ClassifyContext(name, target);
        }

        int from = FromOf(name, declared);
        Emit(target, kind, name.Identifier, from, text, path, sourceOnly: false);

        if (creation is not null)
        {
            SymbolInfo constructorInfo = model.GetSymbolInfo(creation, cancellationToken);
            if (Normalize(constructorInfo.Symbol ?? (constructorInfo.CandidateSymbols.Length == 1 ? constructorInfo.CandidateSymbols[0] : null)) is { } constructor)
                Emit(constructor, CodeUseKinds.Call, name.Identifier, from, text, path, sourceOnly: true);
        }
    }

    private void AddImplicitCreation(ImplicitObjectCreationExpressionSyntax creation, SemanticModel model, SourceText text, string path, Dictionary<SyntaxNode, int> declared, CancellationToken cancellationToken)
    {
        SymbolInfo info = model.GetSymbolInfo(creation, cancellationToken);
        ISymbol? constructor = Normalize(info.Symbol ?? (info.CandidateSymbols.Length == 1 ? info.CandidateSymbols[0] : null));
        ISymbol? type = Normalize(model.GetTypeInfo(creation, cancellationToken).Type ?? (constructor as IMethodSymbol)?.ContainingType);
        int from = FromOf(creation, declared);
        if (type is not null)
            Emit(type, CodeUseKinds.Create, creation.NewKeyword, from, text, path, sourceOnly: false);

        if (constructor is not null)
            Emit(constructor, CodeUseKinds.Call, creation.NewKeyword, from, text, path, sourceOnly: true);
    }

    private void AddInitializer(ConstructorInitializerSyntax initializer, SemanticModel model, SourceText text, string path, Dictionary<SyntaxNode, int> declared, CancellationToken cancellationToken)
    {
        SymbolInfo info = model.GetSymbolInfo(initializer, cancellationToken);
        if (Normalize(info.Symbol ?? (info.CandidateSymbols.Length == 1 ? info.CandidateSymbols[0] : null)) is { } constructor)
            Emit(constructor, CodeUseKinds.Call, initializer.ThisOrBaseKeyword, FromOf(initializer, declared), text, path, sourceOnly: false);
    }

    private static int FromOf(SyntaxNode node, Dictionary<SyntaxNode, int> declared)
    {
        for (SyntaxNode? ancestor = node.Parent; ancestor is not null; ancestor = ancestor.Parent)
        {
            if (declared.TryGetValue(ancestor, out int index))
                return index;
        }

        return -1;
    }

    private void Emit(ISymbol target, string kind, SyntaxToken token, int from, SourceText text, string path, bool sourceOnly)
    {
        TargetInfo info = TargetFor(target);
        if (info.IsSkipped)
            return;

        if (info.ExternalName is not null)
        {
            if (!sourceOnly && from >= 0)
            {
                ExternalUse use = new(from, info.ExternalName, info.ExternalAssembly ?? string.Empty, kind, path);
                _result.External[use] = _result.External.GetValueOrDefault(use) + 1;
            }

            return;
        }

        if (info.Key is not { } key)
            return;

        LinePosition position = text.Lines.GetLinePosition(token.SpanStart);
        _result.References.Add(new RawReference(key, info.TypeKey, path, position.Line + 1, position.Character + 1, token.Span.Length, kind, from));
    }

    private TargetInfo TargetFor(ISymbol symbol)
    {
        if (!_targets.TryGetValue(symbol, out TargetInfo? info))
        {
            info = ComputeTarget(symbol);
            _targets[symbol] = info;
        }

        return info;
    }

    private TargetInfo ComputeTarget(ISymbol symbol)
    {
        INamedTypeSymbol? type = (symbol as INamedTypeSymbol ?? symbol.ContainingType)?.OriginalDefinition;
        if (symbol.Locations.Any(location => location.IsInSource))
        {
            SymbolKey? key = KeyOf(symbol, out string? keyPath);
            string? typePath = keyPath;
            SymbolKey? typeKey = type is null ? null : ReferenceEquals(type, symbol) ? key : KeyOf(type, out typePath);
            if (patchFiles is not null)
            {
                AddStub(key, keyPath, symbol);
                if (type is not null && !ReferenceEquals(type, symbol))
                    AddStub(typeKey, typePath, type);
            }

            return key is null && typeKey is null ? TargetInfo.Skipped : new TargetInfo { Key = key, TypeKey = typeKey };
        }

        if (type is null || type.SpecialType != SpecialType.None || type.TypeKind is TypeKind.Error or TypeKind.Dynamic || type.IsAnonymousType)
            return TargetInfo.Skipped;

        return new TargetInfo { ExternalName = type.ToDisplayString(ExternalFormat), ExternalAssembly = type.ContainingAssembly?.Name ?? string.Empty };
    }

    private SymbolKey? KeyOf(ISymbol symbol, out string? path)
    {
        Location? source = symbol.Locations.FirstOrDefault(location => location.IsInSource);
        path = catalog.Resolve(source?.SourceTree?.FilePath);
        if (path is null || !owners.TryGetValue(path, out int owner))
            return null;

        return symbol.GetDocumentationCommentId() is { } docId ? new SymbolKey(owner, docId) : null;
    }

    private void AddStub(SymbolKey? key, string? path, ISymbol symbol)
    {
        if (key is not { } stubKey || path is null || patchFiles!.Contains(path) || _result.Stubs.ContainsKey(stubKey) || !IsDeclaredInGraph(stubKey, symbol))
            return;

        string name = symbol is IMethodSymbol { MethodKind: MethodKind.Constructor or MethodKind.StaticConstructor or MethodKind.Destructor } ? symbol.ContainingType.Name : symbol.Name;
        _result.Stubs[stubKey] = new StubSymbol(
            KindOf(symbol),
            name,
            FullName(symbol, name),
            SymbolDescriber.Signature(symbol),
            symbol.ContainingNamespace is { IsGlobalNamespace: false } containing ? containing.ToDisplayString() : string.Empty,
            SymbolDescriber.Accessibility(symbol.DeclaredAccessibility),
            path);
    }

    /// <summary>
    /// Tells whether a full build gives the symbol a row: a walked node of a file that the project of the key owns declares it.
    /// An implicit constructor or a primary constructor has a key but no row.
    /// </summary>
    private bool IsDeclaredInGraph(SymbolKey key, ISymbol symbol)
    {
        if (symbol.IsImplicitlyDeclared)
            return false;

        foreach (SyntaxReference reference in symbol.DeclaringSyntaxReferences)
        {
            string? file = catalog.Resolve(reference.SyntaxTree.FilePath);
            if (file is null || !owners.TryGetValue(file, out int owner) || owner != key.Project)
                continue;

            bool declares = reference.GetSyntax() switch
            {
                ClassDeclarationSyntax or StructDeclarationSyntax or InterfaceDeclarationSyntax or RecordDeclarationSyntax or EnumDeclarationSyntax or DelegateDeclarationSyntax => symbol is INamedTypeSymbol,
                MethodDeclarationSyntax or ConstructorDeclarationSyntax or DestructorDeclarationSyntax or OperatorDeclarationSyntax or ConversionOperatorDeclarationSyntax => symbol is IMethodSymbol,
                PropertyDeclarationSyntax or IndexerDeclarationSyntax => symbol is IPropertySymbol,
                EventDeclarationSyntax => symbol is IEventSymbol,
                EnumMemberDeclarationSyntax => symbol is IFieldSymbol,
                VariableDeclaratorSyntax { Parent.Parent: FieldDeclarationSyntax } => symbol is IFieldSymbol,
                VariableDeclaratorSyntax { Parent.Parent: EventFieldDeclarationSyntax } => symbol is IEventSymbol,
                _ => false,
            };

            if (declares)
                return true;
        }

        return false;
    }

    private static string KindOf(ISymbol symbol) => symbol switch
    {
        INamedTypeSymbol { TypeKind: TypeKind.Interface } => CodeSymbolKinds.Interface,
        INamedTypeSymbol { TypeKind: TypeKind.Enum } => CodeSymbolKinds.Enum,
        INamedTypeSymbol { TypeKind: TypeKind.Delegate } => CodeSymbolKinds.Delegate,
        INamedTypeSymbol { TypeKind: TypeKind.Struct, IsRecord: true } => CodeSymbolKinds.RecordStruct,
        INamedTypeSymbol { TypeKind: TypeKind.Struct } => CodeSymbolKinds.Struct,
        INamedTypeSymbol { IsRecord: true } => CodeSymbolKinds.Record,
        INamedTypeSymbol => CodeSymbolKinds.Class,
        IMethodSymbol { MethodKind: MethodKind.Constructor or MethodKind.StaticConstructor } => CodeSymbolKinds.Constructor,
        IMethodSymbol { MethodKind: MethodKind.Destructor } => CodeSymbolKinds.Destructor,
        IMethodSymbol { MethodKind: MethodKind.UserDefinedOperator } => CodeSymbolKinds.Operator,
        IMethodSymbol { MethodKind: MethodKind.Conversion } => CodeSymbolKinds.Conversion,
        IMethodSymbol => CodeSymbolKinds.Method,
        IPropertySymbol { IsIndexer: true } => CodeSymbolKinds.Indexer,
        IPropertySymbol => CodeSymbolKinds.Property,
        IFieldSymbol { ContainingType.TypeKind: TypeKind.Enum } => CodeSymbolKinds.EnumMember,
        IFieldSymbol { IsConst: true } => CodeSymbolKinds.Constant,
        IFieldSymbol => CodeSymbolKinds.Field,
        IEventSymbol => CodeSymbolKinds.Event,
        _ => CodeSymbolKinds.Class,
    };

    private string? FirstDeclarationPath(ISymbol symbol)
    {
        string? first = null;
        foreach (SyntaxReference reference in symbol.DeclaringSyntaxReferences)
        {
            string? file = catalog.Resolve(reference.SyntaxTree.FilePath);
            if (file is null || !owners.TryGetValue(file, out int owner) || owner != unit.Index)
                continue;

            if (first is null || string.CompareOrdinal(file, first) < 0)
                first = file;
        }

        return first;
    }

    private void AddModifiersOfOtherParts(CancellationToken cancellationToken)
    {
        foreach (LocalSymbol local in _result.Symbols)
        {
            ImmutableArray<SyntaxReference> references = local.Symbol!.DeclaringSyntaxReferences;
            if (references.Length < 2)
                continue;

            foreach (SyntaxReference reference in references)
            {
                string? file = catalog.Resolve(reference.SyntaxTree.FilePath);
                if (file is null || patchFiles!.Contains(file) || !owners.TryGetValue(file, out int owner) || owner != unit.Index)
                    continue;

                foreach (SyntaxToken modifier in ModifiersOf(reference.GetSyntax(cancellationToken)))
                {
                    if (ModifierOrder.Contains(modifier.ValueText, StringComparer.Ordinal))
                        local.Modifiers.Add(modifier.ValueText);
                }
            }
        }
    }

    private static SyntaxTokenList ModifiersOf(SyntaxNode node) => node switch
    {
        BaseTypeDeclarationSyntax type => type.Modifiers,
        DelegateDeclarationSyntax declaration => declaration.Modifiers,
        BaseMethodDeclarationSyntax method => method.Modifiers,
        BasePropertyDeclarationSyntax property => property.Modifiers,
        VariableDeclaratorSyntax { Parent.Parent: BaseFieldDeclarationSyntax field } => field.Modifiers,
        _ => default,
    };

    private void AddMemberLinks(CancellationToken cancellationToken)
    {
        for (int index = 0; index < _result.Symbols.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (_result.Symbols[index].Symbol)
            {
                case IMethodSymbol { OverriddenMethod: { } overridden }:
                    AddLink(index, overridden, OverrideKind);
                    break;
                case IPropertySymbol { OverriddenProperty: { } overridden }:
                    AddLink(index, overridden, OverrideKind);
                    break;
                case IEventSymbol { OverriddenEvent: { } overridden }:
                    AddLink(index, overridden, OverrideKind);
                    break;
                case INamedTypeSymbol type:
                    AddImplementations(type);
                    break;
            }
        }
    }

    private void AddLink(int member, ISymbol target, string kind)
    {
        if (TargetFor(target.OriginalDefinition).Key is not { } key || _result.Symbols[member].Symbol is not { } symbol || FirstDeclarationPath(symbol) is not { } path)
            return;

        if (patchFiles is null || patchFiles.Contains(path))
            _result.Links.Add(new RawLink(member, key, kind, path));
    }

    private void AddImplementations(INamedTypeSymbol type)
    {
        foreach (INamedTypeSymbol contract in type.AllInterfaces)
        {
            if (TargetFor(contract.OriginalDefinition).TypeKey is null || contract.Locations.All(location => !location.IsInSource))
                continue;

            foreach (ISymbol member in contract.GetMembers())
            {
                if (member is not (IMethodSymbol { MethodKind: MethodKind.Ordinary } or IPropertySymbol or IEventSymbol))
                    continue;

                ISymbol? implementation = type.FindImplementationForInterfaceMember(member)?.OriginalDefinition;
                if (implementation is null || !SymbolEqualityComparer.Default.Equals(implementation.ContainingType?.OriginalDefinition, type.OriginalDefinition))
                    continue;

                if (implementation.GetDocumentationCommentId() is { } docId && _byDocId.TryGetValue(docId, out int memberIndex))
                    AddLink(memberIndex, member, CodeUseKinds.Implement);
            }
        }
    }

    private void ReadErrors(Compilation compilation, CancellationToken cancellationToken)
    {
        List<(string? Path, int? Line, string Id, string Message)> errors = [];
        foreach (Diagnostic diagnostic in compilation.GetDiagnostics(cancellationToken))
        {
            if (diagnostic.Severity != DiagnosticSeverity.Error)
                continue;

            string? path = diagnostic.Location.IsInSource ? catalog.Resolve(diagnostic.Location.SourceTree?.FilePath) : null;
            int? line = diagnostic.Location.IsInSource ? diagnostic.Location.GetLineSpan().StartLinePosition.Line + 1 : null;
            errors.Add((path, line, diagnostic.Id, diagnostic.GetMessage(CultureInfo.InvariantCulture)));
        }

        _result.ErrorCount = errors.Count;
        IEnumerable<(string? Path, int? Line, string Id, string Message)> kept = errors
            .OrderBy(error => error.Path, StringComparer.Ordinal)
            .ThenBy(error => error.Line)
            .ThenBy(error => error.Id, StringComparer.Ordinal)
            .ThenBy(error => error.Message, StringComparer.Ordinal)
            .Take(maxErrors);

        foreach ((string? path, int? line, string id, string message) in kept)
            _result.Diagnostics.Add(new GraphDiagnostic(unit.Index, "error", id, message, path, line));
    }
}
