# 0001. The architecture of the knowledge base

- Status: Accepted
- Date: 2026-10-31

## Context

The user wants a local knowledge base of technical and project documentation. Agents query it through a local web API and through MCP. A dashboard shows its state.

The request names these constraints:

- The backend uses C#. The dashboard uses TypeScript, SCSS and React.
- The code follows the style of the project Axion.
- PostgreSQL runs in Docker Compose on a slim or Alpine image, with vector and full text search extensions.
- The database access uses Npgsql, Dapper and FluentMigrator. Entity Framework Core needs a reason.
- Python and ONNX need a reason. The application calls a local llama.cpp server over its standard API.
- The project rag already solved many questions of retrieval. Its decisions apply here.
- Three local builds of llama.cpp exist, and a folder of GGUF models.
- The machine has two GPUs. Large models run on a data center GPU with 24 GB. Very small models run on a desktop GPU with 8 GB for speed.

## Analysis of the requirements

### The documents are PDF files too

The folder `data` holds three PDF specifications of OpenGL and GLSL, about 1300 pages. The request names Markdown documentation only by implication.
A knowledge base of technical documentation without PDF support misses the most common format of specifications. So the reader supports PDF from the start.

PdfPig reads PDF in managed code under the Apache 2.0 license. It needs no Python, no ONNX and no native library. The reader turns the bookmarks of a PDF file into Markdown headings, so a specification splits along its chapters like a Markdown file.

### Docker cannot run the models

Docker Desktop gives a container the GPU through WSL2. A data center GPU in the TCC mode of the Windows driver is not visible there. The project rag found the same limit (its ADR 0046).
So Compose runs PostgreSQL only. The llama.cpp servers and the application run on the host. The application on the host also reads the folders of the sources without volume mounts.

### No official slim image of PostgreSQL exists

The official image has Debian and Alpine tags only. The image `pgvector/pgvector` is Debian and weighs more than 400 MB.
The Dockerfile builds pgvector 0.8.7 on `postgres:18.6-alpine3.24` in a second stage. The image weighs 304 MB. The build turns LLVM off, because the clang packages of Alpine change versions and break the build of the JIT bitcode.

### The choice of the extensions

| Extension | Use | Decision |
| --- | --- | --- |
| pgvector | The `vector` and `halfvec` types and the HNSW index | Installed |
| pg_trgm | The fuzzy search of paths and titles, for the tool `find_documents` | Installed |
| built-in FTS | `tsvector` with the configuration `russian` | Used |
| pg_search (ParadeDB, BM25) | A better rank than `ts_rank` | Rejected for now |
| unaccent | Accent folding | Rejected |

The configuration `russian` stems Cyrillic words with the Russian Snowball stemmer and Latin words with the English one. So one configuration serves Russian and English documents and mixed text.

pg_search gives BM25, but it needs a Rust toolchain and pgrx for the build, and ParadeDB does not support musl. The project rag measured that the hybrid search with `ts_rank` and the reranker is good enough (its ADR 0047 and 0052).

unaccent maps "й" to "и" in its default rules. That changes Russian stems, so the extension stays out.

### The build of llama.cpp

| Build | Version | Notes |
| --- | --- | --- |
| A | 0.5.0, build 11146, Clang | The newest official build. CPU variants for each instruction set and CUDA 12 |
| B | 0.4.0, build 10809, Clang | The build of the measurements of the project rag. Older |
| C | 0.2.0, build 10709, MSVC | A fork for the ternary models of Bonsai. The oldest build |

The decision is the build A. It knows `--embedding`, `--pooling`, `--rerank` and `--cache-ram`, and it lists both GPUs.
The folders of the build and of the models belong to one machine. `tools/start-llama.ps1` reads them from `tools/llama.local.json`, which Git ignores.

### The models and the GPUs

The embedding model is Qwen3-Embedding-4B in Q8_0 with 2560 dimensions. The project rag measured it against the 0.6B model on 1000 questions (its ADR 0052).
The vector R@5 grew from 0.47 to 0.67, and R@10 grew from 0.60 to 0.79. The reranker on top of the 4B model added about 4 points of R@1.
It needs about 6 GB of video memory, so it runs on the data center GPU. The desktop GPU also drives the display, and the project rag saw an embedding server crash there 36 times in half an hour.

The reranker is Qwen3-Reranker-0.6B in the GGUF of ggml-org. The file holds the classifier tensor and the template of the reranker. It needs about 2 GB and runs on the desktop GPU, as the request asks for small models.

