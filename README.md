# ObsidianBrain

ObsidianBrain is a small .NET application that turns a folder of Obsidian-style notes into a searchable local knowledge base.

It scans Markdown and text files, parses basic note structure, splits documents into chunks, generates embeddings, stores everything in PostgreSQL with `pgvector`, and exposes that indexed content through a lightweight MCP-compatible HTTP server.

## What the project does

The application is built around three jobs:

1. Ingestion
   - Scans configured note roots for `.md` and `.txt` files
   - Parses YAML-like frontmatter and headings
   - Chunks note content into overlapping token windows
   - Generates embeddings for each chunk
   - Stores documents, chunks, and run logs in PostgreSQL

2. Retrieval
   - Embeds an incoming query
   - Runs hybrid retrieval using vector similarity plus PostgreSQL full-text search
   - Returns ranked note snippets and full document content

3. MCP serving
   - Starts an HTTP JSON-RPC endpoint at `/mcp`
   - Exposes tools for searching notes, fetching a document, and listing recent notes
   - Logs MCP tool calls to the database

## High-level architecture

- `ObsidianBrain.App/Program.cs` wires the CLI commands together.
- `ObsidianBrain.App/Ingestion/` handles file discovery, parsing, chunking, embedding, and watch mode.
- `ObsidianBrain.App/Retrieval/` handles hybrid search.
- `ObsidianBrain.App/Mcp/` exposes indexed notes through an MCP-style JSON-RPC server.
- `ObsidianBrain.App/Data/` initializes the PostgreSQL schema and opens database connections.

## Storage model

The app creates PostgreSQL tables for:

- `documents`: one row per source file
- `document_chunks`: chunk text, metadata, and vector embeddings
- `ingestion_runs` and `ingestion_run_files`: ingestion audit logs
- `mcp_tool_calls`: MCP request audit logs

It also creates the `vector` extension and an HNSW index for cosine similarity search, so PostgreSQL needs `pgvector` available.

## Commands

From the repo root, the app can be run with:

```bash
dotnet run --project ObsidianBrain.App -- scan-once --config ObsidianBrain.App/appsettings.json
dotnet run --project ObsidianBrain.App -- watch --config ObsidianBrain.App/appsettings.json
dotnet run --project ObsidianBrain.App -- mcp-serve --config ObsidianBrain.App/appsettings.json
dotnet run --project ObsidianBrain.App -- healthcheck --config ObsidianBrain.App/appsettings.json
```

Command behavior:

- `scan-once`: performs a full indexing pass over configured note roots
- `watch`: performs an initial scan, then watches for file changes and periodically reconciles the full corpus
- `mcp-serve`: starts the local MCP server
- `healthcheck`: verifies database connectivity and embedding availability

## Configuration

The app reads configuration from `appsettings.json`. If the file does not exist, it creates one with default values.

Key settings include:

- `Roots`: note directories to index
- `Postgres.ConnectionString`: database connection string
- `Embeddings`: API key env var name, model, base URL, dimensions, and retry settings
- `Chunking`: chunk size and overlap
- `Watch`: debounce and reconcile intervals
- `Search`: weighting between vector and full-text search
- `Mcp`: bind host, port, and default result count

An example config lives at `ObsidianBrain.App/appsettings.example.json`.

## Embeddings

The embedding provider is written for an OpenAI-compatible embeddings endpoint. By default it posts to `/v1/embeddings` using the configured model.

If the configured API key environment variable is missing, the app falls back to a deterministic hash-based embedding vector. That is useful for local development, but it is not a substitute for real semantic embeddings.

## MCP tools exposed

The MCP server currently exposes:

- `notes.search`: hybrid search over indexed chunks
- `notes.get_snippets`: search returning snippet-style results
- `notes.get_document`: fetch a full document by path
- `notes.list_recent`: list recently modified indexed documents

## Runtime requirements

- .NET 10 SDK
- PostgreSQL with the `pgvector` extension available
- An embeddings endpoint compatible with the OpenAI embeddings API if you want semantic retrieval quality

## Current scope and limitations

- Markdown parsing is intentionally simple: it extracts frontmatter and headings, not full Markdown structure.
- Chunking is whitespace/token-count based rather than model-tokenizer accurate.
- The MCP server uses `HttpListener` and serves plain HTTP on a local bind address.
- Search quality depends heavily on the configured embedding model and database contents.

## In one sentence

This project is a local note-indexing and retrieval service for Obsidian-style files, backed by PostgreSQL/pgvector and exposed through an MCP-compatible search server.
