using System.Text;

using ProjectRag.Core.Documents;
using ProjectRag.Core.Documents.Html;

namespace ProjectRag.Core.Tests.Documents.Html;

public sealed class HtmlMarkdownImporterTests
{
    private static readonly Uri Address = new("https://example.com/specs/format.html");

    [Fact]
    public void Read_WritesTheHeadingsWithTheirAnchorsAndTheLinks()
    {
        DocumentText text = Read("""
            <html><head><title>Format Specification</title></head><body>
            <div id="toc"><ul><li><a href="#_header">1. Header</a></li></ul></div>
            <h1>Format Specification</h1>
            <div class="sect1"><h2 id="_header">1. Header</h2>
            <p>The header holds the size. See <a href="#_levels">Section 2</a>, <a href="format.html#levelCount">the count</a> and <a href="other.html">the other page</a>.</p></div>
            <h2 id="_levels">2. Levels</h2>
            <p><a id="levelCount"></a>The count of the levels.</p>
            <script>var x = 1;</script>
            <div id="footer">Last updated today</div>
            </body></html>
            """);

        Assert.Equal("Format Specification", text.Title);
        Assert.Contains("<!-- rag:anchor _header -->\n## 1. Header", text.Content, StringComparison.Ordinal);
        Assert.Contains("See [Section 2](#_levels), [the count](#levelCount) and [the other page](https://example.com/specs/other.html).", text.Content, StringComparison.Ordinal);
        Assert.Contains("<!-- rag:anchor levelCount -->\nThe count of the levels.", text.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("var x", text.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("Last updated", text.Content, StringComparison.Ordinal);
        // The table of contents goes away, so the heading appears once.
        Assert.Equal(2, text.Content.Split("1. Header").Length);
    }

    [Fact]
    public void Read_WritesCodeListsTablesAndFormulas()
    {
        DocumentText text = Read("""
            <body>
            <div class="listingblock"><pre class="highlight"><code class="language-c" data-lang="c">UInt32 levelCount
            UInt32 flags</code></pre></div>
            <ul><li><p>First item</p><ul><li>Inner item</li></ul></li><li>Second | item</li></ul>
            <table><caption>Table 1. Schemes</caption><thead><tr><th>Id</th><th>Name</th></tr></thead>
            <tbody><tr><td><p>0</p></td><td><code>a|b</code></td></tr><tr><td colspan="2">Reserved</td></tr></tbody></table>
            <p>The level \(p\) has \(2^{-p}\) of the size.</p>
            <div class="stemblock"><div class="content">\[M = \lfloor\log_2 w\rfloor\]</div></div>
            </body>
            """);

        Assert.Contains("```c\nUInt32 levelCount\nUInt32 flags\n```", text.Content, StringComparison.Ordinal);
        Assert.Contains("- First item\n\n  - Inner item", text.Content, StringComparison.Ordinal);
        Assert.Contains("- Second \\| item", text.Content, StringComparison.Ordinal);
        Assert.Contains("Table 1. Schemes\n\n| Id | Name |\n| --- | --- |\n| 0 | `a\\|b` |\n| Reserved |  |", text.Content, StringComparison.Ordinal);
        Assert.Contains("The level $p$ has $2^{-p}$ of the size.", text.Content, StringComparison.Ordinal);
        Assert.Contains("$$\nM = \\lfloor\\log_2 w\\rfloor\n$$", text.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_MakesATermWithALongDefinitionAHeading()
    {
        DocumentText text = Read("""
            <body>
            <h2 id="formats">Appendix B: Formats</h2>
            <dl>
            <dt>VK_FORMAT_R8_UNORM</dt><dd><dl><dt>OpenGL</dt><dd><ul><li>GL_R8</li></ul></dd></dl></dd>
            <div><dt>VK_FORMAT_R8_SNORM</dt><dd><p>No mapping.</p></dd></div>
            </dl>
            <dl><dt>Premultiplied Alpha</dt><dd><p>The colors carry the alpha.</p></dd></dl>
            <div class="admonitionblock note"><table><tr><td class="icon"><i class="fa icon-note" title="Note"></i></td><td class="content">A note.</td></tr></table></div>
            </body>
            """);

        Assert.Contains("## Appendix B: Formats\n\n### VK_FORMAT_R8_UNORM\n\n#### OpenGL\n\n- GL_R8\n\n### VK_FORMAT_R8_SNORM\n\nNo mapping.", text.Content, StringComparison.Ordinal);
        Assert.Contains("\n\nPremultiplied Alpha\n\nThe colors carry the alpha.\n\nA note.\n", text.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_KeepsTheColumnsOfACellOverTwoRowsAndATableInACell()
    {
        DocumentText text = Read("""
            <table><tr><th>Format</th><th>Size</th><th>Type</th></tr>
            <tr><td rowspan="2">R8</td><td>1</td><td>UNORM</td></tr>
            <tr><td>1</td><td>SNORM</td></tr>
            <tr><td>RG8</td><td><table><tr><td>a|b</td><td>c</td></tr></table></td><td>UINT</td></tr></table>
            """);

        Assert.Contains("| R8 | 1 | UNORM |\n|  | 1 | SNORM |\n| RG8 | a\\|b c | UINT |", text.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_KeepsALineWithAnImageAndDropsTheLinksOfScripts()
    {
        DocumentText text = Read("""
            <head><base href="https://example.com/docs/"></head>
            <ul><li>Press <kbd>Ctrl</kbd> <img src="key.png"> to <a href="javascript:save()">save</a> the <a href="guide.html">guide</a>.</li>
            <ul><li>A nested item</li></ul></ul>
            """);

        Assert.Contains("- Press `Ctrl` to save the [guide](https://example.com/docs/guide.html).\n\n  - A nested item", text.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_KeepsTheRelativeLinksOfAFile()
    {
        DocumentText text = HtmlMarkdownImporter.Read(Encoding.UTF8.GetBytes("<p>See <a href=\"guide.html#setup\">the setup</a>.</p>"), "index", null);

        Assert.Equal("index", text.Title);
        Assert.Equal("See [the setup](guide.html#setup).\n", text.Content);
    }

    private static DocumentText Read(string html) => HtmlMarkdownImporter.Read(Encoding.UTF8.GetBytes(html), "format", Address);
}