The data center GPU, a Pascal card, embeds about 1200 tokens per second with this model. The three specifications give about 3000 chunks and took about 18 minutes. The indexer embeds only new chunks after that.

### Entity Framework Core and the other dependencies

The project needs no ORM. The SQL of the search depends on pgvector operators, partial indexes and literals in the text of a query. Dapper runs that SQL as it is.

| Dependency | Reason |
| --- | --- |
| Npgsql, Dapper, FluentMigrator | The request names them |
| PdfPig | The PDF files, see above |
| ModelContextProtocol.AspNetCore | The official MCP SDK. It follows the revisions of the protocol, including the revision 2026-07-28 without sessions |
| Microsoft.Extensions.FileSystemGlobbing | The patterns of the files of a source |

The package Pgvector is not needed. pgvector casts `real[]` to `vector`, so a vector goes to the database as an array of `float`.

## Decision

1. **The solution** has three .NET projects: `ProjectRag.Core`, `ProjectRag.Server` and `ProjectRag.Core.Tests`. The dashboard is a Vite project in `src/ProjectRag.Dashboard`. The build settings and the style come from Axion.
2. **A source** is a folder on the host with patterns of files, and a list of web pages. Each document is a file or a page. The indexer reads Markdown, plain text, reStructuredText, AsciiDoc, PDF and HTML.
   A page goes to its server once a day at most, and a page that does not load keeps its last version.
3. **A chunk** is a part of one section, at most 1800 characters. It keeps the path of its section, for example `Guide > Install > Linux`. The embedding model and the reranker see the path and the text.
4. **The indexer** runs in the background, one source at a time. It skips a file with the stored size and time. It reuses the vector of a chunk with the same text, so an edit of a long file embeds only the changed chunks. It scans all sources every 10 minutes.
5. **The model identity** follows the project rag. The table `embedding_model` stores the name, the dimensions, the templates, the probe text and its vector. A server that gives another vector for the stored probe text stops the vector search instead of mixing two models.
   The application checks the probe once a minute, and the indexer checks the model before each round of 128 chunks.
6. **The search** is hybrid. HNSW over `halfvec` finds 100 candidates by vector, and the full text search finds 100 candidates with OR between the words. Reciprocal rank fusion with k = 60 and a vector weight of 0.8 merges them. The reranker reorders the best 30. Each leg is optional: without the embedding server the search uses the full text search only.
7. **The web API** under `/api` serves the dashboard and scripts. **The MCP endpoint** `/mcp` gives agents five tools: `list_sources`, `search`, `find_documents`, `read_document` and `index_source`.
8. **The server** listens on 127.0.0.1 only and checks the Host header. An optional bearer token guards the API and MCP against other local programs.

## Rejected options

- **A separate vector database** (Qdrant, Milvus). The corpus holds thousands of chunks, not millions. PostgreSQL gives the vectors, the full text search and the transactions in one place.
- **The application in Docker.** It needs the folders of the sources and the llama.cpp servers on the host. A container adds mounts and gives nothing.
- **Embedding with ONNX Runtime in the process.** llama.cpp already runs the GGUF models on both GPUs, and the request prefers it.
- **A custom MCP server**, as in Axion. Axion speaks a small part of the protocol over `HttpListener`. The SDK follows the new revisions of the protocol, and the project already runs ASP.NET Core.

## Consequences

- A query waits behind the texts of the indexer in the queue of the embedding server. The indexer keeps at most 8 texts in flight, so a query waits about a second during a run.
- The text of a PDF file loses its tables and formulas. The reader drops running headers and footers, but some page numbers stay.
- With an empty token any local program can add a source and read the files of its folder. A token is necessary on a machine with several users.
- `DocumentSummary.ContentLength` counts code points, and the offsets of the chunks count UTF-16 units. The numbers differ for a text with emoji. Only the dashboard shows `ContentLength`.
- The project has no gold set of questions yet. The weights of the fusion come from the measurements of the project rag on lectures, not on technical documentation.

## Next steps

1. Collect a gold set of questions over the OpenGL specifications and measure R@k of the vector, the full text search, the hybrid and the reranker.
2. Watch the folders with `FileSystemWatcher` instead of a scan every 10 minutes.
3. Read source code files with a chunker for code.
4. Let agents add notes to the knowledge base through MCP.
