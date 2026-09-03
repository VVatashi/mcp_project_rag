using System.Collections.Immutable;
using System.Text;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Tokenizers.DotNet;

namespace ProjectRAG.Cli;

/// <summary>A leaf of the tree. Holds the source text of one non-heading block, the lines it covers and the number of tokens in it.</summary>
internal readonly record struct ContentNode(string Text, int Line, int EndLine, int TokenCount);

/// <summary>A heading with its own content leaves and the headings nested under it.</summary>
internal readonly record struct HeadingNode(int Level, string Title, int Line, ImmutableArray<ContentNode> Content, ImmutableArray<HeadingNode> Children);

/// <summary>Collects one heading while the source is read. Becomes an immutable HeadingNode at the end.</summary>
internal sealed class HeadingBuilder(int level, string title, int line)
{
    public int Level { get; } = level;

    public string Title { get; } = title;

    public int Line { get; } = line;

    public List<ContentNode> Content { get; } = new();

    public List<HeadingBuilder> Children { get; } = new();
}

/// <summary>The whole Markdown document. Preamble holds the content before the first heading.</summary>
internal readonly record struct MarkdownTree(ImmutableArray<ContentNode> Preamble, ImmutableArray<HeadingNode> Headings)
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();

    /// <summary>Parses the Markdown source, nests every block under the heading above it and counts the tokens of every leaf.</summary>
    public static MarkdownTree Parse(string text, Tokenizer tokenizer)
    {
        MarkdownDocument document = Markdown.Parse(text, Pipeline);

        List<ContentNode> preamble = new();
        List<HeadingBuilder> roots = new();
        Stack<HeadingBuilder> open = new();

        foreach (Block block in document)
        {
            if (block is HeadingBlock heading)
            {
                HeadingBuilder node = new(heading.Level, ReadTitle(heading), heading.Line + 1);

                while (open.Count > 0 && open.Peek().Level >= node.Level)
                    open.Pop();

                if (open.Count > 0)
                    open.Peek().Children.Add(node);
                else
                    roots.Add(node);

                open.Push(node);
                continue;
            }

            ContentNode content = ReadContent(block, text, tokenizer);
            if (content.Text.Length == 0)
                continue;

            if (open.Count > 0)
                open.Peek().Content.Add(content);
            else
                preamble.Add(content);
        }

        return new MarkdownTree([.. preamble], Freeze(roots));
    }

    private static ImmutableArray<HeadingNode> Freeze(List<HeadingBuilder> builders)
    {
        ImmutableArray<HeadingNode>.Builder nodes = ImmutableArray.CreateBuilder<HeadingNode>(builders.Count);
        foreach (HeadingBuilder builder in builders)
            nodes.Add(new HeadingNode(builder.Level, builder.Title, builder.Line, [.. builder.Content], Freeze(builder.Children)));

        return nodes.MoveToImmutable();
    }

    private static ContentNode ReadContent(Block block, string text, Tokenizer tokenizer)
    {
        SourceSpan span = block.Span;
        ReadOnlySpan<char> raw = span.Start >= 0 && span.Length > 0 && span.End < text.Length ? text.AsSpan(span.Start, span.Length).TrimEnd() : default;
        string source = raw.Trim().ToString();
        int tokenCount = source.Length > 0 ? tokenizer.Encode(source).Length : 0;
        int line = block.Line + 1;

        return new ContentNode(source, line, line + raw.Count('\n'), tokenCount);
    }

    private static string ReadTitle(HeadingBlock heading)
    {
        if (heading.Inline is null)
            return string.Empty;

        StringBuilder title = new();
        foreach (Inline inline in heading.Inline.Descendants())
        {
            switch (inline)
            {
                case LiteralInline literal:
                    title.Append(literal.Content.ToString());
                    break;

                case CodeInline code:
                    title.Append(code.Content);
                    break;

                case LineBreakInline:
                    title.Append(' ');
                    break;
            }
        }

        return title.ToString().Trim();
    }
}
