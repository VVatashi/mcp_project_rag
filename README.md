# ProjectRag

ProjectRag is a local knowledge base of technical and project documentation. Agents search it through MCP or through a web API. A dashboard shows the sources, the runs of the indexer and the search.

The knowledge base reads Markdown, plain text and PDF files from folders on this machine. Every file becomes Markdown first.
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

A source is a folder with the documentation of one project. Its name, for example `axion`, lets an agent search one project.
The default patterns read `**/*.md`, `**/*.markdown`, `**/*.txt`, `**/*.rst`, `**/*.adoc` and `**/*.pdf`. They skip `.git`, `node_modules`, `bin`, `obj`, `artifacts` and `.venv`.

Each document has a category, a topic such as `csharp` or `vulkan`. One folder may hold the documentation of several topics, so the category belongs to the document.
The rules of a source give the categories: a pattern of the paths and a category, the first matching rule wins. A document without a matching rule takes the name of its source.
A change of the rules gives the stored documents their new categories at once, without a new read of the files.

```json
"categoryRules": [
  { "pattern": "ECMA-33*.pdf", "category": "csharp" },
  { "pattern": "vkspec.pdf", "category": "vulkan" }
]
```

The indexer runs over a new or changed source at once and over all sources every 10 minutes. A run does these steps:

1. It skips each file with the stored size and time.
2. It turns each changed file into Markdown and splits it into chunks along its sections. A chunk keeps code, a formula or a table whole, and it splits a long paragraph between sentences.
3. It gives a new chunk the vector of a stored chunk with the same text.
4. It deletes the documents of the files that are gone. A folder without matching files keeps its documents, and the run gives a warning.
5. It embeds the chunks without a vector in rounds of 128. A chunk that the server rejects stays without a vector, and the full text search still finds it.

A file that the indexer cannot read counts as failed, and the run goes on. The state of each run is on the pages Status and Sources.

A Pascal data center GPU embeds about 1200 tokens per second. The three OpenGL specifications in `data/` give about 3000 chunks and took about 18 minutes the first time.

## Connect an agent

The MCP endpoint is `http://127.0.0.1:5180/mcp`. It uses the transport Streamable HTTP. For Claude Code, run this command:

```bash
claude mcp add --transport http project-rag http://127.0.0.1:5180/mcp
```

Other clients take a JSON entry:

```json
{ "mcpServers": { "project-rag": { "type": "http", "url": "http://127.0.0.1:5180/mcp" } } }
```

| Tool | What it does |
| --- | --- |
| `list_categories` | Lists the categories of the documents, such as `csharp` or `vulkan`, with their counts |
| `list_sources` | Lists the sources with their descriptions and counts |
| `search` | Searches the chunks. It takes a query, the categories, the names of sources and a number of hits |
| `find_documents` | Finds documents by a part of the path or the title, with tolerance for typos |
| `read_document` | Reads the text of a document from an offset. A hit gives the offset in `charStart` |
| `read_chunk` | Reads a chunk by its `chunkId`: its section, its pages, the previous and the next chunk, its links with their targets and the chunks that link to it |
| `index_source` | Puts a source into the queue of the indexer |

When `Server:Token` holds a token, the client sends the header `Authorization: Bearer <token>`. For Claude Code, add `--header "Authorization: Bearer <token>"`.

The token is empty by default. The server listens on 127.0.0.1 and rejects a foreign Host header, so web pages cannot reach it.
Any local program can still call it, also a program of another user of this machine. Such a program can add any folder that the server can read as a source and read its files.
On a shared machine, set a token in `appsettings.Local.json`.

## Web API

| Method and path | What it does |
| --- | --- |
| `GET /api/status` | The state of the database, the models, the counts and the indexer |
| `GET /api/sources` | The sources |
| `POST /api/sources` | Adds a source: `name`, `description`, `rootPath`, `includeGlobs`, `excludeGlobs`, `categoryRules` |
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

## Configuration

The server reads `src/ProjectRag.Server/appsettings.json`. Put the settings of one machine into `appsettings.Local.json` next to it. Git ignores that file.

| Option | Default | Meaning |
| --- | --- | --- |
| `Urls` | `http://127.0.0.1:5180` | The address of the server |
| `ConnectionStrings:ProjectRag` | port 5434, user `project_rag` | The database. The password and the port must match `.env` |
| `Server:Token` | empty | The bearer token of `/api` and `/mcp`. Empty turns the check off |
| `Embedding:BaseUrl` | `http://127.0.0.1:8195` | The embedding server |
| `Embedding:Model` | `Qwen3-Embedding-4B-Q8_0` | The name of the model. A new name starts a new set of vectors |
| `Embedding:QueryTemplate` | an English instruction | The template of a query. `{text}` marks the query |
| `Embedding:BatchSize`, `Embedding:Parallelism` | 4, 2 | The texts in one request and the requests in flight |
| `Rerank:Enabled` | `true` | The search orders its hits with the reranker |
| `Rerank:BaseUrl` | `http://127.0.0.1:8196` | The reranker |
| `Rerank:Candidates` | 30 | The candidates that the reranker orders |
| `Search:DefaultLimit`, `Search:MaxLimit` | 8, 50 | The number of hits |
| `Search:Pool` | 100 | The candidates of the vector and of the full text search before the fusion |
| `Search:RrfK`, `Search:VectorWeight` | 60, 0.8 | The fusion of the ranks |
| `Search:QueryTimeoutSeconds` | 20 | The time limit of the embedding of a query. After it the search uses the full text search only |
| `Chunking:MaxChars` | 1800 | The size of a chunk. A change reads every file again |
| `Chunking:AtomicMaxChars` | 3000 | The size of a chunk with code, a formula or a table, which splits only beyond it |
| `Chunking:OverlapChars` | 300 | The most characters that a chunk repeats from the previous one when a block splits |
| `Chunking:MinChars` | 400 | A shorter chunk at the end of a section takes in the next sections of the third level or deeper: its subsections and its siblings. 0 keeps each chunk in one section |
| `Chunking:MaxFileBytes` | 64 MB | The size of the largest file |
| `Indexing:ScanIntervalMinutes` | 10 | The interval of the scans of all sources. 0 turns them off |

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
- The formulas of TeX become LaTeX: scripts, fractions, large operators with their limits, cases and the symbols of CMSY.
- The Markdown holds the service marks `<!-- rag:page N -->` and `<!-- rag:anchor name -->`. The chunks leave them out and keep the pages and the anchors as metadata.
- A link `[text](#anchor)`, a link to another file of the source and a mention such as "section 8.14" become links between chunks.

The dashboard shows a chunk next to its page of the original PDF file, and it lists the formulas of a document with the errors of KaTeX.
Each view of the dashboard has its own address, such as `/chunks/12`. A link to an anchor opens its place in the text, such as `/documents/4/text#p101-0`.
The script `check-math` renders every formula of the knowledge base with KaTeX and fails on an error:

```bash
npm --prefix src/ProjectRag.Dashboard run check-math -- --api http://127.0.0.1:5180
```
