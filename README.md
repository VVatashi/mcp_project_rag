# ProjectRag

ProjectRag is a local knowledge base of technical and project documentation. Agents search it through MCP or through a web API. A dashboard shows the sources, the runs of the indexer and the search.

The knowledge base reads Markdown, plain text, PDF and HTML files from folders on this machine, and web pages by their addresses. Every document becomes Markdown first.
A PDF file keeps its headings, code, tables, footnotes and links, and its formulas become LaTeX. The knowledge base splits each document along its sections and stores the chunks with their pages and links in PostgreSQL.
The search combines vectors of a local embedding model with the full text search of PostgreSQL. A local reranker orders the hits.

The decisions and their reasons are in [docs/ADR/0001-architecture.md](docs/ADR/0001-architecture.md) and [docs/ADR/0002-import-pipeline.md](docs/ADR/0002-import-pipeline.md).

## How it works

```
 folders on the host ──► indexer ──► PostgreSQL 18 + pgvector + pg_trgm (Docker)
                           │                    ▲
                           ▼                    │
              llama-server: embeddings ◄── search ──► llama-server: reranker
              Qwen3-Embedding-4B, large GPU    │      Qwen3-Reranker-0.6B, small GPU
                                               ▼
                         ProjectRag.Server on 127.0.0.1:5180
                         /mcp (agents) · /api (web API) · / (dashboard)
```

| Part | Where | What it does |
| --- | --- | --- |
| `src/ProjectRag.Core` | .NET library | Reads files, splits them into chunks, calls llama.cpp, writes and searches the database |
| `src/ProjectRag.Server` | ASP.NET Core | Hosts the web API, the MCP endpoint, the dashboard and the indexer in the background |
| `src/ProjectRag.Dashboard` | Vite, React, TypeScript, SCSS | The dashboard. Its build goes to `src/ProjectRag.Server/wwwroot` |
| `tests/ProjectRag.Core.Tests` | xUnit v3 | Tests of the chunker, the readers, the clients, the indexer and the search |
| `compose.yaml`, `db/` | Docker Compose | PostgreSQL 18.6 on Alpine with pgvector 0.8.7 and pg_trgm |
| `tools/start-llama.ps1` | PowerShell | Starts a llama.cpp server for the embeddings or the reranker |
| `tools/connect-mcp.ps1` | PowerShell | Adds the MCP server to a client, such as Claude Code, Codex or Cursor |
| `tools/mcp-stdio.mjs` | Node.js | Connects a client that starts only local commands, such as Claude Desktop, to the MCP endpoint |

## Requirements

- .NET SDK 10.0.300 or later.
- Docker Desktop with Docker Compose.
- Node.js 22.12 or later, for the dashboard.
- llama.cpp with CUDA.
- The models `Qwen3-Embedding-4B-Q8_0.gguf` and `qwen3-reranker-0.6b-q8_0.gguf` of ggml-org, in the folders `Qwen3-Embedding-4B-GGUF` and `Qwen3-Reranker-0.6B-GGUF` of one models folder.

## Start

1. Start the database:

   ```bash
   docker compose up -d --build
   ```

2. Copy `tools/llama.example.json` to `tools/llama.local.json`. Set the folder of llama.cpp, the folder of the models and the GPU of each server. Git ignores this file.

3. Start the embedding server in its own terminal. Give it the GPU with at least 8 GB of memory:

   ```bash
   pwsh tools/start-llama.ps1 -Role embedding
   ```

4. Start the reranker in another terminal:

   ```bash
   pwsh tools/start-llama.ps1 -Role rerank
   ```

5. Build the dashboard:

   ```bash
   npm --prefix src/ProjectRag.Dashboard ci
   ```

   ```bash
   npm --prefix src/ProjectRag.Dashboard run build
   ```

6. Start the server. It applies the migrations of the database first:

   ```bash
   dotnet run --project src/ProjectRag.Server
   ```

7. Open http://127.0.0.1:5180 and add a source on the page Sources.

The server starts without the llama.cpp servers. The search then uses the full text search only, and the indexer writes the chunks without vectors.
The next run of the indexer embeds them. The page Status shows the state of each service.

## Sources and the indexer

A source is a folder with the documentation of one project, web pages, or both. Its name, for example `axion`, lets an agent search one project.
The default patterns read `**/*.md`, `**/*.markdown`, `**/*.txt`, `**/*.rst`, `**/*.adoc`, `**/*.pdf`, `**/*.html` and `**/*.htm`. They skip `.git`, `node_modules`, `bin`, `obj`, `artifacts` and `.venv`.
A source reads files of DocBook 5 when its patterns name them, for example `OpenGL-Refpages/gl4/*.xml`.

