using System.Text;

using ProjectRag.Core.Documents;
using ProjectRag.Core.Documents.DocBook;

namespace ProjectRag.Core.Tests.Documents.DocBook;

public sealed class DocBookMarkdownImporterTests
{
    // A reference page in the form of the OpenGL pages: an entity from a file of the DTD and two parts of XInclude.
    private const string Page = """
        <!DOCTYPE refentry [ <!ENTITY % mathent SYSTEM "math.ent"> %mathent; ]>
        <refentry xmlns="http://docbook.org/ns/docbook" xmlns:xlink="http://www.w3.org/1999/xlink" version="5.0" xml:id="glViewport">
            <info><copyright><year>1991</year><holder>Silicon Graphics, Inc.</holder></copyright></info>
            <refmeta><refentrytitle>glViewport</refentrytitle><manvolnum>3G</manvolnum></refmeta>
            <refnamediv><refname>glViewport</refname><refname>glViewportf</refname><refpurpose>set the viewport</refpurpose></refnamediv>
            <refsynopsisdiv><title>C Specification</title>
                <funcsynopsis><funcprototype><funcdef>void <function>glViewport</function></funcdef>
                    <paramdef>GLint <parameter>x</parameter></paramdef><paramdef>GLsizei <parameter>width</parameter></paramdef></funcprototype></funcsynopsis>
            </refsynopsisdiv>
            <refsect1 xml:id="parameters"><title>Parameters</title>
                <variablelist><varlistentry><term><parameter>x</parameter></term>
                    <listitem><para>Specify the left corner&nbsp;of the viewport.</para><para>The initial value is 0.</para></listitem></varlistentry></variablelist>
            </refsect1>
            <refsect1 xml:id="description"><title>Description</title>
                <para>Let <inlineequation><mml:math xmlns:mml="http://www.w3.org/1998/Math/MathML"><mml:msub><mml:mi mathvariant="italic">x</mml:mi><mml:mi>nd</mml:mi></mml:msub></mml:math></inlineequation>
                    be the coordinate, so $x_w \leq width$. See <citerefentry><refentrytitle>glDepthRange</refentrytitle></citerefentry>.</para>
                <para><informalequation><mml:math xmlns:mml="http://www.w3.org/1998/Math/MathML">
                    <mml:mrow><mml:msub><mml:mi>x</mml:mi><mml:mi>w</mml:mi></mml:msub><mml:mo>=</mml:mo>
                    <mml:mfenced><mml:mfrac><mml:mi mathvariant="italic">width</mml:mi><mml:mn>2</mml:mn></mml:mfrac></mml:mfenced><mml:mo>&le;</mml:mo><mml:mi>&delta;s</mml:mi></mml:mrow>
                </mml:math></informalequation></para>
                <itemizedlist><listitem><para>A clamped width.</para></listitem></itemizedlist>
            </refsect1>
            <refsect1 xml:id="versions"><title>Version Support</title>
                <informaltable><tgroup cols="3">
                    <xi:include xmlns:xi="http://www.w3.org/2001/XInclude" href="apifunchead.xml" xpointer="xpointer(/*/*)"/>
                    <tbody><row><entry><function>glViewport</function></entry>
                        <xi:include xmlns:xi="http://www.w3.org/2001/XInclude" href="apiversion.xml" xpointer="xpointer(/*/*[@role='45']/*)"/></row></tbody>
                </tgroup></informaltable>
            </refsect1>
            <refsect1 xml:id="Copyright"><title>Copyright</title><para>Copyright 1991 Silicon Graphics.</para></refsect1>
        </refentry>
        """;

    private static readonly Dictionary<string, string> Files = new()
    {
        ["math.ent"] = """<!ENTITY le "&#x2264;"> <!ENTITY delta "&#x003B4;"> <!ENTITY nbsp "&#x000A0;">""",
        ["apifunchead.xml"] = """
            <funchead xmlns="http://docbook.org/ns/docbook">
                <thead><row><entry spanname="all">Version</entry></row><row><entry>Name</entry><entry>4.5</entry><entry>4.6</entry></row></thead>
            </funchead>
            """,
        ["apiversion.xml"] = """
            <root xmlns="http://docbook.org/ns/docbook">
                <row role="40"><entry>&#x2714;</entry><entry>&#x2714;</entry></row>
                <row role="45"><entry>-</entry><entry>&#x2714;</entry></row>
            </root>
            """,
    };

