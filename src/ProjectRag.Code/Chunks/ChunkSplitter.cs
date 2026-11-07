using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using ProjectRag.Core.Code.Chunks;

namespace ProjectRag.Code.Chunks;

/// <summary>
/// Turns the planned units into chunks and splits a unit that is longer than the limit into parts.
/// </summary>
/// <param name="lines">The lines of the text.</param>
/// <param name="path">The path of the file.</param>
/// <param name="options">The limits of the chunks.</param>
internal sealed class ChunkSplitter(LineIndex lines, string path, CodeChunkingOptions options)
{
    private readonly int _maxChars = Math.Max(1, options.MaxChars);
    private readonly int _overlapLines = Math.Max(0, options.OverlapLines);

    /// <summary>
    /// Writes the chunks of a unit.
    /// </summary>
    /// <param name="unit">The unit.</param>
    /// <param name="output">The list of the chunks.</param>
    public void Emit(ChunkUnit unit, List<CodeChunk> output)
    {
        string heading = path + " > " + unit.Heading;
        if (unit.Kind != ChunkUnitKind.Header && unit.Count > 1)
            heading += $" (+{unit.Count - 1} more)";

        if (unit.Length <= _maxChars)
        {
            output.Add(new CodeChunk(heading, lines.Start(unit.FirstLine), lines.End(unit.LastLine), unit.FirstLine + 1, unit.LastLine + 1));
            return;
        }

        bool topLevel = unit.Kind == ChunkUnitKind.TopLevel;
        int overlapLines = topLevel ? 0 : _overlapLines;
        (int[] primary, int[] secondary) = topLevel ? (ParagraphStarts(unit), Array.Empty<int>()) : StatementStarts(unit);
        int part = 0;
        int newStart = unit.FirstLine;
        int previousStart = newStart;
        bool lineBased = false;
        while (newStart <= unit.LastLine)
        {
            if (lines.IsBlank(newStart))
            {
                newStart++;
                continue;
            }

            int overlap = lineBased ? Math.Min(overlapLines, newStart - previousStart) : 0;
            while (overlap > 0 && lines.End(newStart) - lines.Start(newStart - overlap) > _maxChars)
                overlap--;

            int chunkStart = lines.Start(newStart - overlap);
            if (lines.End(newStart) - chunkStart > _maxChars)
            {
                part = EmitPieces(newStart, heading, part, topLevel, output);
                previousStart = newStart;
                newStart++;
                lineBased = false;
                continue;
            }

            int fit = newStart;
            while (fit < unit.LastLine && lines.End(fit + 1) - chunkStart <= _maxChars)
                fit++;

            int cut = fit == unit.LastLine ? fit : ChooseCut(primary, secondary, newStart, fit, chunkStart);
            int end = cut;
            while (end > newStart && lines.IsBlank(end))
                end--;

            part++;
            output.Add(new CodeChunk(Label(heading, part, topLevel), chunkStart, lines.End(end), newStart - overlap + 1, end + 1, lines.Start(newStart) - chunkStart));
            previousStart = newStart;
            newStart = cut + 1;
            lineBased = true;
        }
    }

    private static string Label(string heading, int part, bool topLevel) => topLevel ? heading : $"{heading} (part {part})";

    private int ChooseCut(int[] primary, int[] secondary, int newStart, int fit, int chunkStart)
    {
        int statement = Largest(primary, newStart, fit + 1);
        if (statement > 0 && lines.End(statement - 1) - chunkStart >= _maxChars / 2)
            return statement - 1;

        int nested = Largest(secondary, newStart, fit + 1);
        int best = Math.Max(statement, nested);
        return best > 0 ? best - 1 : fit;
    }

    private static int Largest(int[] sorted, int exclusiveLow, int inclusiveHigh)
    {
        int index = Array.BinarySearch(sorted, inclusiveHigh);
        if (index < 0)
            index = ~index - 1;

        return index >= 0 && sorted[index] > exclusiveLow ? sorted[index] : -1;
    }

    private int EmitPieces(int line, string heading, int part, bool topLevel, List<CodeChunk> output)
    {
        string text = lines.Text;
        int position = lines.Start(line);
        int end = lines.End(line);
        while (position < end)
        {
            int cut = Math.Min(position + _maxChars, end);
            if (cut < end)
            {
                int floor = position + (_maxChars * 3 / 4);
                for (int index = cut - 1; index >= floor; index--)
                {
                    if (text[index] is ' ' or ',' or ';')
                    {
                        cut = index + 1;
                        break;
                    }
                }

                if (cut > position + 1 && char.IsHighSurrogate(text[cut - 1]))
                    cut--;
            }

            if (!text.AsSpan(position, cut - position).IsWhiteSpace())
            {
                part++;
                output.Add(new CodeChunk(Label(heading, part, topLevel), position, cut, line + 1, line + 1));
            }

            position = cut;
        }

        return part;
    }

    private int[] ParagraphStarts(ChunkUnit unit)
    {
        List<int> starts = [];
        for (int line = unit.FirstLine + 1; line <= unit.LastLine; line++)
        {
            if (!lines.IsBlank(line) && lines.IsBlank(line - 1))
                starts.Add(line);
        }

        return [.. starts];
    }

    private (int[] Primary, int[] Secondary) StatementStarts(ChunkUnit unit)
    {
        List<int> primary = [];
        List<int> secondary = [];
        foreach (BlockSyntax block in BodiesOf(unit.Node, primary))
        {
            foreach (StatementSyntax statement in block.Statements)
            {
                if (statement is not BlockSyntax)
                    primary.Add(StatementStart(statement, unit.FirstLine));
            }

            foreach (StatementSyntax statement in block.DescendantNodes().OfType<StatementSyntax>())
            {
                if (statement is not BlockSyntax && statement.Parent is BlockSyntax or SwitchSectionSyntax)
                    secondary.Add(StatementStart(statement, unit.FirstLine));
            }
        }

        secondary.AddRange(primary);
        return (Sorted(primary), Sorted(secondary));
    }

    private static int[] Sorted(List<int> values)
    {
        int[] result = [.. values.Distinct()];
        Array.Sort(result);
        return result;
    }

    private IEnumerable<BlockSyntax> BodiesOf(SyntaxNode? node, List<int> starts)
    {
        switch (node)
        {
            case BaseMethodDeclarationSyntax { Body: { } body }:
                yield return body;
                break;
            case BasePropertyDeclarationSyntax { AccessorList: { } accessors }:
                foreach (AccessorDeclarationSyntax accessor in accessors.Accessors)
                {
                    starts.Add(lines.LineOf(accessor.SpanStart));
                    if (accessor.Body is { } body)
                        yield return body;
                }

                break;
        }
    }

    private int StatementStart(StatementSyntax statement, int floor)
    {
        int line = lines.LineOf(statement.SpanStart);
        SyntaxToken previous = statement.GetFirstToken().GetPreviousToken();
        int low = Math.Max(floor, previous.RawKind == 0 ? floor : lines.LineOf(Math.Max(0, previous.Span.End - 1)) + 1);
        while (line - 1 >= low && lines.IsComment(line - 1))
            line--;

        return line;
    }
}
