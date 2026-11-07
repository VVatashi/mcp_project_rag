using ProjectRag.Core.Code;

namespace ProjectRag.Core.Tests.Code;

public sealed class CodeLineMatcherTests
{
    private const string Sample = "using System;\npublic class Scene\n{\n    int scene = 1;\n}\n";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Create_RejectsAPatternWithANulCharacter(bool isRegex)
    {
        CodeSearchException error = Assert.Throws<CodeSearchException>(() => CodeLineMatcher.Create("a\0b", isRegex, true));

        Assert.Equal("Pattern", error.Field);
    }

    [Fact]
    public void Find_GivesTheLineTheColumnAndTheLengthOfALiteral()
    {
        LineMatch match = Assert.Single(CodeLineMatcher.Create("Scene", false, true).Find(Sample));

        Assert.Equal(new LineMatch(2, 14, 5, "public class Scene"), match);
    }

    [Fact]
    public void Find_IgnoresTheCaseOnRequest()
    {
        LineMatch[] matches = [.. CodeLineMatcher.Create("SCENE", false, false).Find(Sample)];

        Assert.Equal([2, 4], matches.Select(static match => match.Line));
        Assert.Equal([14, 9], matches.Select(static match => match.Column));
        Assert.Empty(CodeLineMatcher.Create("SCENE", false, true).Find(Sample));
    }

    [Fact]
    public void Find_TakesTheFirstMatchOfALine()
    {
        LineMatch match = Assert.Single(CodeLineMatcher.Create("ab", false, true).Find("ab ab ab\n"));

        Assert.Equal(1, match.Column);
    }

    [Fact]
    public void Find_CountsTheColumnInUtf16Units()
    {
        LineMatch match = Assert.Single(CodeLineMatcher.Create("x", false, true).Find("\U0001F600 x\n"));

        Assert.Equal(4, match.Column);
    }

    [Fact]
    public void Find_MatchesARegularExpressionPerLine()
    {
        LineMatch match = Assert.Single(CodeLineMatcher.Create(@"class\s+(\w+)", true, true).Find(Sample));

        Assert.Equal(2, match.Line);
        Assert.Equal(8, match.Column);
        Assert.Equal("class Scene".Length, match.Length);
    }

    [Fact]
    public void Find_AnchorsARegularExpressionToTheLine()
    {
        LineMatch[] matches = [.. CodeLineMatcher.Create("^}$", true, true).Find(Sample)];
        LineMatch[] insensitive = [.. CodeLineMatcher.Create("^using", true, false).Find("USING x\nusing y\n  using z\n")];

        Assert.Equal([5], matches.Select(static match => match.Line));
        Assert.Equal([1, 2], insensitive.Select(static match => match.Line));
    }

    [Fact]
    public void Find_ReadsALastLineWithoutALineEndAndNoEmptyLineAfterTheLastEnd()
    {
        Assert.Equal([2], CodeLineMatcher.Create("b", false, true).Find("a\nb").Select(static match => match.Line));
        Assert.Empty(CodeLineMatcher.Create("^$", true, true).Find("a\nb\n"));
        Assert.Empty(CodeLineMatcher.Create("a", false, true).Find(string.Empty));
    }

    [Fact]
    public void Find_CutsALongLineAroundTheMatchAndKeepsTheColumn()
    {
        string line = new string('a', 600) + "needle" + new string('b', 600);

        LineMatch match = Assert.Single(CodeLineMatcher.Create("needle", false, true).Find(line + "\n"));

        Assert.Equal(601, match.Column);
        Assert.Equal(CodeLineMatcher.MaxLineText, match.Text.Length);
        Assert.Equal("needle", match.Text.Substring(match.Column - 1 - match.TextStart, match.Length));
    }

    [Fact]
    public void Find_KeepsTheStartOfALongLineForAMatchNearTheStart()
    {
        string line = "needle" + new string('b', 600);

        LineMatch match = Assert.Single(CodeLineMatcher.Create("needle", false, true).Find(line));

        Assert.Equal((1, 0, CodeLineMatcher.MaxLineText), (match.Column, match.TextStart, match.Text.Length));
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("a\nb", false)]
    [InlineData("a\rb", true)]
    [InlineData("(", true)]
    [InlineData("[a-", true)]
    public void Create_RejectsAnEmptyPatternALineEndAndABadExpression(string pattern, bool isRegex)
    {
        CodeSearchException exception = Assert.Throws<CodeSearchException>(() => CodeLineMatcher.Create(pattern, isRegex, true));

        Assert.Equal("Pattern", exception.Field);
    }

    [Fact]
    public void Create_AcceptsParenthesesInALiteral()
    {
        Assert.Single(CodeLineMatcher.Create("Foo(", false, true).Find("Foo(1)\n"));
    }

    [Fact]
    public void Find_StopsARunawayExpressionWithAnError()
    {
        CodeLineMatcher matcher = CodeLineMatcher.Create("^(a+)+$", true, true);
        string text = new string('a', 60) + "!\n";

        CodeSearchException exception = Assert.Throws<CodeSearchException>(() => matcher.Find(text).ToList());

        Assert.Equal("Pattern", exception.Field);
    }

    [Theory]
    [InlineData("50%", "50\\%")]
    [InlineData("a_b", "a\\_b")]
    [InlineData("a\\b", "a\\\\b")]
    [InlineData("plain", "plain")]
    public void EscapeLike_EscapesTheMarksOfLike(string text, string expected) =>
        Assert.Equal(expected, CodeSearchStore.EscapeLike(text));
}
