using Npgsql;

namespace ObsidianBrain.App.Data;

public sealed class SchemaInitializer(Database db)
{
    private readonly Database _db = db;

    public async Task EnsureSchemaAsync(int embeddingDimensions, CancellationToken cancellationToken = default)
    {
        var sql = $$"""
        CREATE EXTENSION IF NOT EXISTS vector;

        CREATE TABLE IF NOT EXISTS documents (
            id uuid PRIMARY KEY,
            path text UNIQUE NOT NULL,
            filename text NOT NULL,
            file_hash_sha256 char(64) NOT NULL,
            content_text text NOT NULL,
            title text NULL,
            frontmatter_json jsonb NOT NULL DEFAULT '{}',
            headings_json jsonb NOT NULL DEFAULT '[]',
            created_at_fs timestamptz NULL,
            modified_at_fs timestamptz NULL,
            ingested_at timestamptz NOT NULL,
            updated_at timestamptz NOT NULL,
            is_deleted boolean NOT NULL DEFAULT false
        );

        CREATE TABLE IF NOT EXISTS document_chunks (
            id uuid PRIMARY KEY,
            document_id uuid NOT NULL REFERENCES documents(id) ON DELETE CASCADE,
            chunk_index int NOT NULL,
            heading_path text NULL,
            chunk_text text NOT NULL,
            chunk_text_tsv tsvector GENERATED ALWAYS AS (to_tsvector('english', chunk_text)) STORED,
            chunk_hash_sha256 char(64) NOT NULL,
            token_count int NOT NULL,
            embedding vector({{embeddingDimensions}}) NOT NULL,
            embedding_model text NOT NULL,
            embedding_version text NOT NULL,
            created_at timestamptz NOT NULL
        );

        ALTER TABLE document_chunks
            ADD COLUMN IF NOT EXISTS chunk_text_tsv tsvector GENERATED ALWAYS AS (to_tsvector('english', chunk_text)) STORED;

        CREATE TABLE IF NOT EXISTS ingestion_runs (
            id uuid PRIMARY KEY,
            mode text NOT NULL,
            started_at timestamptz NOT NULL,
            finished_at timestamptz NULL,
            status text NOT NULL,
            files_discovered int NOT NULL DEFAULT 0,
            files_processed int NOT NULL DEFAULT 0,
            files_changed int NOT NULL DEFAULT 0,
            files_skipped int NOT NULL DEFAULT 0,
            files_deleted_soft int NOT NULL DEFAULT 0,
            chunks_created int NOT NULL DEFAULT 0,
            chunks_updated int NOT NULL DEFAULT 0,
            chunks_deleted int NOT NULL DEFAULT 0,
            tokens_embedded_total int NOT NULL DEFAULT 0,
            embed_requests int NOT NULL DEFAULT 0,
            embed_failures int NOT NULL DEFAULT 0,
            retry_count int NOT NULL DEFAULT 0,
            duration_ms bigint NULL,
            errors_json jsonb NOT NULL DEFAULT '[]'
        );

        CREATE TABLE IF NOT EXISTS ingestion_run_files (
            id uuid PRIMARY KEY,
            run_id uuid NOT NULL REFERENCES ingestion_runs(id) ON DELETE CASCADE,
            path text NOT NULL,
            file_hash char(64) NULL,
            action text NOT NULL,
            bytes_read bigint NOT NULL DEFAULT 0,
            chunks_created int NOT NULL DEFAULT 0,
            chunks_updated int NOT NULL DEFAULT 0,
            chunks_deleted int NOT NULL DEFAULT 0,
            tokens_embedded int NOT NULL DEFAULT 0,
            duration_ms int NOT NULL DEFAULT 0,
            error_message text NULL,
            processed_at timestamptz NOT NULL
        );

        CREATE TABLE IF NOT EXISTS mcp_tool_calls (
            id uuid PRIMARY KEY,
            called_at timestamptz NOT NULL,
            request_id text NULL,
            client_id text NULL,
            tool_name text NOT NULL,
            params_json jsonb NOT NULL,
            params_size_bytes int NOT NULL,
            status text NOT NULL,
            result_summary_json jsonb NOT NULL,
            result_truncated_text text NULL,
            result_size_bytes int NOT NULL,
            duration_ms int NULL,
            error_code text NULL,
            error_message text NULL
        );

        CREATE INDEX IF NOT EXISTS ix_documents_active_mtime ON documents(is_deleted, modified_at_fs);
        CREATE INDEX IF NOT EXISTS ix_chunks_doc ON document_chunks(document_id, chunk_index);
        CREATE INDEX IF NOT EXISTS ix_chunks_fts ON document_chunks USING GIN (chunk_text_tsv);
        CREATE INDEX IF NOT EXISTS ix_chunks_embedding ON document_chunks USING hnsw (embedding vector_cosine_ops);
        CREATE INDEX IF NOT EXISTS ix_ingestion_files_run ON ingestion_run_files(run_id);
        CREATE INDEX IF NOT EXISTS ix_ingestion_files_action_time ON ingestion_run_files(action, processed_at DESC);
        CREATE INDEX IF NOT EXISTS ix_mcp_calls_time ON mcp_tool_calls(called_at DESC);
        CREATE INDEX IF NOT EXISTS ix_mcp_calls_tool_time ON mcp_tool_calls(tool_name, called_at DESC);
        CREATE INDEX IF NOT EXISTS ix_mcp_calls_status_time ON mcp_tool_calls(status, called_at DESC);
        """;

        await using var conn = await _db.OpenConnectionAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
}
