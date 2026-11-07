# 0003. The index of local code bases

- Status: Proposed
- Date: 2026-11-07
- Refines: [0001](0001-architecture.md), [0002](0002-import-pipeline.md)

## Context

The user wants the knowledge base to index local code bases. A code base enters the database by its folder on the disk.
The index covers source code (`.cs` for C# and others), configuration (`.json`, `.xml`, `.yaml`, `.toml` and others) and documentation (`.md`).

The request names these properties:

1. The index respects `.gitignore` and similar files and skips dot files.
2. One folder may hold several .NET projects and several solutions with different options. Solutions may share projects. The projects of one code base are indexed together and may reference each other.
3. The index follows changes on the disk, for example with a file watcher, if that causes no problems.
4. The search has three levels, in the order of priority:
   1. A map of the code from Roslyn, for C# only at first: the types and the namespace of each file, the types that a type uses and the types that use it.
      The dashboard draws the dependencies as a readable diagram, a UML class diagram if possible. MCP gives agents the map as the static analyzer sees it: outlines, members of types and a symbol search as in an IDE.
   2. A full text search finds every exact occurrence of a string.
   3. A vector search finds similar implementations and concepts.
5. C# has first-class support. Other languages get the minimum.

The first code base is Axion. A survey of its repository on 2026-11-07 found these facts:

- 1180 tracked `.cs` files with about 345 thousand lines. 32 project files and two solutions in the `.slnx` format, with 29 and 3 projects. The solutions share one project. One project is in no solution.
- No project has several target frameworks. The properties `AxionMetal`, `AxionAndroid` and `AxionGLES` select variants. They change the target framework, the symbols of conditional compilation and the set of files.
- Test projects link files from `src` with `Compile Include="..\..\src\..."`. The folder `tests/Axion.Audio.Contract` has files of C# but no project file. Two test projects include it.
- The other files: about 100 GLSL shaders, 61 scripts in the language of Axion (`.axe`), PowerShell and shell scripts, 42 Markdown files, 21 JSON files. The largest JSON file is a font atlas of 364 KB.
- The build writes `bin` and `obj` into `artifacts` at the root. `global.json` pins the SDK 10.0.300.
- The folder `.claude/worktrees` holds two full checkouts of other branches, with 2667 `.cs` files. Only `.git/info/exclude` excludes them, not `.gitignore`.

## Analysis of the requirements

### Roslyn, not a language server

A language server answers questions about one position of a cursor. A full map through LSP needs a `documentSymbol` request for each file and a `references` request for each symbol, tens of thousands of requests.
The C# language servers use Roslyn inside. `MSBuildWorkspace` loads the projects into the process, and one pass over the semantic models gives all symbols, references and hierarchies.
So the indexer uses Roslyn directly and no language server.

### One view of the build

A semantic model belongs to one project, one target framework and one set of MSBuild properties. In Axion the default view does not see the code under `#if AXION_METAL` or the folder `Android`.
One graph cannot be correct for every variant. So a code base has one view of the build: a set of global properties, by default `Configuration=Debug` and nothing else.

The projects of a code base are not the projects of one solution. They are all project files under its root that the rules of the file list keep. So a project outside every solution enters the index too.
The solutions are metadata: the dashboard and the tools filter by them. Code in an inactive branch of `#if` stays in the full text and vector search, but not in the graph.
More views, for example a view with `AxionMetal=true`, wait for a real need.

### The files of several projects

Project references are the simple part. Roslyn resolves them, and the edges between projects come out of the semantic model without extra work.
A linked file is the hard part. One file compiles into several projects, and its types become different symbols in different assemblies.
For example, `RuntimeSettings` exists in `Axion.Runtime` and in `Axion.Core.Tests`.

So each file has an owner project: the project whose folder holds the file. The symbols and references of the file come from the compilation of its owner. The other projects of the file are a many-to-many relation.
A file outside the folder of every project, as in `Axion.Audio.Contract`, belongs to the first of its projects in the ordinal order of their paths.

### Restore and the code of MSBuild

A design-time build needs the file `project.assets.json` of each project. It also runs the targets of the repository, which is code of the repository.
`dotnet restore` writes into the repository and reads the network. So the indexer never runs restore on its own.
A project without its assets gives a diagnostic of the code base, and the user starts the restore explicitly. The indexer reads only trusted folders. This adds a reason for a token on a machine with several users.

The design-time build is not read-only either. It writes generated files, such as `AssemblyInfo.cs`, into the `obj` folder of each project. The prototype saw it rewrite such files in `artifacts/obj` of Axion.

### The SDK of the code base

`global.json` of the code base selects its SDK. MSBuild of another version may fail to load the projects.
`MSBuildLocator` lists every installed SDK and ignores `global.json`. So the indexer asks `dotnet --version` in the root of the code base and registers that SDK.

A process registers MSBuild once. All code bases of one server share one SDK, and the indexer registers the newest SDK that their `global.json` files allow.
A code base that needs an older SDK, outside its `rollForward` rule, gives a diagnostic. A worker process for each SDK solves that case if it ever happens.

`MSBuildWorkspace` 5.9 opens `.slnx` files. The indexer still opens each project file, because a project outside every solution must enter the index too.
The package `Microsoft.VisualStudio.SolutionPersistence` reads the solutions as metadata without a design-time build.

### The list of files

The rules of `.gitignore` are hard to repeat exactly. They include nested files, `.git/info/exclude` and the global excludes of the user. Axion needs `.git/info/exclude` for its worktrees.
So in a work tree of Git the list of files comes from `git ls-files --cached --others --exclude-standard`.

A folder outside Git uses a reader of `.gitignore` files of the project and these built-in rules:

- The folders `bin`, `obj`, `artifacts` and `node_modules` stay out.
- The walk does not enter a folder with its own `.git`, such as a submodule or a worktree.
- A file with a NUL byte in its first 8 KB is binary and stays out.
- A file over the size limit stays out. The default limit is 256 KB for configuration and 1 MB for code.

### Dot files

Dot files and dot folders stay out by default. Some of them are useful configuration: the workflows in `.github`, `.editorconfig` and `.config/dotnet-tools.json`.
So a code base has patterns that bring dot files back. The defaults are `.github/**/*.yml`, `.github/**/*.yaml`, `.editorconfig` and `.config/*.json`.

### The exact search and the grep of an agent

A local agent already has ripgrep, and ripgrep always sees the current files. The exact search of the index still has uses:

- An agent without access to the disk can search.
- The dashboard can search.
- The search filters by project, language and folder.
- A hit knows its symbol: the line of a match names the method around it.

The exact search uses a GIN index of pg_trgm over the content of each file. It answers `LIKE`, `ILIKE` and regular expressions and gives lines with their numbers.
A string under three characters cannot use the index. The scan of all files is fast enough at this size.

The hybrid search needs a full text search over tokens too. The configuration `russian` stems words and breaks identifiers. So code chunks get a second `tsvector` with the configuration `simple`.
The application splits identifiers before the database reads them: `SceneSynchronizer` gives `scenesynchronizer scene synchronizer`.

### Three levels need tiers, not fusion

Reciprocal rank fusion merges two lists of the same kind of item. A symbol, a line with a match and a semantic chunk are different items, and one rank over them means nothing.
So the tool `code_search` answers in tiers:

1. Symbols: an exact name, a prefix, camel humps such as `SSync` for `SceneSynchronizer`, then fuzzy names.
2. Exact matches of the text.
3. Semantic matches.

A place in the code appears once, in its highest tier. Each level also has a tool of its own, and an agent picks the level it needs.

### The diagrams

Axion has some thousands of types. A class diagram of all types is unreadable. The dashboard shows levels of scale instead:

1. The graph of projects, about 32 nodes.
2. The graph of namespaces, with the edges between types summed into weights.
3. The neighborhood of one type, depth 1 or 2, at most 40 nodes.
4. The card of one type with its members.

Test projects and external types (the base class library, NuGet packages) stay hidden by default.

PlantUML needs Java and Graphviz or a server of its own, and it gives a static picture. Mermaid lays out more than about 30 classes badly.
So the server gives the graph as JSON, and the dashboard writes DOT and draws it with Graphviz compiled to WebAssembly (`@hpcc-js/wasm-graphviz`).
A node looks like a UML class with compartments for fields and methods. A click on a node opens the type. The API also exports the graph as PlantUML and DOT text.

### The watcher

`FileSystemWatcher` has known problems:

- The buffer of events can overflow.
- `git checkout` gives thousands of events at once.
- Editors save through a temporary file and a rename.
- The build writes into `artifacts` all the time.

The design handles them:

- An event passes the rules of the file list before anything else.
- The watcher waits for 2 seconds without events and then sends a batch.
- An overflow starts a full scan. The scan is cheap, because it compares sizes and times of files.
- The text levels update each changed file.
- The graph reads the changed `.cs` files again, and the files that referenced their symbols.
- A change of a project file, a `.props` or `.targets` file, a solution or `global.json` loads the workspace again.
- The workspace stays in memory and unloads after 30 minutes without changes.

### The cost of the vectors

The chunker splits the C# of Axion into 14125 chunks with 16.9 million characters of headings and bodies, about 5.6 million tokens.
The embedding server embeds about 1200 tokens per second, so the first run takes about 80 minutes.
During that run the queries of the documentation search wait in the same queue. Later runs embed only new chunks, by the hash of their text.

The identity of an embedding model includes its query template. A code search needs another instruction, for example "retrieve code that implements the description".
A second template would create a second model and a second copy of every vector. Yet the template does not change the vector of a document.
So the query template leaves the identity of the model. The identity keeps the name, the dimensions and the document template, and each kind of search has its own query template.

### A code base is a source

A code base is a source with the kind `code`. It shares the scheduler, the runs of the indexer, the status, the token and `list_sources` with the documentation.

- A file is a `document` with its content. Reading and the trigram index need no copy of the text.
- A chunk is a `chunk` with new columns: the kind (`prose`, `code` or `config`), the first and the last line, the symbol and the second `tsvector`.
- A Markdown file of a code base goes through the pipeline of ADR 0002 and appears in the tool `search`. Chunks of code and configuration stay out of `search` by default.

## Decision

1. **A code base** is a source with the kind `code`, a view of the build (global MSBuild properties) and patterns that bring dot files back.
2. **The list of files** comes from `git ls-files` in a work tree of Git, or from the built-in rules and the `.gitignore` files elsewhere.
3. **The graph** comes from Roslyn `MSBuildWorkspace`. The indexer opens each project file of the code base. Each file has one owner project.
   The database stores projects, solutions, the projects of each file, symbols with their declarations, references and edges between types with their kinds and counts.
   The kinds of an edge are:
   - inheritance and an implemented interface
   - the type of a field, a property, a parameter or a return value
   - `new`, a call and an access to a static member
   - an attribute and a generic argument

   A reference to a symbol of the code base is a row of its own. References to external symbols only add to the edges between types.
   So the map knows that a type uses `List<T>`, but it does not list each use.
   Generated documents, such as `AssemblyInfo.cs` in `obj` and the output of source generators, take part in the semantic model but get no rows.
4. **The exact search** uses a trigram index over the content of the files. **The token search** uses a `tsvector` with the configuration `simple` over split identifiers.
5. **The chunks of code**: a C# chunk is a member of a type with the heading `Project > Namespace.Type > signature`. A small type is one chunk. A long method splits between statements.
   Other code and configuration split into windows of lines at empty lines. Markdown uses the chunker of ADR 0002.
6. **The query template** leaves the identity of an embedding model. Each kind of search has its own template.
7. **MCP** gets these tools. Each answers with compact text, relative paths and line numbers:
   - `code_find_symbol`: a symbol by name, as "Go to symbol" in an IDE.
   - `code_outline`: the outline of a file or the members of a type.
   - `code_relations`: the types that a type uses, the types that use it, the derived types, the implementations and the callers, with a depth.
   - `code_references`: every reference to a symbol.
   - `code_grep`: an exact string or a regular expression.
   - `code_search`: the search in tiers.
   - `code_read`: lines of a file.
   - `code_map`: a summary of a project or a namespace.
8. **The dashboard** draws the graphs of projects, of namespaces and of the neighborhood of a type with Graphviz in WebAssembly.
9. **The watcher** follows each code base as described above. The scan every 10 minutes stays as a safety net.
10. **The process**: the new project `ProjectRag.Code` runs inside the server. The prototype found the time and the memory acceptable, see below.

## Rejected options

- **A language server** (the Roslyn language server, csharp-ls). It answers one position at a time and needs a protocol client. Roslyn gives the same data in one pass.
- **One graph for every variant of the build.** Symbols of different variants conflict. A second view of the build is a second code base, if it is ever needed.
- **The projects of one solution.** A project outside the solutions would be lost, and overlapping solutions would load a project twice.
- **ripgrep at the time of the query.** It needs an external program and gives no filters by project or symbol. It stays the tool of a local agent.
- **PlantUML on the server.** It needs Java and Graphviz, or another container, and gives static pictures.
- **Tree-sitter or ctags for other languages.** They give outlines for other languages but need native code or an external program. The other languages wait for a real need.

## Results of the prototype

The prototype loaded Axion with `MSBuildWorkspace` on 2026-11-07. It used Microsoft.CodeAnalysis 5.9.0 and Microsoft.Build.Locator 1.11.2.
The Locator also needs a reference to `Microsoft.Build.Framework` without its runtime assets, or its build check fails.
The memory column gives the managed heap after a full collection and the working set of the process.

| Step | Time | Heap | Working set |
| --- | --- | --- | --- |
| `OpenSolutionAsync` on `Axion.slnx`, 29 projects | 7 s | 10 MB | 118 MB |
| `OpenProjectAsync` on each project file, 32 projects | 28 s | 11 MB | 167 MB |
| The compilations of all projects, 16 in parallel | 14 s | 358 MB | 662 MB |
| The walk over all symbols and references, 16 projects in parallel | 23 s | 363 MB | 863 MB |

The workspace reported no diagnostics, and the compilations had no errors. So the default view of the build is complete, and the graph of the first version takes about a minute.

The counts of the walk:

| Measure | Count |
| --- | --- |
| Documents, including 120 generated files in `artifacts/obj` | 1376 |
| Files in more than one project | 50 |
| Types (2346 classes, 481 structs, 219 enums, 89 interfaces, 13 delegates) | 3148 |
| Members: methods, constructors, properties, fields and events | 32142 |
| References to symbols of the code base | 514919 |
| References to external symbols | 162963 |
| Unresolved references | 1666 |
| Distinct edges between types of the code base | 23536 |

The test project `Axion.Core.Tests` links all files of `Axion.Editor`. The owner rule keeps their symbols in `Axion.Editor`.

The prototype edited one file of median size in `Axion.Core` three times. `WithDocumentText` took at most 7 ms.
The new semantic model took 9 to 74 ms, and the walk over its references took 24 to 64 ms.
`SymbolFinder.FindReferencesAsync` for a type with about 2200 references takes 0.3 to 2 s the first time and 10 to 30 ms after that.
So the incremental update of the graph is cheap, and the watcher can keep the graph current within seconds.

A workspace with the global property `AxionMetal=true` loaded `Axion.Runtime` with 12 projects in 4 s and compiled it without errors. Its file set had the extra file of the Metal variant. So the view of the build works through global properties.

After the run `git status` of Axion did not change. The number of files in `artifacts/obj` did not change, but some generated files there got new write times.

## Results of stages 1 and 2

The server indexed Axion on 2026-11-07 without the embedding server.

| Measure | Value |
| --- | --- |
| Files in the list of Git, without the worktrees | 1622 |
| Documents, without binary files and the font atlas | 1500 |
| Run of the files | 14 s |
| Exact search, including a regular expression over all files | 70 to 200 ms |
| Graph: projects, symbols, references | 32, 34818, 297611 |
| Graph: extraction and write | 61 s and 3.3 s |
| Tools of the graph | 40 to 440 ms |

The exact search found the same lines and files as `git grep`. The graph has fewer references than the prototype, because locals and parameters are not symbols of the graph.

Stage 3 was checked on a part of Axion: the folders `Scene`, `Physics`, `Animation` and `ECS` of `Axion.Core` with the project files.

| Measure | Value |
| --- | --- |
| Files, chunks with vectors | 140, 1350 |
| Run with the vectors | 7 min |
| The C# chunker over all 1180 files of Axion | 1.6 to 3.3 s, 14125 chunks |
| `code_search` without the reranker | about 0.2 s |
| `code_search` with the reranker | 2.5 to 3.8 s |

The questions "destroy an entity and remove all its components", "cast a ray against the physics colliders" and "blend the transforms between two simulation snapshots" found `World.Destroy`, `IPhysicsQueries.RayCast` and `SceneSynchronizer.Sync` among the first two hits.
The order without the reranker was the same in these checks. A gold set of questions is still missing.

Stage 4 was checked on the whole graph of Axion. The four views answer in 20 to 250 ms.
The first version drew every edge between the shown nodes. The neighborhood of the hub type `World` had 154 edges, and the namespaces of `Axion.Core` had 235 edges on a canvas of 3144 by 4710 points. Neither was readable.
So the type view keeps only the edges of the focus, the inheritance between the shown nodes and one edge from each deeper node to the node that found it. The namespace views keep the 60 heaviest edges.
The dashboard also runs `unflatten` of Graphviz, which spreads a wide fan of nodes over several rows. After these changes the neighborhood of `World` has 40 edges, and the namespaces of `Axion.Core` fit into 1270 by 1453 points.

Stage 5 was checked on a copy of the tracked files of Axion, outside Git, so the walk with the reader of `.gitignore` listed the files. It found the same 1622 files as `git ls-files`.

| Measure | Value |
| --- | --- |
| A full build of the graph with the workspace kept | 44 to 59 s |
| From a save of `World.cs` to the updated graph, with the wait of the watcher | 5.6 s, of which the update took 3.0 s |
| A rename of a method in three files of three projects, one batch | 1.8 s for the update, 0.1 s for the write |
| The update of `Entity.cs`, used in 172 files, measured in the extractor | 5.5 s |
| The memory of the server with the kept workspace | about 1.7 GB of working set |

After the edit of `World.cs` and after the rename, the rows of all six tables of the graph matched a full build exactly. The check compared hashes of normalized rows: symbols, declarations, references, type edges, external edges and member links.

The large tables of the graph have no foreign keys. A check of the keys cost about 25 s for 515 thousand references, against 4 s without them.
Two triggers on `document` and `source` delete the rows of the graph instead.

## Consequences

- The graph shows the default view of the build. Code under an inactive `#if` appears in the text search only.
- A symbol of a linked file belongs to its owner project. The other projects of the file see it, but the map does not repeat it.
- A code base without restored assets has no graph until the user starts the restore.
- The first run of a large code base keeps the embedding server busy for more than an hour.
- Prose and code share the HNSW index of a model. The documentation search filters the kind after the walk of the index, with an iterative scan. A separate index for each kind needs the kind on `chunk_embedding`.
- The workspace of Axion holds about 360 MB of managed heap and about 860 MB of working set while it stays loaded.
- The design-time build rewrites generated files in the `obj` folders of the code base. A build of the user at the same time may compete for these files.
- One server uses one SDK for all code bases.
- An update counts the compiler errors of the owner projects of the changed files only. An error that a change causes in another project shows after the next full build.
- An update reads the changed files and the files that refer to their symbols. A file that had an unresolved name, which a change now declares, keeps its old rows until the next full build.
- A failed graph keeps the old graph. The same input builds again after an hour, and a change builds at the next run.
- The migration of the model identity changes the unique key of `embedding_model`. The stored vectors stay.

## Next steps

1. Add the code base as a source, the list of files, the exact search, `code_grep` and `code_read`.
2. Add the graph of Roslyn and the tools `code_find_symbol`, `code_outline`, `code_relations`, `code_references` and `code_map`. At first any change builds the whole graph again.
3. Add the chunker of code, the vectors, the query templates and `code_search`.
4. Add the diagrams to the dashboard.
5. Add the watcher and the incremental update of the graph.
6. Collect a gold set of questions over Axion and measure R@k of each tier.
