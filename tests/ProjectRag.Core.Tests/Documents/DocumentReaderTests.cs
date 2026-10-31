using System.Text;

using ProjectRag.Core.Documents;

namespace ProjectRag.Core.Tests.Documents;

public sealed class DocumentReaderTests
{
    [Fact]
    public void Read_TakesTheTitleFromTheFirstHeading()
    {
        DocumentText text = DocumentReader.Read("docs/setup.md", Encoding.UTF8.GetBytes("# Setup of the server\r\n\r\nText.\r\n"));

        Assert.Equal("Setup of the server", text.Title);
        Assert.Equal("# Setup of the server\n\nText.\n", text.Content);
    }

    [Fact]
    public void Read_TakesTheTitleFromTheFileName()
    {
        DocumentText text = DocumentReader.Read("notes/todo.txt", Encoding.UTF8.GetBytes("Buy milk."));

        Assert.Equal("todo", text.Title);
    }

    [Fact]
    public void Read_DropsTheByteOrderMark()
    {
        byte[] bytes = [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes("Привет")];

        Assert.Equal("Привет", DocumentReader.Read("a.txt", bytes).Content);
    }

    [Fact]
    public void Read_DecodesUtf16WithItsMark()
    {
        byte[] bytes = [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes("Text")];

        Assert.Equal("Text", DocumentReader.Read("a.txt", bytes).Content);
    }

    [Fact]
    public void Clean_DropsCharactersThatPostgresRejects()
    {
        Assert.Equal("a\nb\tc�d\ne", TextCleanup.Clean("a\r\nb\tc\uD800d\fe\0"));
    }
}
