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
            <p>The level \(p\) has \(2^{-p}\) of the size, and \(\textit{num_levels}\) counts them.</p>
            <div class="stemblock"><div class="content">\[M = \lfloor\log_2 w\rfloor\]</div></div>
            </body>
            """);

        Assert.Contains("```c\nUInt32 levelCount\nUInt32 flags\n```", text.Content, StringComparison.Ordinal);
        Assert.Contains("- First item\n\n  - Inner item", text.Content, StringComparison.Ordinal);
        Assert.Contains("- Second \\| item", text.Content, StringComparison.Ordinal);
        Assert.Contains("Table 1. Schemes\n\n| Id | Name |\n| --- | --- |\n| 0 | `a\\|b` |\n| Reserved |  |", text.Content, StringComparison.Ordinal);
        Assert.Contains("The level $p$ has $2^{-p}$ of the size, and $\\textit{num\\_levels}$ counts them.", text.Content, StringComparison.Ordinal);
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
    public void Read_EndsACellOverTwoRowsWithTheHeadAndKeepsTheLinkOfACard()
    {
        DocumentText text = Read("""
            <table><thead><tr><th rowspan="2">A</th><th>B</th></tr></thead><tbody><tr><td>1</td><td>2</td></tr></tbody></table>
            <a href="/guide"><div>Guide</div><p>The setup.</p></a>
            <a name="old"><h3>Old style</h3></a>
            """);

        Assert.Contains("| A | B |\n| --- | --- |\n| 1 | 2 |", text.Content, StringComparison.Ordinal);
        Assert.Contains("[Guide The setup.](https://example.com/guide)", text.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("<!-- rag:anchor old -->", text.Content, StringComparison.Ordinal);
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
    public void Read_TakesTheDollarsOfMathJaxWhenThePageSetsThem()
    {
        DocumentText text = Read("""
            <head><script type="text/x-mathjax-config">MathJax.Hub.Config({ tex2jax: { inlineMath: [['$','$'], ['\\(','\\)']] } });</script></head>
            <body><p>The value $R'$ is nonlinear, and \(G\) is linear.</p>
            <div class="informalequation"><span class="phrase">$$N = 9$$ $$B = 15$$</span></div>
            <div class="informalequation"><span class="phrase">\begin{align*} R &amp;= {R' \over 12.92} \end{align*}</span></div>
            </body>
            """);

        Assert.Contains("The value $R'$ is nonlinear, and $G$ is linear.", text.Content, StringComparison.Ordinal);
        Assert.Contains("$$\nN = 9\n$$\n\n$$\nB = 15\n$$", text.Content, StringComparison.Ordinal);
        Assert.Contains("$$\n\\begin{align*} R &= {R' \\over 12.92} \\end{align*}\n$$", text.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_KeepsCodeAnchorsAndTablesAroundTheFormulasOfMathJax()
    {
        DocumentText text = Read("""
            <head><script>MathJax.Hub.Config({ tex2jax: { inlineMath: [['\\(','\\)'], ['$','$']] } });</script></head>
            <body><p>See <a href="#eq1">the sum</a> and $x$.</p>
            <div class="informalequation"><a id="eq1"></a>$$a + b$$</div>
            <div class="listingblock"><div class="content"><pre>\begin{align}x\end{align}</pre></div></div>
            <table><tr><th>Norm</th></tr><tr><td><div>$$|x|$$</div></td></tr></table>
            </body>
            """);

        Assert.Contains("See [the sum](#eq1) and $x$.", text.Content, StringComparison.Ordinal);
        Assert.Contains("<!-- rag:anchor eq1 -->\n$$\na + b\n$$", text.Content, StringComparison.Ordinal);
        Assert.Contains("```\n\\begin{align}x\\end{align}\n```", text.Content, StringComparison.Ordinal);
        Assert.Contains("| $\\vert x\\vert $ |", text.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_KeepsTheDollarsOfAPageWithoutTheSetting()
    {
        DocumentText text = Read("<p>It costs $5 and $6.</p>");

        Assert.Contains("It costs \\$5 and \\$6.", text.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_TakesTheLatexBackFromAFormulaOfKatex()
    {
        // KaTeX drew f = c / (2^b - 1) on the server. A box above the strut is the numerator or the superscript.
        DocumentText text = Read("""
            <div class="stemblock"><div class="content"><span class="katex-display"><span class="katex"><span class="katex-html" aria-hidden="true">
            <span class="base"><span class="strut" style="height:1.8em;"></span><span class="mord mathnormal">f</span><span class="mspace"></span><span class="mrel">=</span></span>
            <span class="base"><span class="mord"><span class="mopen nulldelimiter"></span><span class="mfrac"><span class="vlist-t vlist-t2"><span class="vlist-r"><span class="vlist">
            <span style="top:-2.314em;"><span class="pstrut" style="height:3em;"></span><span class="mord"><span class="mord"><span class="mord">2</span><span class="msupsub"><span class="vlist-t"><span class="vlist-r"><span class="vlist">
            <span style="top:-2.931em;"><span class="pstrut" style="height:2.7em;"></span><span class="sizing mtight"><span class="mord mathnormal mtight">b</span></span></span>
            </span></span></span></span></span><span class="mbin">−</span><span class="mord">1</span></span></span>
            <span style="top:-3.23em;"><span class="pstrut" style="height:3em;"></span><span class="frac-line"></span></span>
            <span style="top:-3.677em;"><span class="pstrut" style="height:3em;"></span><span class="mord"><span class="mord mathnormal">c</span></span></span>
            </span><span class="vlist-s">​</span></span></span></span><span class="mclose nulldelimiter"></span></span></span>
            </span></span></span></div></div>
            <p>The value <span class="katex"><span class="katex-html"><span class="base"><span class="mord mathnormal">x</span><span class="mrel">≤</span><span class="mord text"><span class="mord textrm">a &amp; b</span></span></span></span></span> holds.</p>
            """);

        Assert.Contains("$$\nf=\\frac{c}{2^{b}-1}\n$$", text.Content, StringComparison.Ordinal);
        Assert.Contains("The value $x\\leq \\text{a \\& b}$ holds.", text.Content, StringComparison.Ordinal);
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
