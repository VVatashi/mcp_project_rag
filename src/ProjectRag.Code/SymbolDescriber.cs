using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

using Microsoft.CodeAnalysis;

namespace ProjectRag.Code;

/// <summary>
/// Writes the signature, the accessibility and the summary of a symbol.
/// </summary>
internal static partial class SymbolDescriber
{
    private const int MaxSummaryLength = 1000;

    private static readonly SymbolDisplayFormat SignatureFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameOnly,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters | SymbolDisplayGenericsOptions.IncludeVariance,
        memberOptions: SymbolDisplayMemberOptions.IncludeAccessibility
            | SymbolDisplayMemberOptions.IncludeModifiers
            | SymbolDisplayMemberOptions.IncludeType
            | SymbolDisplayMemberOptions.IncludeParameters
            | SymbolDisplayMemberOptions.IncludeExplicitInterface
            | SymbolDisplayMemberOptions.IncludeConstantValue
            | SymbolDisplayMemberOptions.IncludeRef,
        delegateStyle: SymbolDisplayDelegateStyle.NameAndSignature,
        extensionMethodStyle: SymbolDisplayExtensionMethodStyle.StaticMethod,
        parameterOptions: SymbolDisplayParameterOptions.IncludeExtensionThis
            | SymbolDisplayParameterOptions.IncludeModifiers
            | SymbolDisplayParameterOptions.IncludeType
            | SymbolDisplayParameterOptions.IncludeName
            | SymbolDisplayParameterOptions.IncludeDefaultValue,
        propertyStyle: SymbolDisplayPropertyStyle.ShowReadWriteDescriptor,
        kindOptions: SymbolDisplayKindOptions.IncludeTypeKeyword | SymbolDisplayKindOptions.IncludeMemberKeyword,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes
            | SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers
            | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    public static string Signature(ISymbol symbol)
    {
        if (symbol is IMethodSymbol { MethodKind: MethodKind.Destructor })
            return "~" + symbol.ContainingType.Name + "()";

        string display = symbol.ToDisplayString(SignatureFormat);
        if (symbol is not INamedTypeSymbol type)
            return display;

        string modifiers = type switch
        {
            { TypeKind: TypeKind.Class, IsStatic: true } => "static ",
            { TypeKind: TypeKind.Class, IsAbstract: true } => "abstract ",
            { TypeKind: TypeKind.Class, IsSealed: true } => "sealed ",
            _ => string.Empty,
        };

        return Accessibility(type.DeclaredAccessibility) + " " + modifiers + display;
    }

    public static string Accessibility(Accessibility accessibility) => accessibility switch
    {
        Microsoft.CodeAnalysis.Accessibility.Public => "public",
        Microsoft.CodeAnalysis.Accessibility.Internal => "internal",
        Microsoft.CodeAnalysis.Accessibility.Protected => "protected",
        Microsoft.CodeAnalysis.Accessibility.ProtectedOrInternal => "protected internal",
        Microsoft.CodeAnalysis.Accessibility.ProtectedAndInternal => "private protected",
        _ => "private",
    };

    public static string? Summary(ISymbol symbol, CancellationToken cancellationToken)
    {
        string? xml = symbol.GetDocumentationCommentXml(cancellationToken: cancellationToken);
        if (string.IsNullOrWhiteSpace(xml))
            return null;

        XElement? summary;
        try
        {
            summary = XDocument.Parse(xml).Descendants("summary").FirstOrDefault();
        }
        catch (XmlException)
        {
            return null;
        }

        if (summary is null)
            return null;

        StringBuilder builder = new();
        Render(summary, builder);
        string text = Whitespace().Replace(builder.ToString(), " ").Trim();
        if (text.Length == 0)
            return null;

        if (text.Length > MaxSummaryLength)
        {
            int end = char.IsHighSurrogate(text[MaxSummaryLength - 1]) ? MaxSummaryLength - 1 : MaxSummaryLength;
            text = text[..end];
        }

        return text;
    }

    private static void Render(XElement element, StringBuilder builder)
    {
        foreach (XNode node in element.Nodes())
        {
            switch (node)
            {
                case XText text:
                    builder.Append(text.Value);
                    break;
                case XElement child:
                    RenderElement(child, builder);
                    break;
            }
        }
    }

    private static void RenderElement(XElement element, StringBuilder builder)
    {
        switch (element.Name.LocalName)
        {
            case "see" or "seealso":
                if (element.Attribute("cref")?.Value is { Length: > 0 } cref)
                    builder.Append(LastSegment(cref));
                else if (element.Attribute("langword")?.Value is { Length: > 0 } langword)
                    builder.Append(langword);
                else if (element.Value.Length > 0)
                    builder.Append(element.Value);
                else if (element.Attribute("href")?.Value is { Length: > 0 } href)
                    builder.Append(href);

                break;
            case "paramref" or "typeparamref":
                builder.Append(element.Attribute("name")?.Value);
                break;
            case "inheritdoc":
                break;
            case "para" or "list" or "item" or "listheader" or "term" or "description" or "code" or "br":
                builder.Append(' ');
                Render(element, builder);
                builder.Append(' ');
                break;
            default:
                Render(element, builder);
                break;
        }
    }

    private static string LastSegment(string cref)
    {
        int colon = cref.IndexOf(':', StringComparison.Ordinal);
        string name = colon >= 0 ? cref[(colon + 1)..] : cref;
        int parenthesis = name.IndexOf('(', StringComparison.Ordinal);
        if (parenthesis >= 0)
            name = name[..parenthesis];

        int dot = name.LastIndexOf('.');
        if (dot >= 0)
            name = name[(dot + 1)..];

        int tick = name.IndexOf('`', StringComparison.Ordinal);
        return tick >= 0 ? name[..tick] : name;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
