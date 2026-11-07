using ProjectRag.Core.Code.Graph;

namespace ProjectRag.Core.Tests.Code;

// Returns a graph that the test built by hand, and counts the calls.
// An update asks the scripted patcher for a patch. Without a patcher the extractor keeps no workspace, so the builder falls back to a full build.
internal sealed class FakeCodeGraphExtractor : IIncrementalCodeGraphExtractor
{
    public static readonly string LongLine = "        string text = \"x\"; // " + new string('y', 500) + " SceneHost";

    public int Version { get; set; } = 1;

    public int Calls { get; private set; }

    public CodeGraphInput? LastInput { get; private set; }

    public Exception? Failure { get; set; }

    public CodeGraph Graph { get; set; } = SampleGraph.Create();

    public int UpdateCalls { get; private set; }

    public CodeGraphUpdate? LastUpdate { get; private set; }

    public Func<CodeGraphUpdate, CodeGraphPatch?>? Patcher { get; set; }

    public Exception? UpdateFailure { get; set; }

    public List<string> Released { get; } = [];

    public Task<CodeGraph> ExtractAsync(CodeGraphInput input, CancellationToken cancellationToken)
    {
        Calls++;
        LastInput = input;
        return Failure is null ? Task.FromResult(Graph) : Task.FromException<CodeGraph>(Failure);
    }

    public Task<CodeGraphPatch?> UpdateAsync(CodeGraphUpdate update, CancellationToken cancellationToken)
    {
        UpdateCalls++;
        LastUpdate = update;
        if (UpdateFailure is not null)
            return Task.FromException<CodeGraphPatch?>(UpdateFailure);

        return Task.FromResult(Patcher?.Invoke(update));
    }

    public void Release(string rootPath) => Released.Add(rootPath);
}

// A code base of two projects: Demo.Core with a partial type and an interface hierarchy, and Demo.Core.Tests with a test.
internal static class SampleGraph
{
    public const int Scene = 2;
    public const int SceneHost = 4;
    public const int ProjectCore = 0;

    public const string SceneFile = "src/Core/Scene.cs";
    public const string HostFile = "src/Core/SceneHost.cs";
    public const string SynchronizerFile = "src/Core/SceneSynchronizer.cs";
    public const string TestFile = "tests/Core.Tests/SceneTests.cs";

    // The files of the code base. The lines that a reference points at hold the text that the tests check.
    public static IReadOnlyDictionary<string, string> Files { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Demo.slnx"] = "<Solution>\n  <Project Path=\"src/Core/Core.csproj\" />\n  <Project Path=\"tests/Core.Tests/Core.Tests.csproj\" />\n</Solution>\n",
        ["src/Core/Core.csproj"] = "<Project Sdk=\"Microsoft.NET.Sdk\" />\n",
        ["tests/Core.Tests/Core.Tests.csproj"] = "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <ItemGroup>\n    <Compile Include=\"..\\..\\src\\Core\\SceneHost.cs\" />\n  </ItemGroup>\n</Project>\n",
        ["src/Core/IScene.cs"] = "namespace Demo;\n\n/// <summary>A scene of a level.</summary>\npublic interface IScene\n{\n    void Update(double time);\n}\n",
        ["src/Core/Scene.cs"] = "namespace Demo;\n\n/// <summary>The base of the scenes.</summary>\npublic abstract class Scene : IScene\n{\n    public virtual void Update(double time)\n    {\n    }\n}\n",
        ["src/Core/SceneHost.cs"] = "namespace Demo;\n\n/// <summary>Runs a scene. It has several overloads.</summary>\npublic sealed partial class SceneHost : Scene\n{\n    public override void Update(double time)\n    {\n    }\n\n    public void Reset()\n    {\n    }\n\n    public void Reset(bool all)\n    {\n    }\n}\n",
        ["src/Core/SceneHost.Sync.cs"] = "namespace Demo;\n\npublic sealed partial class SceneHost\n{\n    public void Sync()\n    {\n    }\n}\n",
        ["src/Core/SceneSynchronizer.cs"] = "namespace Demo;\n\npublic sealed class SceneSynchronizer\n{\n    public void Run(SceneHost host)\n    {\n        var copy = new SceneHost();\n        host.Update(1.0);\n        host.Sync();\n    }\n}\n",
        ["src/Core/Long.cs"] = "namespace Demo;\n\n" + FakeCodeGraphExtractor.LongLine + "\n",
        ["tests/Core.Tests/SceneTests.cs"] = "namespace Demo;\n\npublic sealed class SceneTests\n{\n    public void Updates()\n    {\n        new SceneHost().Update(2.0);\n    }\n}\n",
        ["README.md"] = "# Demo\n\nA demo code base.\n",
    };

