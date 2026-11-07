using ProjectRag.Core.Code;
using ProjectRag.Core.Code.Chunks;

namespace ProjectRag.Core.Tests.Code;

public sealed class IdentifierTokensTests
{
    [Theory]
    [InlineData("SceneSynchronizer", "scenesynchronizer scene synchronizer")]
    [InlineData("_softGPU", "softgpu soft gpu")]
    [InlineData("HTTPClient", "httpclient http client")]
    [InlineData("snake_case_name", "snakecasename snake case name")]
    [InlineData("__init__", "init")]
    [InlineData("Update", "update")]
    [InlineData("GL_TEXTURE_2D", "gltexture2d gl texture 2 d")]
    [InlineData("Vector3Normalize", "vector3normalize vector3 normalize")]
    [InlineData("x = 42;", "x 42")]
    [InlineData("a.b(c)", "a b c")]
    [InlineData("int fooBar = Baz;", "int foobar foo bar baz")]
    [InlineData("СценаХост", "сценахост сцена хост")]
    [InlineData("", "")]
    [InlineData("{ } ;", "")]
    public void Split_GivesTheWholeNameAndItsParts(string text, string expected) =>
        Assert.Equal(expected, IdentifierTokens.Split(text));

    [Fact]
    public void Split_KeepsTheOrderAndTheRepeats()
    {
        Assert.Equal("scene host scene", IdentifierTokens.Split("scene host scene"));
    }

    [Fact]
    public void Terms_DropsRepeatsShortTokensAndCommonWords()
    {
        Assert.Equal(["scenehost", "scene", "host", "created"], IdentifierTokens.Terms("Where is the SceneHost created? SceneHost!"));
    }

    [Fact]
    public void Terms_KeepsEverythingWhenNothingElseIsLeft()
    {
        Assert.Equal(["how", "to"], IdentifierTokens.Terms("how to"));
        Assert.Empty(IdentifierTokens.Terms("... ;;"));
    }

    [Fact]
    public void Terms_StopsAfter32Tokens()
    {
        string query = string.Join(' ', Enumerable.Range(1, 100).Select(static i => $"word{i}"));

        Assert.Equal(32, IdentifierTokens.Terms(query).Count);
    }

    [Fact]
    public void TsQuery_JoinsQuotedTokensWithOrAndDropsOperators()
    {
        Assert.Equal("'scene' | 'ab' | 'xy'", CodeChunkStore.TsQuery(["scene", "a'b", "x;y", "scene", "&|"]));
        Assert.Equal(string.Empty, CodeChunkStore.TsQuery([]));
    }
}
