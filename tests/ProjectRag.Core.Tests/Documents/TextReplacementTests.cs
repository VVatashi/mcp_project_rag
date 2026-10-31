using ProjectRag.Core.Indexing;
using ProjectRag.Core.Sources;

namespace ProjectRag.Core.Tests.Documents;

public sealed class TextReplacementTests
{
    [Fact]
    public void ApplyAll_ChangesTheTextAndKeepsTheMarks()
    {
        const string text = "<!-- rag:anchor p12-3 -->\nsparse_color-Tv read() <!-- rag:page 5 --> sparse_color-T x;\n";

        string fixedText = TextReplacement.ApplyAll(text, [new TextReplacement { Pattern = "*.pdf", Find = @"sparse_color-(Tv?)\b", Replace = "sparse_color<$1>" }]);

        Assert.Equal("<!-- rag:anchor p12-3 -->\nsparse_color<Tv> read() <!-- rag:page 5 --> sparse_color<T> x;\n", fixedText);
        // A digit after a hyphen in an anchor stays as it is.
        Assert.Contains("p12-3", TextReplacement.ApplyAll(text, [new TextReplacement { Pattern = "*", Find = @"-(\d)", Replace = "–$1" }]), StringComparison.Ordinal);
    }

    [Fact]
    public void ApplyAll_RejectsAReplacementThatMovesAMarkOffItsLine()
    {
        const string text = "A word that breaks at the end of a page with a hyph-\n<!-- rag:page 5 -->\nen.\n";

        Assert.Throws<InvalidOperationException>(() => TextReplacement.ApplyAll(text, [new TextReplacement { Pattern = "*", Find = @"-\n", Replace = string.Empty }]));
    }

    [Fact]
    public void FileVersion_ChangesWithTheReplacements()
    {
        TextReplacement first = new() { Pattern = "*.pdf", Find = "a", Replace = "b" };
        TextReplacement second = new() { Pattern = "*.pdf", Find = "c", Replace = "d" };

        Assert.Equal("8", Indexer.FileVersion("8", []));
        Assert.NotEqual(Indexer.FileVersion("8", [first, second]), Indexer.FileVersion("8", [second, first]));
        // The texts of a pair do not run together: ("a\tb", "c") differs from ("a", "b\tc").
        Assert.NotEqual(
            Indexer.FileVersion("8", [new TextReplacement { Find = "a\tb", Replace = "c" }]),
            Indexer.FileVersion("8", [new TextReplacement { Find = "a", Replace = "b\tc" }]));
    }
}
