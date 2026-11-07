using System.Text;

using ProjectRag.Code.Chunks;
using ProjectRag.Core.Code.Chunks;

namespace ProjectRag.Code.Tests;

public sealed class CSharpCodeChunkerTests
{
    private const string File = "src/Scene.cs";

    private static readonly CSharpCodeChunker Chunker = new();

    private static CodeChunkingOptions Options(int max = 2400, int min = 400, int overlap = 2) =>
        new() { MaxChars = max, MinChars = min, OverlapLines = overlap };

    private static IReadOnlyList<CodeChunk> Split(string content, CodeChunkingOptions? options = null)
    {
        options ??= Options();
        IReadOnlyList<CodeChunk> chunks = Chunker.Split(File, content, options);
        ChunkAssertions.AssertInvariants(File, content, chunks, options);
        return chunks;
    }

    private static string Body(string content, CodeChunk chunk) => content[chunk.Start..chunk.End];

    private static string[] Headings(IEnumerable<CodeChunk> chunks) => [.. chunks.Select(chunk => chunk.Heading)];

    private static string Method(string name, int statements, string indent = "    ")
    {
        StringBuilder builder = new();
        builder.Append(indent).Append("public int ").Append(name).Append("(int value)\n");
        builder.Append(indent).Append("{\n");
        builder.Append(indent).Append("    int total = value;\n");
        for (int index = 0; index < statements; index++)
            builder.Append(indent).Append("    total += Step").Append(index).Append("(value);\n");

        builder.Append(indent).Append("    return total;\n");
        builder.Append(indent).Append("}\n");
        return builder.ToString();
    }

