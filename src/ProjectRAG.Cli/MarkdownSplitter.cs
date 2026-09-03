using System.Collections.Immutable;
using System.Text;
using Microsoft.Extensions.Options;
using Tokenizers.DotNet;

namespace ProjectRAG.Cli;

/// <summary>Settings of the splitter, bound from the Chunking section of appsettings.json.</summary>
public sealed class ChunkOptions
{
    public const string SectionName = "Chunking";

    /// <summary>The largest number of tokens one chunk may hold.</summary>
    public int MaxTokens { get; set; } = 512;
}

/// <summary>A piece of a document ready for embedding. Text holds the joined leaves, the other fields say where they come from.</summary>
public readonly record struct Chunk(string Source, ImmutableArray<string> HeadingPath, string Text, int Line, int EndLine, int TokenCount)
{
    /// <summary>Builds the text of the chunk: the heading path as Markdown headings, then the body.
    /// The level of a heading follows its depth in the path, so a document that jumps from one level to the next but one renders without the gap.</summary>
    public string Render() => Render(HeadingPath, Text);

    /// <summary>Builds the same text for a path and a body that are not a chunk yet, so that the caller can count its tokens.</summary>
    internal static string Render(ImmutableArray<string> path, string text)
    {
        StringBuilder full = new();
        for (int level = 1; level <= path.Length; level++)
            full.Append('#', level).Append(' ').Append(path[level - 1]).Append('\n');

        return full.Append(text).ToString();
    }
}

/// <summary>Cuts a document into chunks. One splitter reads one family of file types.</summary>
public interface IDocumentSplitter
{
    /// <summary>The file extensions this splitter reads, with the leading dot in lower case.</summary>
    ImmutableArray<string> Extensions { get; }

    /// <summary>Tells whether this splitter reads the file at that path.</summary>
    bool CanSplit(string path) => Extensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    /// <summary>Splits text the caller has already read. The path goes into the metadata of every chunk.</summary>
    ImmutableArray<Chunk> Split(string path, string text);

    /// <summary>Reads the file and splits it.</summary>
    async Task<ImmutableArray<Chunk>> SplitAsync(string path, CancellationToken cancellationToken = default) => Split(path, await File.ReadAllTextAsync(path, cancellationToken));
}

/// <summary>Splits Markdown. Every chunk holds the leaves of one heading and the titles of the headings above it.</summary>
public sealed class MarkdownSplitter(Tokenizer tokenizer, IOptions<ChunkOptions> options) : IDocumentSplitter
{
    private const string Separator = "\n\n";

    private static readonly ImmutableArray<string> MarkdownExtensions = [".md", ".markdown"];

    private readonly Tokenizer _tokenizer = tokenizer;
    private readonly int _maxTokens = Positive(options.Value.MaxTokens);

    public ImmutableArray<string> Extensions => MarkdownExtensions;

    /// <summary>Walks the tree in order and packs the leaves into chunks of at most MaxTokens tokens. Leaves of two different headings never share a chunk.</summary>
    public ImmutableArray<Chunk> Split(string path, string text)
    {
        MarkdownTree tree = MarkdownTree.Parse(text, _tokenizer);
        ImmutableArray<Chunk>.Builder chunks = ImmutableArray.CreateBuilder<Chunk>();

        Pack(chunks, path, [], tree.Preamble);
        Walk(chunks, path, [], tree.Headings);

        return chunks.ToImmutable();
    }

    private void Walk(ImmutableArray<Chunk>.Builder chunks, string source, ImmutableArray<string> path, ImmutableArray<HeadingNode> headings)
    {
        foreach (HeadingNode heading in headings)
        {
            ImmutableArray<string> nested = path.Add(heading.Title);

            Pack(chunks, source, nested, heading.Content);
            Walk(chunks, source, nested, heading.Children);
        }
    }

    private void Pack(ImmutableArray<Chunk>.Builder chunks, string source, ImmutableArray<string> path, ImmutableArray<ContentNode> content)
    {
        List<ContentNode> group = new();
        int tokens = 0;

        foreach (ContentNode node in content)
        {
            if (node.Text.Length == 0)
                continue;

            foreach (ContentNode piece in Cut(node))
            {
                if (group.Count > 0 && tokens + piece.TokenCount > _maxTokens)
                {
                    chunks.Add(Build(source, path, group));
                    group.Clear();
                    tokens = 0;
                }

                group.Add(piece);
                tokens += piece.TokenCount;
            }
        }

        if (group.Count > 0)
            chunks.Add(Build(source, path, group));
    }

    /// <summary>Cuts a leaf that alone does not fit into pieces that do. The cut runs along line borders.</summary>
    private IEnumerable<ContentNode> Cut(ContentNode node)
    {
        if (node.TokenCount <= _maxTokens)
        {
            yield return node;
            yield break;
        }

        StringBuilder piece = new();
        string[] lines = node.Text.Split('\n');
        int start = node.Line;
        int line = node.Line;
        int tokens = 0;

        for (int index = 0; index < lines.Length; index++)
        {
            line = node.Line + index;
            foreach (string part in Slice(lines[index]))
            {
                int count = _tokenizer.Encode(part).Length;
                if (piece.Length > 0 && tokens + count > _maxTokens)
                {
                    yield return new ContentNode(piece.ToString(), start, line, tokens);
                    piece.Clear();
                    tokens = 0;
                    start = line;
                }

                if (piece.Length > 0)
                    piece.Append('\n');

                piece.Append(part);
                tokens += count;
            }
        }

        if (piece.Length > 0)
            yield return new ContentNode(piece.ToString(), start, line, tokens);
    }

    /// <summary>Cuts one line that alone does not fit. The first guess keeps the ratio of characters to tokens of the whole line, and every piece shrinks until it fits, because the ratio changes along a line.</summary>
    private IEnumerable<string> Slice(string line)
    {
        int count = _tokenizer.Encode(line).Length;
        if (count <= _maxTokens)
        {
            yield return line;
            yield break;
        }

        int size = (int)Math.Max(1L, (long)line.Length * _maxTokens / count);
        for (int start = 0; start < line.Length;)
        {
            int length = Math.Min(size, line.Length - start);
            while (length > 1 && _tokenizer.Encode(line[start..(start + length)]).Length > _maxTokens)
                length = length * 9 / 10;

            yield return line[start..(start + length)];
            start += length;
        }
    }

    /// <summary>Joins the leaves of one group into a chunk. The token count comes from a second pass of the tokenizer,
    /// because the heading path in front of the body changes the count, and an exact number matters more than one pass saved.</summary>
    private Chunk Build(string source, ImmutableArray<string> path, List<ContentNode> nodes)
    {
        StringBuilder body = new();
        foreach (ContentNode node in nodes)
        {
            if (body.Length > 0)
                body.Append(Separator);

            body.Append(node.Text);
        }

        string text = body.ToString();
        return new Chunk(source, path, text, nodes[0].Line, nodes[^1].EndLine, _tokenizer.Encode(Chunk.Render(path, text)).Length);
    }

    private static int Positive(int maxTokens)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxTokens, nameof(ChunkOptions.MaxTokens));
        return maxTokens;
    }
}