    public static CodeGraph Create()
    {
        GraphProject core = new("src/Core/Core.csproj", "Demo.Core", "net10.0", false, [], 6, 0);
        GraphProject tests = new("tests/Core.Tests/Core.Tests.csproj", "Demo.Core.Tests", "net10.0", true, [0], 1, 0);
        int longColumn = FakeCodeGraphExtractor.LongLine.LastIndexOf("SceneHost", StringComparison.Ordinal) + 1;

        GraphSymbol[] symbols =
        [
            NewType(0, null, "interface", "Demo.IScene", "public interface IScene", "A scene of a level. It updates once per frame."),
            NewMember(0, 0, "method", "Demo.IScene.Update", "M:Demo.IScene.Update(System.Double)", "void Update(double time)", "Updates the scene."),
            NewType(0, null, "class", "Demo.Scene", "public abstract class Scene : IScene", "The base of the scenes.", "abstract"),
            NewMember(0, 2, "method", "Demo.Scene.Update", "M:Demo.Scene.Update(System.Double)", "public virtual void Update(double time)", null, "virtual"),
            NewType(0, null, "class", "Demo.SceneHost", "public sealed partial class SceneHost : Scene", "Runs a scene. It has several overloads.", "sealed", "partial"),
            NewMember(0, 4, "method", "Demo.SceneHost.Update", "M:Demo.SceneHost.Update(System.Double)", "public override void Update(double time)", null, "override"),
            NewMember(0, 4, "method", "Demo.SceneHost.Sync", "M:Demo.SceneHost.Sync", "public void Sync()", "Sends the scene to the other side."),
            NewType(0, null, "class", "Demo.SceneSynchronizer", "public sealed class SceneSynchronizer", null, "sealed"),
            NewMember(0, 7, "method", "Demo.SceneSynchronizer.Run", "M:Demo.SceneSynchronizer.Run(Demo.SceneHost)", "public void Run(SceneHost host)", null),
            NewType(1, null, "class", "Demo.SceneTests", "public sealed class SceneTests", null, "sealed"),
            NewMember(1, 9, "method", "Demo.SceneTests.Updates", "M:Demo.SceneTests.Updates", "public void Updates()", null),
            NewMember(0, 4, "method", "Demo.SceneHost.Reset", "M:Demo.SceneHost.Reset", "public void Reset()", null),
            NewMember(0, 4, "method", "Demo.SceneHost.Reset", "M:Demo.SceneHost.Reset(System.Boolean)", "public void Reset(bool all)", null),
            NewType(0, null, "class", "Demo.Ghost", "public class Ghost", null),
        ];

        GraphDeclaration[] declarations =
        [
            new(0, "src/Core/IScene.cs", 3, 7, 4, 18),
            new(1, "src/Core/IScene.cs", 6, 6, 6, 10),
            new(2, "src/Core/Scene.cs", 3, 9, 4, 23),
            new(3, "src/Core/Scene.cs", 6, 8, 6, 25),
            new(4, "src/Core/SceneHost.cs", 3, 17, 4, 29),
            new(4, "src/Core/SceneHost.Sync.cs", 3, 8, 3, 29),
            new(5, "src/Core/SceneHost.cs", 6, 8, 6, 26),
            new(6, "src/Core/SceneHost.Sync.cs", 5, 7, 5, 17),
            new(7, "src/Core/SceneSynchronizer.cs", 3, 11, 3, 21),
            new(8, "src/Core/SceneSynchronizer.cs", 5, 10, 5, 17),
            new(9, "tests/Core.Tests/SceneTests.cs", 3, 9, 3, 21),
            new(10, "tests/Core.Tests/SceneTests.cs", 5, 8, 5, 17),
            new(11, "src/Core/SceneHost.cs", 10, 12, 10, 17),
            new(12, "src/Core/SceneHost.cs", 14, 16, 14, 17),
            new(13, "missing/Ghost.cs", 1, 1, 1, 14),
        ];

        GraphReference[] references =
        [
            new(SceneHost, "src/Core/SceneSynchronizer.cs", 5, 21, 9, CodeUseKinds.Signature, 8),
            new(SceneHost, "src/Core/SceneSynchronizer.cs", 7, 24, 9, CodeUseKinds.Create, 8),
            new(5, "src/Core/SceneSynchronizer.cs", 8, 14, 6, CodeUseKinds.Call, 8),
            new(6, "src/Core/SceneSynchronizer.cs", 9, 14, 4, CodeUseKinds.Call, 8),
            new(SceneHost, "tests/Core.Tests/SceneTests.cs", 7, 13, 9, CodeUseKinds.Create, 10),
            new(5, "tests/Core.Tests/SceneTests.cs", 7, 25, 6, CodeUseKinds.Call, 10),
            new(SceneHost, "src/Core/Long.cs", 3, longColumn, 9, CodeUseKinds.Type, null),
            new(SceneHost, "missing/Ghost.cs", 1, 1, 9, CodeUseKinds.Type, 13),
            new(Scene, "src/Core/SceneHost.cs", 4, 41, 5, CodeUseKinds.Inherit, 4),
        ];

        return new CodeGraph(
            "10.0.300",
            [core, tests],
            [new GraphSolution("Demo.slnx", [0, 1])],
            [
                new GraphFileProject("src/Core/IScene.cs", 0, true),
                new GraphFileProject("src/Core/Scene.cs", 0, true),
                new GraphFileProject("src/Core/SceneHost.cs", 0, true),
                new GraphFileProject("src/Core/SceneHost.cs", 1, false),
                new GraphFileProject("src/Core/SceneHost.Sync.cs", 0, true),
                new GraphFileProject("src/Core/SceneSynchronizer.cs", 0, true),
                new GraphFileProject("src/Core/Long.cs", 0, true),
                new GraphFileProject("tests/Core.Tests/SceneTests.cs", 1, true),
                new GraphFileProject("missing/Ghost.cs", 0, true),
            ],
            symbols,
            declarations,
            references,
            [
                new GraphTypeEdge(2, 0, CodeUseKinds.Implement, 1, SceneFile),
                new GraphTypeEdge(4, 2, CodeUseKinds.Inherit, 1, HostFile),
                new GraphTypeEdge(7, 4, CodeUseKinds.Signature, 1, SynchronizerFile),
                new GraphTypeEdge(7, 4, CodeUseKinds.Create, 1, SynchronizerFile),
                new GraphTypeEdge(7, 4, CodeUseKinds.Call, 2, SynchronizerFile),
                new GraphTypeEdge(9, 4, CodeUseKinds.Create, 1, TestFile),
                new GraphTypeEdge(9, 4, CodeUseKinds.Call, 1, TestFile),
                new GraphTypeEdge(4, 7, CodeUseKinds.Type, 1, HostFile),
                new GraphTypeEdge(13, 4, CodeUseKinds.Type, 1, "missing/Ghost.cs"),
            ],
            [
                new GraphExternalEdge(7, "System.Collections.Generic.List<T>", "System.Collections", CodeUseKinds.Type, 4, SynchronizerFile),
                new GraphExternalEdge(7, "System.Collections.Generic.List<T>", "System.Collections", CodeUseKinds.Signature, 2, SynchronizerFile),
                new GraphExternalEdge(7, "System.String", "System.Runtime", CodeUseKinds.Type, 1, SynchronizerFile),
            ],
            [
                new GraphMemberLink(5, 3, "override", HostFile),
                new GraphMemberLink(3, 1, "implement", SceneFile),
            ],
            [
                new GraphDiagnostic(1, "warning", "NU1701", "The package was restored with another framework.", null, null),
                new GraphDiagnostic(null, "error", "MSB4025", "The project failed to load.", "src/Core/Core.csproj", 1),
            ]);
    }

    private static GraphSymbol NewType(int project, int? parent, string kind, string fullName, string signature, string? summary, params string[] modifiers) =>
        new(project, parent, kind, fullName[(fullName.LastIndexOf('.') + 1)..], fullName, "T:" + fullName, signature, fullName[..fullName.LastIndexOf('.')], "public", modifiers, summary);

    private static GraphSymbol NewMember(int project, int parent, string kind, string fullName, string docId, string signature, string? summary, params string[] modifiers) =>
        new(project, parent, kind, fullName[(fullName.LastIndexOf('.') + 1)..], fullName, docId, signature, fullName[..fullName[..fullName.LastIndexOf('.')].LastIndexOf('.')], "public", modifiers, summary);
}