    [Fact]
    public void Read_WritesAReferencePage()
    {
        DocumentText text = Read(Page);

        Assert.Equal("glViewport", text.Title);
        Assert.StartsWith("# glViewport\n\nglViewport, glViewportf — set the viewport\n\n## C Specification\n\n```c\nvoid glViewport(GLint x, GLsizei width);\n```", text.Content, StringComparison.Ordinal);
        Assert.Contains("## Parameters\n\n`x`: Specify the left corner of the viewport.\n\nThe initial value is 0.", text.Content, StringComparison.Ordinal);
        Assert.Contains("Let $x_{\\mathrm{nd}}$ be the coordinate, so $x_w \\leq width$. See [glDepthRange](glDepthRange.xml).", text.Content, StringComparison.Ordinal);
        Assert.Contains("$$\nx_{w} = ( \\frac{\\mathit{width}}{2} ) \\le \\delta s\n$$", text.Content, StringComparison.Ordinal);
        Assert.Contains("- A clamped width.", text.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("Copyright", text.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_PutsThePartsOfXIncludeInPlace()
    {
        DocumentText text = Read(Page);

        Assert.Contains("## Version Support\n\n| Name | 4.5 | 4.6 |\n| --- | --- | --- |\n| `glViewport` | - | ✔ |", text.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_ReadsNoFileOutsideTheSource()
    {
        string page = Page.Replace("href=\"apiversion.xml\"", "href=\"../secret.xml\"", StringComparison.Ordinal);
        List<string> asked = [];

        DocumentText text = DocBookMarkdownImporter.Read(Encoding.UTF8.GetBytes(page), "page", path =>
        {
            asked.Add(path);
            return Files.TryGetValue(path, out string? content) ? Encoding.UTF8.GetBytes(content) : null;
        });

        Assert.DoesNotContain(asked, static path => path.Contains("secret", StringComparison.Ordinal));
        Assert.Contains("| `glViewport` |  |  |", text.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_StopsALoopOfParts()
    {
        const string loop = """
            <section xmlns="http://docbook.org/ns/docbook" xmlns:xi="http://www.w3.org/2001/XInclude"><para>Start.</para>
            <xi:include href="loop.xml"/><xi:include href="loop.xml"/><xi:include href="loop.xml"/><xi:include href="loop.xml"/><xi:include href="loop.xml"/>
            <xi:include href="loop.xml"/><xi:include href="loop.xml"/><xi:include href="loop.xml"/><xi:include href="loop.xml"/><xi:include href="loop.xml"/></section>
            """;
        System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();

        DocumentText text = DocBookMarkdownImporter.Read(Encoding.UTF8.GetBytes(loop), "loop", path => path == "loop.xml" ? Encoding.UTF8.GetBytes(loop) : null);

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5));
        Assert.StartsWith("Start.", text.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_KeepsThePricesTheCellsAndTheEmptyItems()
    {
        DocumentText text = Read("""
            <section xmlns="http://docbook.org/ns/docbook">
                <para>It costs $5 and $6 today, but $x + 1$ is a formula.</para>
                <informaltable><tgroup cols="3"><colspec colname="a"/><colspec colname="b"/><colspec colname="c"/>
                    <thead><row><entry>A</entry><entry>B</entry><entry>C</entry></row></thead>
                    <tbody><row><entry namest="a" nameend="b" morerows="1">wide</entry><entry>1</entry></row>
                    <row><entry colname="c">2</entry></row>
                    <row><entry><programlisting>a | b</programlisting></entry><entry>x</entry><entry>y</entry></row></tbody>
                </tgroup></informaltable>
                <orderedlist><listitem/><listitem><para>two</para></listitem></orderedlist>
            </section>
            """);

        Assert.Contains("It costs \\$5 and \\$6 today, but $x + 1$ is a formula.", text.Content, StringComparison.Ordinal);
        Assert.Contains("| wide |  | 1 |\n|  |  | 2 |\n| ``` a \\| b ``` | x | y |", text.Content, StringComparison.Ordinal);
        Assert.Contains("1.\n\n2. two", text.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_ReadsNoFileOutsideTheRoot()
    {
        string top = Path.Combine(Path.GetTempPath(), "docbook-" + Guid.NewGuid().ToString("N"));
        string root = Path.Combine(top, "src");
        Directory.CreateDirectory(Path.Combine(root, "gl4"));
        Directory.CreateDirectory(Path.Combine(top, "src2"));
        try
        {
            File.WriteAllText(Path.Combine(top, "src2", "secret.xml"), """<para xmlns="http://docbook.org/ns/docbook">SECRET</para>""");
            string absolute = Uri.EscapeDataString(Path.Combine(top, "src2", "secret.xml").Replace('\\', '/'));
            string page = $"""
                <section xmlns="http://docbook.org/ns/docbook" xmlns:xi="http://www.w3.org/2001/XInclude"><para>Start.</para>
                <xi:include href="../../src2/secret.xml"/><xi:include href="{absolute}"/><xi:include href="..%2F..%2Fsrc2%2Fsecret.xml"/></section>
                """;

            DocumentText text = DocumentReader.Read("gl4/page.xml", Encoding.UTF8.GetBytes(page), null, root + Path.DirectorySeparatorChar);

            Assert.DoesNotContain("SECRET", text.Content, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(top, recursive: true);
        }
    }

    [Fact]
    public void Read_TakesTheFilesOfTheSourceThroughTheReader()
    {
        string root = Path.Combine(Path.GetTempPath(), "docbook-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "gl4"));
        try
        {
            foreach ((string name, string content) in Files)
                File.WriteAllText(Path.Combine(root, "gl4", name), content);

            DocumentText text = DocumentReader.Read("gl4/glViewport.xml", Encoding.UTF8.GetBytes(Page), null, root);

            Assert.Contains("| `glViewport` | - | ✔ |", text.Content, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static DocumentText Read(string page) =>
        DocBookMarkdownImporter.Read(Encoding.UTF8.GetBytes(page), "page", static path => Files.TryGetValue(path, out string? content) ? Encoding.UTF8.GetBytes(content) : null);
}
