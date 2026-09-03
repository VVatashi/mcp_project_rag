# ProjectRAG

ProjectRAG indexes Markdown documents for retrieval. It cuts a file into chunks along its headings, embeds every chunk with the `Qdrant/Qwen3-Embedding-0.6B-onnx` export of Qwen3-Embedding-0.6B through ONNX Runtime, and stores the text and the vector in PostgreSQL. A search runs over the words with a full text index, or over the meaning with a vector index.

## Requirements

1. Install the .NET SDK 10.0 or newer.
2. Install Docker with the Compose plugin.
3. Keep about 1.2 GB of disk for the model and about 2 GB of memory for a run.

The project references `Tokenizers.DotNet.runtime.win-x64`, so it builds for Windows on x64. On another platform, replace that package with the runtime package of your platform.

## Set up the database

The application needs PostgreSQL 18 with the pgvector extension. The Compose file builds that image from `docker/postgres/Dockerfile`.

1. Copy `.env.example` to `.env` if you want other credentials than the defaults.
2. Start the container:

```bash
docker compose up -d
```

The first start builds pgvector from source and takes a few minutes. The init script `docker/postgres/init/01-extensions.sql` creates the `vector` extension in the fresh database.

## Get the embedding model

The model does not live in the repository. Download two files from the Hugging Face repository `Qdrant/Qwen3-Embedding-0.6B-onnx` and place them like this:

```
models/
  Qwen3-Embedding-0.6B/
    tokenizer.json
    onnx/
      model_quantized.onnx
```

The Hugging Face client writes that layout on its own:

```bash
hf download Qdrant/Qwen3-Embedding-0.6B-onnx tokenizer.json onnx/model_quantized.onnx --local-dir models/Qwen3-Embedding-0.6B
```

Older versions of the client name this command `huggingface-cli download`. You can also download both files from the model page by hand.

The application looks for the two paths from the working directory upwards, and then from the directory of the binary upwards. The `models` folder at the root of the repository therefore works for `dotnet run` and for a published binary. Another export of the same model works as well, as long as it returns a 1024 wide `last_hidden_state` and you point the settings at its files.

## Build and test

```bash
dotnet build
```

```bash
dotnet test
```

The tests that need the database skip themselves when nothing answers on the connection string. The tests that need the model skip themselves when the model files are missing.

## Commands

Create or update the schema:

```bash
dotnet run --project src/ProjectRAG.Cli -- migrate
```

Index one Markdown file. This command runs the pending migrations first, so it also works on an empty database:

```bash
dotnet run --project src/ProjectRAG.Cli -- index docs/architecture.md
```

Search by meaning, which embeds the query and compares vectors:

```bash
dotnet run --project src/ProjectRAG.Cli -- search "how does the render hardware interface work"
```

Search by words, which uses the full text index:

```bash
dotnet run --project src/ProjectRAG.Cli -- search-text "render hardware interface"
```

Both search commands take an optional number of hits after the query. `ProjectRAG.Cli help` prints the whole list.

Both searches return the five best chunks with the document path, the line range and the heading path. Neither of them touches the schema, so a search needs read rights only. The word search never loads the model, so it starts without the second that the model costs.

The word index holds the heading path as well as the body, and a heading match weighs more than a body match.

`index` reads the file, hashes the content and compares the hash with the stored one. A file that did not change keeps the rows it already has. A changed file replaces its document row and all of its chunks in one transaction.

The key of a document is its path as an absolute `file:///` URI, compared without case. The same file therefore stays one document, whatever spelling of the path you pass.

## Settings

`src/ProjectRAG.Cli/appsettings.json` holds the defaults:

| Section | Key | Default |
| --- | --- | --- |
| `Database` | `ConnectionString` | `Host=localhost;Port=5432;Database=projectrag;Username=projectrag;Password=projectrag` |
| `Embedding` | `TokenizerFile` | `models/Qwen3-Embedding-0.6B/tokenizer.json` |
| `Embedding` | `ModelFile` | `models/Qwen3-Embedding-0.6B/onnx/model_quantized.onnx` |
| `Embedding` | `BatchSize` | `8` |
| `Chunking` | `MaxTokens` | `512` |
| `Logging` | `LogLevel:Default` | `Information` |

To override a value, create `src/ProjectRAG.Cli/appsettings.Local.json` with the same shape. Git ignores that file, so it is the place for your own credentials.

## What the index holds

The migration creates two tables.

1. `documents` keeps one row per file: the path, the title, the SHA-256 of the content, the whole text and the time of the last indexing.
2. `chunks` keeps one row per chunk: the heading path, the first and the last line in the file, the text, its `tsvector`, the 1024 wide embedding and the token count.

An HNSW index over the embedding serves the vector search. A GIN index over the `tsvector` serves the word search. Both searches sort by their own score and return the same shape of result.

## Project layout

```
docker/postgres/       the PostgreSQL image with pgvector
src/ProjectRAG.Cli/    the application
  Migrations/          the database schema
tests/                 the xunit tests
models/                the model files, ignored by git
```