    [Fact]
    public void Chunker_HasVersionAndLanguage()
    {
        Assert.Equal(1, Chunker.Version);
        Assert.True(Chunker.Supports("csharp"));
        Assert.False(Chunker.Supports("text"));
        Assert.False(Chunker.Supports("CSharp"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n\n  ")]
    public void Split_BlankText_GivesNoChunks(string content) => Assert.Empty(Split(content));

    [Fact]
    public void Split_SmallType_IsOneChunkWithTheNamespaceInTheHeading()
    {
        const string content = """
            using System;

            namespace Axion.Core.Scene;

            /// <summary>The host.</summary>
            public sealed class SceneHost
            {
                private int _count;

                public void Update(double time)
                {
                    _count++;
                }
            }
            """;

        CodeChunk chunk = Assert.Single(Split(content));

        Assert.Equal("src/Scene.cs > Axion.Core.Scene.SceneHost", chunk.Heading);
        Assert.Equal(5, chunk.LineStart);
        Assert.Equal(14, chunk.LineEnd);
        Assert.StartsWith("/// <summary>The host.</summary>\npublic sealed class SceneHost", Body(content, chunk), StringComparison.Ordinal);
        Assert.EndsWith("    }\n}", Body(content, chunk), StringComparison.Ordinal);
        Assert.Equal(0, chunk.OverlapChars);
    }

    [Fact]
    public void Split_BlockNamespacesAndGenerics_JoinTheNames()
    {
        const string content = """
            namespace Axion
            {
                namespace Collections
                {
                    public class Table<TKey, TValue>
                        where TKey : notnull
                    {
                        public TValue Find(TKey key) => default!;
                    }
                }
            }
            """;

        CodeChunk chunk = Assert.Single(Split(content));

        Assert.Equal("src/Scene.cs > Axion.Collections.Table<TKey, TValue>", chunk.Heading);
    }

    [Fact]
    public void Split_LargeType_GivesAHeaderChunkAndAChunkForEachLargeMember()
    {
        string content = "namespace Axion.Scene;\n\n"
            + "/// <summary>The host of the scene with its fields.</summary>\n"
            + "public class Host\n{\n"
            + "    private int _first;\n    private int _second;\n\n"
            + Method("Alpha", 10) + "\n" + Method("Beta", 10) + "\n" + Method("Gamma", 10)
            + "}\n";

        IReadOnlyList<CodeChunk> chunks = Split(content, Options(max: 500, min: 60));

        Assert.Equal(
            [
                "src/Scene.cs > Axion.Scene.Host",
                "src/Scene.cs > Axion.Scene.Host > public int Alpha(int value)",
                "src/Scene.cs > Axion.Scene.Host > public int Beta(int value)",
                "src/Scene.cs > Axion.Scene.Host > public int Gamma(int value)",
            ],
            Headings(chunks));
        string header = Body(content, chunks[0]);
        Assert.StartsWith("/// <summary>The host", header, StringComparison.Ordinal);
        Assert.Contains("private int _second;", header, StringComparison.Ordinal);
        Assert.DoesNotContain("Alpha", header, StringComparison.Ordinal);
        Assert.StartsWith("    public int Alpha", Body(content, chunks[1]), StringComparison.Ordinal);
        Assert.EndsWith("    }", Body(content, chunks[1]), StringComparison.Ordinal);
    }

    [Fact]
    public void Split_NestedTypes_JoinTheContainersWithDots()
    {
        string content = "namespace Axion;\n\n"
            + "public class Outer\n{\n"
            + "    public class Inner\n    {\n"
            + Method("Work", 6, "        ")
            + "    }\n\n"
            + Method("Alpha", 10) + "\n" + Method("Beta", 10)
            + "}\n";

        IReadOnlyList<CodeChunk> chunks = Split(content, Options(max: 500, min: 60));

        Assert.Equal(
            [
                "src/Scene.cs > Axion.Outer",
                "src/Scene.cs > Axion.Outer.Inner",
                "src/Scene.cs > Axion.Outer > public int Alpha(int value)",
                "src/Scene.cs > Axion.Outer > public int Beta(int value)",
            ],
            Headings(chunks));
        Assert.StartsWith("    public class Inner", Body(content, chunks[1]), StringComparison.Ordinal);
        Assert.EndsWith("    }", Body(content, chunks[1]), StringComparison.Ordinal);
    }

    [Fact]
    public void Split_LargeNestedType_GivesItsOwnHeaderAndMembers()
    {
        string content = "namespace Axion;\n\n"
            + "public class Outer\n{\n"
            + "    /// <summary>The inner part of the outer type with a long description.</summary>\n"
            + "    public class Inner\n    {\n"
            + Method("Work", 10, "        ") + "\n" + Method("Rest", 10, "        ")
            + "    }\n"
            + "}\n";

        IReadOnlyList<CodeChunk> chunks = Split(content, Options(max: 600, min: 60));

        Assert.Equal(
            [
                "src/Scene.cs > Axion.Outer",
                "src/Scene.cs > Axion.Outer.Inner",
                "src/Scene.cs > Axion.Outer.Inner > public int Work(int value)",
                "src/Scene.cs > Axion.Outer.Inner > public int Rest(int value)",
            ],
            Headings(chunks));
    }

    [Fact]
    public void Split_SmallMembers_MergeAndNameTheFirstOne()
    {
        string content = "/// <summary>The host of the scene with a long description of its members.</summary>\n"
            + "public class Host\n{\n"
            + Method("Big", 14) + "\n"
            + "    public int A() => 1;\n"
            + "    public int B() => 2;\n"
            + "    public int C() => 3;\n"
            + "    public int D() => 4;\n"
            + "}\n";

        IReadOnlyList<CodeChunk> chunks = Split(content, Options(max: 540, min: 100));

        Assert.Equal(
            [
                "src/Scene.cs > Host",
                "src/Scene.cs > Host > public int Big(int value)",
                "src/Scene.cs > Host > public int A() (+3 more)",
            ],
            Headings(chunks));
        string merged = Body(content, chunks[2]);
        Assert.StartsWith("    public int A() => 1;", merged, StringComparison.Ordinal);
        Assert.EndsWith("    public int D() => 4;", merged, StringComparison.Ordinal);
    }

    [Fact]
    public void Split_SmallMembersOfDifferentTypes_DoNotMerge()
    {
        string content = "public class Outer\n{\n"
            + Method("Big", 14) + "\n"
            + "    public int A() => 1;\n"
            + "    public class Inner\n    {\n        public int B() => 2;\n    }\n"
            + "    public int C() => 3;\n"
            + "}\n";

        IReadOnlyList<CodeChunk> chunks = Split(content, Options(max: 540, min: 30));

        Assert.Equal(
            [
                "src/Scene.cs > Outer",
                "src/Scene.cs > Outer > public int Big(int value)",
                "src/Scene.cs > Outer > public int A()",
                "src/Scene.cs > Outer.Inner",
                "src/Scene.cs > Outer > public int C()",
            ],
            Headings(chunks));
    }

    [Fact]
    public void Split_MemberHeaders_DropBodiesAttributesAndDocs()
    {
        string content = "public class Host\n{\n"
            + "    private readonly List<int> _items = new() { 1, 2, 3 };\n"
            + "    public int Count { get; private set; }\n"
            + "    public int this[int index] => _items[index];\n"
            + "    public event EventHandler? Changed;\n"
            + "    public Host(int size) { }\n"
            + "    public static Host operator +(Host left, Host right) => left;\n"
            + "    /// <summary>Gets the name.</summary>\n"
            + "    [Obsolete(\"Use something else\")]\n"
            + "    [System.Diagnostics.DebuggerStepThrough]\n"
            + "    public string  Name(\n        string   first,\n        string second)\n    {\n        return first;\n    }\n"
            + "}\n";

        IReadOnlyList<CodeChunk> chunks = Split(content, Options(max: 400, min: 1));

        string[] headings = Headings(chunks);
        Assert.Contains("src/Scene.cs > Host > private readonly List<int> _items", headings);
        Assert.Contains("src/Scene.cs > Host > public int Count", headings);
        Assert.Contains("src/Scene.cs > Host > public int this[int index]", headings);
        Assert.Contains("src/Scene.cs > Host > public event EventHandler? Changed", headings);
        Assert.Contains("src/Scene.cs > Host > public Host(int size)", headings);
        Assert.Contains("src/Scene.cs > Host > public static Host operator +(Host left, Host right)", headings);
        Assert.Contains("src/Scene.cs > Host > public string Name( string first, string second)", headings);
    }

    [Fact]
    public void Split_MemberHeader_IsCutAt200Characters()
    {
        string parameters = string.Join(", ", Enumerable.Range(0, 40).Select(index => $"int parameter{index}"));
        string content = "public class Host\n{\n"
            + $"    public void Run({parameters})\n    {{\n        int a = 1;\n        int b = 2;\n    }}\n\n"
            + Method("Other", 30)
            + "}\n";

        IReadOnlyList<CodeChunk> chunks = Split(content, Options(max: 1500, min: 1));

        string heading = chunks.Select(chunk => chunk.Heading).Single(heading => heading.Contains("> public void Run", StringComparison.Ordinal));
        Assert.Equal("src/Scene.cs > Host > ".Length + 200, heading.Length);
    }

    [Fact]
    public void Split_DocCommentAndAttributes_StartTheChunk()
    {
        string content = "public class Host\n{\n"
            + Method("Alpha", 10) + "\n"
            + "    /// <summary>\n    /// Runs.\n    /// </summary>\n    [Obsolete]\n    [Flags]\n"
            + Method("Beta", 10)
            + "}\n";

        IReadOnlyList<CodeChunk> chunks = Split(content, Options(max: 500, min: 60));

        CodeChunk beta = chunks.Single(chunk => chunk.Heading.EndsWith("Beta(int value)", StringComparison.Ordinal));
        Assert.StartsWith("    /// <summary>\n    /// Runs.", Body(content, beta), StringComparison.Ordinal);
        Assert.Contains("[Obsolete]\n    [Flags]\n    public int Beta", Body(content, beta), StringComparison.Ordinal);
        Assert.Equal(1 + content[..beta.Start].Count(character => character == '\n'), beta.LineStart);
    }

    [Fact]
    public void Split_AttributesWithoutDocComment_StartTheChunk()
    {
        string content = "public class Host\n{\n"
            + Method("Alpha", 10) + "\n"
            + "    [Obsolete]\n"
            + Method("Beta", 10)
            + "}\n";

        IReadOnlyList<CodeChunk> chunks = Split(content, Options(max: 500, min: 60));

        CodeChunk beta = chunks.Single(chunk => chunk.Heading.EndsWith("Beta(int value)", StringComparison.Ordinal));
        Assert.StartsWith("    [Obsolete]\n    public int Beta", Body(content, beta), StringComparison.Ordinal);
    }

    [Fact]
    public void Split_LongMethod_SplitsAtStatementsWithOverlapAndPartNumbers()
    {
        string content = "public class Host\n{\n" + Method("Run", 60) + "}\n";
        CodeChunkingOptions options = Options(max: 600, min: 100, overlap: 2);

        IReadOnlyList<CodeChunk> all = Split(content, options);

        CodeChunk[] chunks = [.. all.Where(chunk => chunk.Heading.Contains("(part", StringComparison.Ordinal))];
        Assert.True(chunks.Length >= 3);
        Assert.Equal(all.Count - 1, chunks.Length);
        for (int index = 0; index < chunks.Length; index++)
        {
            Assert.EndsWith($"public int Run(int value) (part {index + 1})", chunks[index].Heading, StringComparison.Ordinal);
            string body = Body(content, chunks[index]);
            if (index == 0)
            {
                Assert.Equal(0, chunks[index].OverlapChars);
                Assert.StartsWith("    public int Run", body, StringComparison.Ordinal);
                continue;
            }

            string overlap = body[..chunks[index].OverlapChars];
            Assert.Equal(2, overlap.Count(character => character == '\n'));
            Assert.EndsWith(overlap.TrimEnd('\n'), Body(content, chunks[index - 1]), StringComparison.Ordinal);
            Assert.Matches(@"^\s+total \+= Step\d+\(value\);", body[chunks[index].OverlapChars..]);
        }
    }

    [Fact]
    public void Split_LongMethod_PrefersTheBoundaryOfAStatement()
    {
        StringBuilder body = new();
        for (int index = 0; index < 12; index++)
        {
            body.Append("        foreach (int item in Items)\n        {\n");
            body.Append("            Process(item, ").Append(index).Append(");\n");
            body.Append("            Log(item);\n        }\n\n");
        }

        string content = "public class Host\n{\n    public void Run()\n    {\n" + body + "    }\n}\n";

        IReadOnlyList<CodeChunk> chunks = Split(content, Options(max: 600, min: 100, overlap: 0));

        CodeChunk[] parts = [.. chunks.Where(chunk => chunk.Heading.Contains("(part", StringComparison.Ordinal))];
        Assert.True(parts.Length >= 2);
        foreach (CodeChunk part in parts.Skip(1))
            Assert.StartsWith("        foreach", Body(content, part), StringComparison.Ordinal);
    }

    [Fact]
    public void Split_ExpressionBodiedMember_SplitsAtLines()
    {
        string expression = string.Join("\n        + ", Enumerable.Range(0, 80).Select(index => $"Value{index}"));
        string content = $"public class Host\n{{\n    public int Total =>\n        {expression};\n}}\n";

        IReadOnlyList<CodeChunk> chunks = Split(content, Options(max: 300, min: 50, overlap: 1));

        CodeChunk[] parts = [.. chunks.Where(chunk => chunk.Heading.Contains("> public int Total", StringComparison.Ordinal))];
        Assert.True(parts.Length >= 3);
        for (int index = 0; index < parts.Length; index++)
            Assert.EndsWith($"public int Total (part {index + 1})", parts[index].Heading, StringComparison.Ordinal);
    }

    [Fact]
    public void Split_SingleHugeLine_SplitsByCharacters()
    {
        string literal = string.Join(" ", Enumerable.Range(0, 1500).Select(index => $"word{index}"));
        string content = $"public class Host\n{{\n    public string Text()\n    {{\n        return \"{literal}\";\n    }}\n}}\n";

        IReadOnlyList<CodeChunk> chunks = Split(content, Options(max: 500, min: 100));

        Assert.True(chunks.Count > 10);
        CodeChunk[] pieces = [.. chunks.Where(chunk => chunk.LineStart == 5 && chunk.LineEnd == 5)];
        Assert.True(pieces.Length > 10);
        Assert.All(pieces, piece => Assert.Equal(0, piece.OverlapChars));
        string joined = string.Concat(pieces.Select(piece => Body(content, piece)));
        Assert.Equal($"        return \"{literal}\";", joined);
    }

    [Fact]
    public void Split_LineNumbersAndRanges_MatchTheText()
    {
        string content = "using System;\n\nnamespace Axion;\n\n"
            + "public class Host\n{\n    private int _a;\n\n"
            + Method("Alpha", 10) + "\n" + Method("Beta", 10) + "\n" + Method("Run", 50)
            + "}\n\npublic class Other\n{\n    public int X;\n}\n";
        CodeChunkingOptions options = Options(max: 500, min: 60);

        IReadOnlyList<CodeChunk> chunks = Split(content, options);

        Assert.True(chunks.Count > 5);
        foreach (CodeChunk chunk in chunks.Where(chunk => chunk.Heading.Contains("(part", StringComparison.Ordinal) is false))
        {
            Assert.True(chunk.Start == 0 || content[chunk.Start - 1] == '\n');
            Assert.True(chunk.End == content.Length || content[chunk.End] == '\n');
            Assert.NotEqual('\n', content[chunk.End - 1]);
        }

        Assert.Equal("src/Scene.cs > Axion.Other", chunks[^1].Heading);
        Assert.Equal("public class Other\n{\n    public int X;\n}", Body(content, chunks[^1]));
    }

    [Fact]
    public void Split_TopLevelStatements_GoIntoChunksAtBlankLines()
    {
        const string content = """
            using System;
            using System.Linq;

            [assembly: CLSCompliant(true)]

            Console.WriteLine("start");
            int total = 0;

            foreach (int item in args.Select(int.Parse))
            {
                total += item;
            }

            Console.WriteLine(total);
            """;

        IReadOnlyList<CodeChunk> chunks = Split(content);

        CodeChunk chunk = Assert.Single(chunks);
        Assert.Equal("src/Scene.cs > (top level)", chunk.Heading);
        Assert.StartsWith("[assembly: CLSCompliant(true)]", Body(content, chunk), StringComparison.Ordinal);
        Assert.DoesNotContain("using System", Body(content, chunk), StringComparison.Ordinal);
        Assert.EndsWith("Console.WriteLine(total);", Body(content, chunk), StringComparison.Ordinal);
    }

    [Fact]
    public void Split_LongTopLevelCode_SplitsAtBlankLinesWithoutOverlap()
    {
        StringBuilder builder = new();
        for (int index = 0; index < 20; index++)
            builder.Append("Console.WriteLine(\"paragraph ").Append(index).Append("\");\nConsole.WriteLine(\"second line\");\n\n");

        string content = builder.ToString();

        IReadOnlyList<CodeChunk> chunks = Split(content, Options(max: 300, min: 50));

        Assert.True(chunks.Count > 3);
        Assert.All(chunks, chunk =>
        {
            Assert.Equal("src/Scene.cs > (top level)", chunk.Heading);
            Assert.Equal(0, chunk.OverlapChars);
            Assert.StartsWith("Console.WriteLine(\"paragraph", Body(content, chunk), StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Split_TypesAfterTopLevelStatements_KeepTheirOwnChunks()
    {
        const string content = """
            Run();

            void Run()
            {
            }

            public class Helper
            {
                public int X;
            }
            """;

        IReadOnlyList<CodeChunk> chunks = Split(content);

        Assert.Equal(["src/Scene.cs > (top level)", "src/Scene.cs > Helper"], Headings(chunks));
    }

    [Fact]
    public void Split_FileWithOnlyUsings_GivesNoChunks()
    {
        const string content = "global using System;\nusing System.Linq;\nusing Alias = System.Text.StringBuilder;\n";

        Assert.Empty(Split(content));
    }

    [Fact]
    public void Split_FileWithOnlyAComment_FallsBackToLines()
    {
        const string content = "using System;\n\n// A file with no declarations.\n// Second line.\n";

        CodeChunk chunk = Assert.Single(Split(content));

        Assert.Equal("src/Scene.cs > (top level)", chunk.Heading);
        Assert.Equal("// A file with no declarations.\n// Second line.", Body(content, chunk));
    }

    [Fact]
    public void Split_BrokenFile_DoesNotThrowAndKeepsTheInvariants()
    {
        string[] files =
        [
            "namespace Axion { public class A { void M( { int x = ; } ",
            "public class A\n{\n    public int M(\n    {\n        return 1\n    }\n\n    public void N() { if ( }\n",
            "class { } } } namespace ;;; ]]] public",
            "this is not code at all\nsecond line\n",
            "public class Open\n{\n" + Method("Alpha", 10) + Method("Beta", 10),
        ];

        foreach (string content in files)
            Assert.NotNull(Split(content, Options(max: 200, min: 40)));
    }

    [Fact]
    public void Split_LargeEnum_SplitsByMembers()
    {
        string members = string.Join("\n", Enumerable.Range(0, 60).Select(index => $"    /// <summary>Value {index}.</summary>\n    Value{index} = {index},"));
        string content = $"namespace Axion;\n\n/// <summary>The kinds.</summary>\npublic enum Kind\n{{\n{members}\n}}\n";

        IReadOnlyList<CodeChunk> chunks = Split(content, Options(max: 400, min: 100));

        Assert.True(chunks.Count > 3);
        Assert.Equal("src/Scene.cs > Axion.Kind", chunks[0].Heading);
        Assert.StartsWith("/// <summary>The kinds.</summary>\npublic enum Kind", Body(content, chunks[0]), StringComparison.Ordinal);
        Assert.Contains(chunks, chunk => chunk.Heading.StartsWith("src/Scene.cs > Axion.Kind > Value", StringComparison.Ordinal));
        Assert.Contains(chunks, chunk => chunk.Heading.Contains("more)", StringComparison.Ordinal));
        Assert.Contains("Value59", Body(content, chunks[^1]), StringComparison.Ordinal);
    }

    [Fact]
    public void Split_SmallEnum_IsOneChunk()
    {
        const string content = "namespace Axion;\n\npublic enum Mode\n{\n    A,\n    B,\n}\n";

        CodeChunk chunk = Assert.Single(Split(content));

        Assert.Equal("src/Scene.cs > Axion.Mode", chunk.Heading);
    }

    [Fact]
    public void Split_DelegatesAndBodylessRecords_MergeAtTheNamespaceLevel()
    {
        const string content = """
            namespace Axion;

            public delegate void Handler(int value);

            public record Point(int X, int Y);

            public delegate int Compare(int left, int right);

            public class Box
            {
                public int Value;
            }
            """;

        IReadOnlyList<CodeChunk> chunks = Split(content);

        Assert.Equal(["src/Scene.cs > Axion > public delegate void Handler(int value) (+2 more)", "src/Scene.cs > Axion.Box"], Headings(chunks));
        Assert.EndsWith("Compare(int left, int right);", Body(content, chunks[0]), StringComparison.Ordinal);
    }

    [Fact]
    public void Split_SmallTypes_StayApartAcrossTheTypeBoundary()
    {
        const string content = """
            namespace Axion;

            public class First
            {
                public int Value;
            }

            public class Second
            {
                public int Value;
            }
            """;

        IReadOnlyList<CodeChunk> chunks = Split(content);

        Assert.Equal(["src/Scene.cs > Axion.First", "src/Scene.cs > Axion.Second"], Headings(chunks));
    }

    [Fact]
    public void Split_InactiveDirectiveTextInsideAMethod_StaysInTheRange()
    {
        string content = "public class Host\n{\n"
            + "    public int Run(int value)\n    {\n"
            + "#if DEBUG\n        Console.WriteLine(\"debug\");\n#else\n        value++;\n#endif\n"
            + "        return value;\n    }\n"
            + "}\n";

        CodeChunk chunk = Assert.Single(Split(content));

        Assert.Contains("#if DEBUG", Body(content, chunk), StringComparison.Ordinal);
        Assert.Contains("Console.WriteLine(\"debug\")", Body(content, chunk), StringComparison.Ordinal);
        Assert.Contains("#endif", Body(content, chunk), StringComparison.Ordinal);
    }

    [Fact]
    public void Split_NoPartExceedsTheLimitForManyGeneratedMembers()
    {
        Random random = new(12345);
        for (int round = 0; round < 40; round++)
        {
            StringBuilder builder = new("namespace Gen.Space;\n\npublic partial class Host<T>\n{\n");
            int members = random.Next(5, 40);
            for (int index = 0; index < members; index++)
            {
                switch (random.Next(5))
                {
                    case 0:
                        builder.Append("    private int _field").Append(index).Append(" = ").Append(random.Next(1000)).Append(";\n");
                        break;
                    case 1:
                        builder.Append("    /// <summary>Property ").Append(index).Append(".</summary>\n    public int Prop").Append(index).Append(" { get; set; }\n");
                        break;
                    case 2:
                        builder.Append("    public string Text").Append(index).Append(" => \"").Append(new string('x', random.Next(1, 900))).Append("\";\n");
                        break;
                    case 3:
                        builder.Append("    [Obsolete]\n").Append(Method("Method" + index, random.Next(1, 90)));
                        break;
                    default:
                        builder.Append("    public class Nested").Append(index).Append("\n    {\n").Append(Method("Inner" + index, random.Next(1, 40), "        ")).Append("    }\n");
                        break;
                }

                if (random.Next(3) == 0)
                    builder.Append('\n');
            }

            builder.Append("}\n");
            string content = builder.ToString();
            CodeChunkingOptions options = Options(max: random.Next(120, 2401), min: random.Next(0, 500), overlap: random.Next(0, 4));

            IReadOnlyList<CodeChunk> chunks = Chunker.Split(File, content, options);

            ChunkAssertions.AssertInvariants(File, content, chunks, options);
            Assert.NotEmpty(chunks);
        }
    }
}
