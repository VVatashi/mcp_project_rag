using ProjectRag.Core.Code;

namespace ProjectRag.Core.Tests.Code;

public sealed class GitIgnoreTests
{
    // The rules are lines of one file joined with '|'. The cases come from the documentation of gitignore.
    [Theory]
    [InlineData("*.log", "a.log", false, true)]
    [InlineData("*.log", "dir/a.log", false, true)]
    [InlineData("*.log", "a.logx", false, false)]
    [InlineData("foo/", "foo", true, true)]
    [InlineData("foo/", "foo", false, false)]
    [InlineData("foo/", "a/foo", true, true)]
    [InlineData("foo/", "foo/bar.txt", false, true)]
    [InlineData("/foo", "foo", false, true)]
    [InlineData("/foo", "a/foo", false, false)]
    [InlineData("foo", "a/b/foo", false, true)]
    [InlineData("doc/frotz/", "doc/frotz", true, true)]
    [InlineData("doc/frotz/", "a/doc/frotz", true, false)]
    [InlineData("doc/frotz", "a/doc/frotz", false, false)]
    [InlineData("foo/*", "foo/test.json", false, true)]
    [InlineData("foo/*", "foo/bar", true, true)]
    [InlineData("foo/*", "bar/foo/test.json", false, false)]
    [InlineData("**/foo", "foo", false, true)]
    [InlineData("**/foo", "a/b/foo", false, true)]
    [InlineData("**/foo/bar", "foo/bar", false, true)]
    [InlineData("**/foo/bar", "a/foo/bar", false, true)]
    [InlineData("**/foo/bar", "foo/baz", false, false)]
    [InlineData("abc/**", "abc/x", false, true)]
    [InlineData("abc/**", "abc/x/y", false, true)]
    [InlineData("abc/**", "abc", true, false)]
    [InlineData("abc/**", "x/abc/y", false, false)]
    [InlineData("a/**/b", "a/b", false, true)]
    [InlineData("a/**/b", "a/x/b", false, true)]
    [InlineData("a/**/b", "a/x/y/b", false, true)]
    [InlineData("a/**/b", "ab", false, false)]
    [InlineData("a**b", "axxb", false, true)]
    [InlineData("a**b", "a/b", false, false)]
    [InlineData("a*b", "a/b", false, false)]
    [InlineData("a*b", "axyb", false, true)]
    [InlineData("f?o", "foo", false, true)]
    [InlineData("f?o", "fo", false, false)]
    [InlineData("f?o", "f/o", false, false)]
    [InlineData("[a-c]x", "bx", false, true)]
    [InlineData("[a-c]x", "dx", false, false)]
    [InlineData("[!a-c]x", "dx", false, true)]
    [InlineData("[!a-c]x", "bx", false, false)]
    [InlineData("[^a-c]x", "dx", false, true)]
    [InlineData("[[:digit:]].txt", "5.txt", false, true)]
    [InlineData("[[:digit:]].txt", "a.txt", false, false)]
    [InlineData("[]x]", "]", false, true)]
    [InlineData("a[b", "a[b", false, true)]
    [InlineData("#comment", "#comment", false, false)]
    [InlineData(@"\#file", "#file", false, true)]
    [InlineData(@"\!important", "!important", false, true)]
    [InlineData("foo  ", "foo", false, true)]
    [InlineData(@"foo\ ", "foo ", false, true)]
    [InlineData(@"foo\ ", "foo", false, false)]
    [InlineData("", "foo", false, false)]
    [InlineData("*.log|!keep.log", "keep.log", false, false)]
    [InlineData("*.log|!keep.log", "other.log", false, true)]
    [InlineData("!keep.log|*.log", "keep.log", false, true)]
    [InlineData("build/|!build/keep.txt", "build/keep.txt", false, true)]
    [InlineData("/*|!/foo", "foo", true, false)]
    [InlineData("/*|!/foo", "bar", false, true)]
    [InlineData(@"a\*b", "a*b", false, true)]
    [InlineData(@"a\*b", "axb", false, false)]
    public void IsIgnored_FollowsTheRulesOfGitignore(string rules, string path, bool isDirectory, bool expected)
    {
        GitIgnore ignore = GitIgnore.Parse(rules.Split('|'));

        Assert.Equal(expected, ignore.IsIgnored(path, isDirectory));
    }

    [Fact]
    public void Match_ReportsANegatedRuleAndNoRule()
    {
        GitIgnore ignore = GitIgnore.Parse(["*.log", "!keep.log"]);

        Assert.True(ignore.Match("a.log", false));
        Assert.False(ignore.Match("keep.log", false));
        Assert.Null(ignore.Match("a.cs", false));
    }

    [Fact]
    public void Match_DoesNotLookAtTheParents()
    {
        GitIgnore ignore = GitIgnore.Parse(["foo/"]);

        Assert.Null(ignore.Match("foo/bar.txt", false));
        Assert.True(ignore.IsIgnored("foo/bar.txt", false));
    }

    [Fact]
    public void Parse_SkipsTheMarkOfTheEncodingAndTheBlankLines()
    {
        GitIgnore ignore = GitIgnore.Parse(["﻿*.log", "", "   ", "/"]);

        Assert.True(ignore.IsIgnored("a.log", false));
        Assert.False(ignore.IsIgnored("a.cs", false));
    }
}