The field `urls` of a source lists web pages by their addresses. The address of a page is the path of its document, so the rules of the categories match it too.
A source of web pages only leaves `rootPath` empty. The indexer requests a page again after `Indexing:PageRefreshHours`, 24 hours by default.
A page that does not load keeps its last version in the search, and the run gives a warning.
A web page links to other pages by their full addresses, and these links stay out of the links between chunks.

```json
"urls": ["https://registry.khronos.org/KTX/specs/2.0/ktxspec.v2.html"],
"categoryRules": [{ "pattern": "**/ktxspec.*.html", "category": "ktx" }]
```

Each document has a category, a topic such as `csharp` or `vulkan`. One folder may hold the documentation of several topics, so the category belongs to the document.
The rules of a source give the categories: a pattern of the paths and a category, the first matching rule wins. A document without a matching rule takes the name of its source.
A change of the rules gives the stored documents their new categories at once, without a new read of the files.

```json
"categoryRules": [
  { "pattern": "ECMA-33*.pdf", "category": "csharp" },
  { "pattern": "vkspec.pdf", "category": "vulkan" }
]
```

A published file may hold errors of its own. The Metal specification shows `sparse_color<Tv>` as `sparse_color-Tv` in the PDF itself.
The replacements of a source fix such errors: a pattern of the files, a regular expression of .NET and its replacement.
The indexer applies them to the Markdown of each matching file before the chunks. They change the text between the marks of pages and anchors, never the marks.
A change of the replacements reads only the matching files again. A replacement that runs longer than 2 seconds fails its file with a warning.

```json
"replacements": [
  { "pattern": "Metal-Shading-Language-Specification.pdf", "find": "sparse_color-(Tv?)\\b", "replace": "sparse_color<$1>" }
]
```

The indexer runs over a new or changed source at once and over all sources every 10 minutes. A run does these steps:

1. It skips each file with the stored size and time.
2. It turns each changed file into Markdown and splits it into chunks along its sections. A chunk keeps code, a formula or a table whole, and it splits a long paragraph between sentences.
3. It gives a new chunk the vector of a stored chunk with the same text.
4. It deletes the documents of the files that are gone. A folder without matching files keeps its documents, and the run gives a warning.
5. It embeds the chunks without a vector in rounds of 128. A chunk that the server rejects stays without a vector, and the full text search still finds it.

A file that the indexer cannot read counts as failed, and the run goes on. The state of each run is on the pages Status and Sources.

### Code sources

A source with `"kind": "code"` is a local code base. It needs a `rootPath` and no `urls`. The kind of a source cannot change.
The folder must be a work tree of Git or a plain folder. In a work tree the file list comes from `git ls-files --cached --others --exclude-standard`, so it follows every rule of Git.
In a plain folder, or when Git fails, the indexer walks the folder and reads the `.gitignore` files itself. The walk does not enter a folder with its own `.git`.
Both lists skip `bin`, `obj` and `node_modules`. They skip every path with a segment that starts with a dot, unless a pattern of `dotGlobs` matches the path.

| Field | Default for a code source |
| --- | --- |
| `includeGlobs` | `**/*` |
| `excludeGlobs` | `**/artifacts/**`, the lock files of npm, `**/*.min.js`, `**/*.min.css`, `**/*.map` |
| `dotGlobs` | `.github/**/*.yml`, `.github/**/*.yaml`, `.editorconfig`, `.config/*.json` |

The indexer keeps the exact text of each file as a document with a language, such as `csharp` or `json`. It skips binary files and large files, see `Code:MaxFileBytes` and `Code:MaxConfigFileBytes`.
A Markdown file goes through the pipeline of the documents, so `search` finds it. The other files have no chunks and no vectors. Only the exact search reads them.

Two MCP tools read code sources:

| Tool | What it does |
| --- | --- |
| `code_grep` | Finds a text or a regular expression of .NET. It takes `codebase`, `regex`, `caseSensitive`, `path`, `language` and `limit`. It answers with the lines of each file |
| `code_read` | Reads lines of a file: `codebase`, `path`, `startLine` and `endLine`. It gives 200 lines by default and 500 at most |

The web API has the same functions in `GET /api/code/grep` and `GET /api/code/lines`.

#### Watcher and updates of the code graph

