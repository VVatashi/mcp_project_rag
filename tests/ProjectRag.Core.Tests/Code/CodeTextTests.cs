using System.Text;

using ProjectRag.Core.Code;

namespace ProjectRag.Core.Tests.Code;

public sealed class CodeTextTests
{
    [Fact]
    public void Decode_StripsTheMarkOfUtf8AndKeepsTheText()
    {
        byte[] bytes = [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes("aé中")];

        Assert.Equal("aé中", CodeText.Decode(bytes));
    }

    [Fact]
    public void Decode_ReadsUtf16InBothByteOrders()
    {
        byte[] little = [0xFF, 0xFE, .. Encoding.Unicode.GetBytes("line 1\r\nline 2")];
        byte[] big = [0xFE, 0xFF, .. Encoding.BigEndianUnicode.GetBytes("line 1\r\nline 2")];

        Assert.Equal("line 1\nline 2", CodeText.Decode(little));
        Assert.Equal("line 1\nline 2", CodeText.Decode(big));
    }

    [Fact]
    public void Decode_TakesTextWithoutAMarkAsUtf8AndRepairsBadBytes()
    {
        Assert.Equal("café", CodeText.Decode(Encoding.UTF8.GetBytes("café")));
        Assert.Equal("a�b", CodeText.Decode([(byte)'a', 0xFF, (byte)'b']));
    }

    [Theory]
    [InlineData("a\r\nb\r\n", "a\nb\n")]
    [InlineData("a\rb\r", "a\nb\n")]
    [InlineData("a\r\n\r\nb", "a\n\nb")]
    [InlineData("a\n\rb", "a\n\nb")]
    [InlineData("no ends", "no ends")]
    public void Decode_ReplacesTheLineEnds(string text, string expected) =>
        Assert.Equal(expected, CodeText.Decode(Encoding.UTF8.GetBytes(text)));

    [Fact]
    public void IsBinary_FindsANulByteInTheFirstEightKilobytes()
    {
        byte[] text = new byte[20_000];
        Array.Fill(text, (byte)'a');
        byte[] early = [.. text];
        early[8191] = 0;
        byte[] late = [.. text];
        late[8192] = 0;

        Assert.True(CodeText.IsBinary(early));
        Assert.False(CodeText.IsBinary(late));
        Assert.False(CodeText.IsBinary(text));
        Assert.False(CodeText.IsBinary([]));
    }

    [Fact]
    public void IsBinary_AcceptsUtf16BecauseItHoldsNulBytes()
    {
        Assert.False(CodeText.IsBinary([0xFF, 0xFE, (byte)'a', 0]));
        Assert.False(CodeText.IsBinary([0xFE, 0xFF, 0, (byte)'a']));
        Assert.True(CodeText.IsBinary([(byte)'a', 0, (byte)'b', 0]));
    }

    [Theory]
    [InlineData("", 0)]
    [InlineData("a", 1)]
    [InlineData("a\n", 1)]
    [InlineData("a\nb", 2)]
    [InlineData("a\nb\n", 2)]
    [InlineData("a\n\n", 2)]
    [InlineData("\n", 1)]
    public void SplitLines_DoesNotCountTheEndOfTheLastLine(string content, int count) =>
        Assert.Equal(count, CodeText.SplitLines(content).Count);

    [Theory]
    [InlineData("Scene.cs", "csharp")]
    [InlineData("src/App/App.CSPROJ", "msbuild")]
    [InlineData("a.vbproj", "msbuild")]
    [InlineData("a.fsproj", "msbuild")]
    [InlineData("Directory.Build.props", "msbuild")]
    [InlineData("a.targets", "msbuild")]
    [InlineData("A.sln", "solution")]
    [InlineData("A.slnx", "solution")]
    [InlineData("A.slnf", "solution")]
    [InlineData("global.json", "json")]
    [InlineData("a.xml", "xml")]
    [InlineData("a.xsd", "xml")]
    [InlineData("a.xaml", "xml")]
    [InlineData("a.resx", "xml")]
    [InlineData("app.config", "xml")]
    [InlineData("a.yaml", "yaml")]
    [InlineData("a.yml", "yaml")]
    [InlineData("a.toml", "toml")]
    [InlineData("README.md", "markdown")]
    [InlineData("a.markdown", "markdown")]
    [InlineData("a.glsl", "glsl")]
    [InlineData("a.vert", "glsl")]
    [InlineData("a.frag", "glsl")]
    [InlineData("a.comp", "glsl")]
    [InlineData("a.geom", "glsl")]
    [InlineData("a.tesc", "glsl")]
    [InlineData("a.tese", "glsl")]
    [InlineData("a.shader", "glsl")]
    [InlineData("a.hlsl", "hlsl")]
    [InlineData("a.metal", "metal")]
    [InlineData("a.ps1", "powershell")]
    [InlineData("a.psm1", "powershell")]
    [InlineData("a.psd1", "powershell")]
    [InlineData("a.sh", "shell")]
    [InlineData("a.bash", "shell")]
    [InlineData("a.py", "python")]
    [InlineData("a.js", "javascript")]
    [InlineData("a.mjs", "javascript")]
    [InlineData("a.cjs", "javascript")]
    [InlineData("a.ts", "typescript")]
    [InlineData("a.tsx", "typescript")]
    [InlineData("a.c", "c")]
    [InlineData("a.h", "c")]
    [InlineData("a.cpp", "cpp")]
    [InlineData("a.cc", "cpp")]
    [InlineData("a.hpp", "cpp")]
    [InlineData("a.hh", "cpp")]
    [InlineData("a.sql", "sql")]
    [InlineData(".editorconfig", "ini")]
    [InlineData("a.ini", "ini")]
    [InlineData("a.axe", "text")]
    [InlineData("Makefile", "text")]
    [InlineData("a.", "text")]
    public void FromPath_MapsTheExtensions(string path, string language) =>
        Assert.Equal(language, CodeLanguages.FromPath(path));

    [Theory]
    [InlineData("json", true)]
    [InlineData("xml", true)]
    [InlineData("yaml", true)]
    [InlineData("toml", true)]
    [InlineData("csharp", false)]
    [InlineData("msbuild", false)]
    public void IsConfig_NamesTheDataLanguages(string language, bool expected) =>
        Assert.Equal(expected, CodeLanguages.IsConfig(language));
}
