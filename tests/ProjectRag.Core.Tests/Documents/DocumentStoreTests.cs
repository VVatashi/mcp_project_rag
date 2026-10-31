using ProjectRag.Core.Documents;

namespace ProjectRag.Core.Tests.Documents;

public sealed class DocumentStoreTests
{
    [Theory]
    [InlineData(0, true)]
    [InlineData(9, true)]
    [InlineData(10, false)]
    [InlineData(47, true)]
    [InlineData(53, false)]
    public void StartsBlock_FindsTheStartsOfBlocks(int offset, bool expected)
    {
        // A paragraph, a table after an anchor, and the second row of the table.
        const string content = "# Title\n\nA paragraph.\n\n<!-- rag:anchor t-1 -->\n| a |\n| --- |\n";

        Assert.Equal(expected, DocumentStore.StartsBlock(content, offset));
    }
}