The server watches the folder of each code source and starts a run of the indexer after a burst of changes. The watcher skips `.git`, `bin`, `obj`, `node_modules`, `artifacts` and dot folders.
It waits `Watch:QuietMilliseconds` without a change, but no longer than `Watch:MaxDelaySeconds`. If the system loses events, the watcher asks for a run at once.
The scan of `Indexing:ScanIntervalMinutes` stays as a safety net. A folder that the watcher cannot follow gets a new try at the next refresh.

After a run, the code graph updates in place when all of these hold:

1. The graph has no error, and the extractor version and the build properties are unchanged.
2. The run only changed the content of `.cs` files that exist already.
3. The run changed no project, solution, `Directory.Packages.props`, `global.json` or `nuget.config`.
4. The change reaches at most `Code:IncrementalMaxFiles` files. These are the changed files and the files that refer to their symbols.

Otherwise, or if the update fails, the run builds the whole graph. The status of the graph shows the last full build, the last update and the number of updates since.

A Pascal data center GPU embeds about 1200 tokens per second. The three OpenGL specifications in `data/` give about 3000 chunks and took about 18 minutes the first time.

## Connect a client

The MCP endpoint is `http://127.0.0.1:5180/mcp`. It uses the transport Streamable HTTP and keeps no sessions, so a client works on after a restart of the server.
The best search needs both llama.cpp servers. The embedding server turns the query into a vector, and the reranker orders the hits.

The file `.mcp.json` connects Claude Code in this repository. The script `tools/connect-mcp.ps1` adds the server to a client for all its projects:

```bash
pwsh tools/connect-mcp.ps1 -Client claude-code
```

| Client | What the script does |
| --- | --- |
| `claude-code` | Runs `claude mcp add --scope user` |
| `codex` | Runs `codex mcp add` |
| `gemini` | Runs `gemini mcp add --scope user` |
| `claude-desktop` | Writes `claude_desktop_config.json` with the bridge `tools/mcp-stdio.mjs` |
| `cursor` | Writes `~/.cursor/mcp.json` |
| `vscode` | Writes the file `mcp.json` of the user profile |
| `windsurf` | Writes `~/.codeium/windsurf/mcp_config.json` |
| `json` | Prints the entries for any other client and changes nothing |

`-DryRun` prints the command or the new entry and changes nothing. The script keeps an old JSON settings file with the extension `.bak`. Restart the client after the change.

A client that starts only local commands, such as Claude Desktop, needs the bridge `tools/mcp-stdio.mjs`. The bridge reads MCP messages from stdin and sends them to the endpoint. It needs Node.js and no packages:

```json
{ "mcpServers": { "project-rag": { "command": "node", "args": ["<repository>/tools/mcp-stdio.mjs"], "env": { "PROJECT_RAG_URL": "http://127.0.0.1:5180/mcp" } } } }
```

A client with Streamable HTTP takes this entry:

```json
{ "mcpServers": { "project-rag": { "type": "http", "url": "http://127.0.0.1:5180/mcp" } } }
```

| Tool | What it does |
| --- | --- |
| `list_categories` | Lists the categories of the documents, such as `csharp` or `vulkan`, with their counts |
| `list_sources` | Lists the sources with their kinds, descriptions and counts |
| `search` | Searches the chunks. It takes a query, the categories, the names of sources and a number of hits. A text that several places share appears once, and the hit names the other places as copies. A copy may differ in a few words, and its similarity below 1 shows that |
| `find_documents` | Finds documents by a part of the path or the title, with tolerance for typos |
| `read_document` | Reads the text of a document from an offset. A hit gives the offset in `charStart` |
| `read_chunk` | Reads a chunk by its `chunkId`: its section, its pages, the previous and the next chunk, its links with their targets and the chunks that link to it |
| `index_source` | Puts a source into the queue of the indexer |

When `Server:Token` holds a token, the client sends the header `Authorization: Bearer <token>`. The script takes the token from `-Token` or from the variable `PROJECT_RAG_TOKEN`.
The bridge and Codex read the token from `PROJECT_RAG_TOKEN` of their environment. The other clients keep the token as plain text in their settings.

The token is empty by default. The server listens on 127.0.0.1 and rejects a foreign Host header, so web pages cannot reach it.
Any local program can still call it, also a program of another user of this machine. Such a program can add any folder that the server can read as a source and read its files.
On a shared machine, set a token in `appsettings.Local.json`.

## Web API

