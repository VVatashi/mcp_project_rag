using Microsoft.CodeAnalysis;

namespace ProjectRag.Code.Chunks;

/// <summary>
/// The kind of a unit of the chunk plan.
/// </summary>
internal enum ChunkUnitKind
{
    /// <summary>A whole type, or a small type that goes with its neighbors.</summary>
    Type,

    /// <summary>The declaration of a large type with its doc comment.</summary>
    Header,

    /// <summary>A member of a type or a namespace.</summary>
    Member,

    /// <summary>Lines outside any type.</summary>
    TopLevel,
}

/// <summary>
/// A range of whole lines that becomes one chunk, or several parts when it is too long.
/// </summary>
/// <param name="heading">The heading without the path.</param>
/// <param name="firstLine">The first line.</param>
/// <param name="lastLine">The last line.</param>
/// <param name="group">The group of units that may merge. Units of different groups never merge.</param>
/// <param name="kind">The kind.</param>
internal sealed class ChunkUnit(string heading, int firstLine, int lastLine, int group, ChunkUnitKind kind)
{
    /// <summary>Gets the heading without the path.</summary>
    public string Heading { get; } = heading;

    /// <summary>Gets or sets the first line.</summary>
    public int FirstLine { get; set; } = firstLine;

    /// <summary>Gets or sets the last line.</summary>
    public int LastLine { get; set; } = lastLine;

    /// <summary>Gets the group of units that may merge.</summary>
    public int Group { get; } = group;

    /// <summary>Gets the kind.</summary>
    public ChunkUnitKind Kind { get; } = kind;

    /// <summary>Gets the number of members that the unit holds.</summary>
    public int Count { get; private set; } = 1;

    /// <summary>Gets or sets the number of characters of the range.</summary>
    public int Length { get; set; }

    /// <summary>Gets or sets a value that tells whether every member of the unit is under the minimum size.</summary>
    public bool Small { get; set; }

    /// <summary>Gets or sets the syntax node of a single member, used to find the statements of a long member.</summary>
    public SyntaxNode? Node { get; set; }

    /// <summary>
    /// Joins a later unit of the same group into this unit.
    /// </summary>
    /// <param name="other">The later unit.</param>
    /// <param name="lines">The lines of the text.</param>
    public void Absorb(ChunkUnit other, LineIndex lines)
    {
        LastLine = Math.Max(LastLine, other.LastLine);
        if (Kind != ChunkUnitKind.Header)
            Count += other.Count;

        Small &= other.Small;
        Node = null;
        Length = lines.Length(FirstLine, LastLine);
    }
}