| Method and path | What it does |
| --- | --- |
| `GET /api/status` | The state of the database, the models, the counts and the indexer |
| `GET /api/sources` | The sources |
| `POST /api/sources` | Adds a source: `name`, `kind`, `description`, `rootPath`, `includeGlobs`, `excludeGlobs`, `dotGlobs`, `categoryRules`, `replacements` |
| `PUT /api/sources/{id}` | Changes a source |
| `DELETE /api/sources/{id}` | Deletes a source with its documents. The files stay |
| `POST /api/sources/{id}/index` | Puts a source into the queue of the indexer |
| `GET /api/runs?sourceId=&limit=` | The last runs of the indexer |
| `GET /api/categories` | The categories of the documents with their counts |
| `GET /api/documents?sourceId=&category=&q=&offset=&limit=` | The documents. `q` matches a part of the path or the title |
| `GET /api/documents/{id}` | A document |
| `GET /api/documents/{id}/content?offset=&length=` | A part of the text of a document, up to 100000 characters |
| `GET /api/documents/{id}/chunks?sectionId=&offset=&limit=` | A page of the chunks of a document, up to 500, with the counts |
| `GET /api/documents/{id}/boundaries` | The offsets where a view of the text can start: the starts of the chunks that start a block |
| `GET /api/documents/{id}/formulas` | The display formulas of a document with their pages |
| `GET /api/documents/{id}/sections` | The sections of a document with their numbers and pages |
| `GET /api/documents/{id}/anchors/{name}` | An anchor of a document: its offset and the chunk at it |
| `GET /api/documents/{id}/file` | The original file |
| `GET /api/chunks/{id}` | A chunk with its section, pages, neighbors, links and backlinks |
| `POST /api/search` | The search: `query`, `categories`, `sources`, `limit`, `rerank` |
| `GET /api/code/grep?pattern=&sourceId=&regex=&caseSensitive=&path=&language=&limit=` | The exact search in the code sources: `matches`, `filesMatched`, `truncated`, `elapsedMs`. A wrong regular expression gives 400 |
| `GET /api/code/lines?documentId=&start=&end=` | Lines of a file of a code source, up to 2000 |

## Configuration

The server reads `src/ProjectRag.Server/appsettings.json`. Put the settings of one machine into `appsettings.Local.json` next to it. Git ignores that file.

| Option | Default | Meaning |
| --- | --- | --- |
| `Urls` | `http://127.0.0.1:5180` | The address of the server |
| `ConnectionStrings:ProjectRag` | port 5434, user `project_rag` | The database. The password and the port must match `.env` |
| `Server:Token` | empty | The bearer token of `/api` and `/mcp`. Empty turns the check off |
| `Embedding:BaseUrl` | `http://127.0.0.1:8195` | The embedding server |
| `Embedding:Model` | `Qwen3-Embedding-4B-Q8_0` | The name of the model. A new name starts a new set of vectors |
| `Embedding:QueryTemplate` | an English instruction | The template of a query of the documentation search. `{text}` marks the query |
| `Embedding:CodeQueryTemplate` | an English instruction | The template of a query of `code_search`. The templates of the queries do not change the stored vectors |
| `Embedding:BatchSize`, `Embedding:Parallelism` | 4, 2 | The texts in one request and the requests in flight |
| `Rerank:Enabled` | `true` | The search orders its hits with the reranker |
| `Rerank:BaseUrl` | `http://127.0.0.1:8196` | The reranker |
| `Rerank:Candidates` | 30 | The candidates that the reranker orders |
| `Search:DefaultLimit`, `Search:MaxLimit` | 8, 50 | The number of hits |
| `Search:Pool` | 100 | The candidates of the vector and of the full text search before the fusion |
| `Search:RrfK`, `Search:VectorWeight` | 60, 0.8 | The fusion of the ranks |
| `Search:QueryTimeoutSeconds` | 20 | The time limit of the embedding of a query. After it the search uses the full text search only |
| `Search:HiddenCategories` | `["opengl-old"]` | The categories that a search without categories skips, such as the old versions of a specification. A request that names one searches it |
| `Chunking:MaxChars` | 1800 | The size of a chunk. A change reads every file again |
| `Chunking:AtomicMaxChars` | 3000 | The size of a chunk with code, a formula or a table, which splits only beyond it. A digit in such a chunk counts as three characters |
| `Chunking:OverlapChars` | 300 | The most characters that a chunk repeats from the previous one when a block splits |
| `Chunking:MinChars` | 400 | A shorter chunk at the end of a section takes in the next small sections of the third level or deeper: its subsections and its siblings. A section that fits whole with its subsections under `MaxChars` becomes one chunk. A short text under a title also takes in the small subsections that follow it. 0 keeps each chunk in one section |
| `Chunking:MaxFileBytes` | 64 MB | The size of the largest file |
| `Indexing:ScanIntervalMinutes` | 10 | The interval of the scans of all sources. 0 turns them off |
| `Indexing:PageRefreshHours` | 24 | The hours between two requests of a web page |
| `Code:GitPath` | `git` | The Git program that lists the files of a code source |
| `Code:GitTimeoutSeconds` | 60 | The time limit of a Git command |
| `Code:MaxFileBytes` | 1 MB | The largest file of a code source. The indexer skips a larger file |
| `Code:MaxConfigFileBytes` | 256 KB | The largest JSON, XML, YAML or TOML file of a code source |
| `Code:GraphTimeoutMinutes` | 15 | The time limit of the build of a code graph |
| `Code:IncrementalMaxFiles` | 300 | The most files that one incremental update of the code graph may read. A larger change builds the whole graph. 0 turns updates off |
| `Watch:Enabled` | true | Watch the folders of the code sources for changes |
| `Watch:RefreshSeconds` | 60 | The interval at which the watcher reads the list of sources |
| `Watch:QuietMilliseconds` | 2000 | The time without changes that ends a burst and starts a run |
| `Watch:MaxDelaySeconds` | 30 | The longest wait from the first change of a burst to the run |
| `CodeGraph:MsBuildPath` | empty | The folder of MSBuild for the code graph. Empty takes the newest installed .NET SDK |
| `CodeGraph:MaxDegreeOfParallelism` | half the processors | The projects that the code graph reads at once |
| `CodeChunking:MaxChars` | 2400 | The size of a chunk of code. A change splits every code file again |
| `CodeChunking:MinChars` | 400 | Neighbor members of one type merge into one chunk below this size |
| `CodeChunking:OverlapLines` | 2 | The lines that a part of a long member repeats from the part before it |

## Development

The build treats warnings as errors and checks the style of `.editorconfig`. The style follows the project Axion.

```bash
dotnet build ProjectRag.slnx
```

The tests of the database need PostgreSQL. They drop and create the database named in `PROJECTRAG_TEST_DATABASE`, so that database must not be the database of the application.
Without the variable they skip themselves.

```bash
PROJECTRAG_TEST_DATABASE="Host=127.0.0.1;Port=5434;Database=project_rag_test;Username=project_rag;Password=project_rag" dotnet test --project tests/ProjectRag.Core.Tests
```

```bash
dotnet format ProjectRag.slnx --verify-no-changes --severity warn
```

The dashboard has a dev server with hot reload on http://127.0.0.1:5173. It sends `/api` to the running server.

```bash
npm --prefix src/ProjectRag.Dashboard run dev
```

```bash
npm --prefix src/ProjectRag.Dashboard run lint
```

## The import of documents

Every file becomes Markdown, the intermediate form of the pipeline. The details are in [docs/ADR/0002-import-pipeline.md](docs/ADR/0002-import-pipeline.md).

- A PDF file goes through a layout analysis of its glyphs. The bookmarks give the headings, monospace lines give code, rules give tables, and the link annotations give links.
- An HTML file or a web page keeps its headings, lists, tables, code and links to its anchors. The formulas of MathJax stay LaTeX, also with the delimiter `$` when the page sets it. A formula that KaTeX drew on the server gets its LaTeX back from its boxes. The navigation, the table of contents and the scripts go away.
- A file of DocBook 5, such as a reference page of OpenGL, keeps its sections, lists, tables and the synopsis of its functions. Its formulas of MathML become LaTeX.
  A reference to another page becomes a link to its file. The entities of the DTD and the parts of XInclude come from the folder of the file and its subfolders.
- The formulas of TeX become LaTeX: scripts, fractions, large operators with their limits, cases and the symbols of CMSY.
- The Markdown holds the service marks `<!-- rag:page N -->` and `<!-- rag:anchor name -->`. The chunks leave them out and keep the pages and the anchors as metadata.
- A link `[text](#anchor)`, a link to another file of the source and a mention such as "section 8.14" become links between chunks.

The dashboard shows a chunk next to its page of the original PDF file, and it lists the formulas of a document with the errors of KaTeX.
Each view of the dashboard has its own address, such as `/chunks/12`. A link to an anchor opens its place in the text, such as `/documents/4/text#p101-0`.
The script `check-math` renders every formula of the knowledge base with KaTeX and fails on an error:

```bash
npm --prefix src/ProjectRag.Dashboard run check-math -- --api http://127.0.0.1:5180
```
