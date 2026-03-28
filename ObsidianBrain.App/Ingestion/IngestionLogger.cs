using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using ObsidianBrain.App.Data;

namespace ObsidianBrain.App.Ingestion;

public sealed class IngestionLogger(Database db)
{
    private readonly Database _db = db;

    public async Task<Guid> StartRunAsync(string mode, CancellationToken cancellationToken)
    {
        var runId = Guid.NewGuid();
        const string sql = """
            INSERT INTO ingestion_runs (id, mode, started_at, status)
            VALUES (@id, @mode, now(), 'running');
            """;
        await using var conn = await _db.OpenConnectionAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("id", runId);
        cmd.Parameters.AddWithValue("mode", mode);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
        return runId;
    }

    public async Task LogFileAsync(Guid runId, IngestionFileStat stat, CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO ingestion_run_files
            (id, run_id, path, file_hash, action, bytes_read, chunks_created, chunks_updated, chunks_deleted, tokens_embedded, duration_ms, error_message, processed_at)
            VALUES
            (@id, @runId, @path, @hash, @action, @bytesRead, @chunksCreated, @chunksUpdated, @chunksDeleted, @tokensEmbedded, @durationMs, @errorMessage, now());
            """;

        await using var conn = await _db.OpenConnectionAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("id", Guid.NewGuid());
        cmd.Parameters.AddWithValue("runId", runId);
        cmd.Parameters.AddWithValue("path", stat.Path);
        cmd.Parameters.Add(new NpgsqlParameter("hash", NpgsqlDbType.Text) { Value = (object?)stat.FileHash ?? DBNull.Value });
        cmd.Parameters.AddWithValue("action", stat.Action);
        cmd.Parameters.AddWithValue("bytesRead", stat.BytesRead);
        cmd.Parameters.AddWithValue("chunksCreated", stat.ChunksCreated);
        cmd.Parameters.AddWithValue("chunksUpdated", stat.ChunksUpdated);
        cmd.Parameters.AddWithValue("chunksDeleted", stat.ChunksDeleted);
        cmd.Parameters.AddWithValue("tokensEmbedded", stat.TokensEmbedded);
        cmd.Parameters.AddWithValue("durationMs", stat.DurationMs);
        cmd.Parameters.Add(new NpgsqlParameter("errorMessage", NpgsqlDbType.Text) { Value = (object?)stat.ErrorMessage ?? DBNull.Value });
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task FinishRunAsync(Guid runId, string status, RunCounters counters, List<string> errors, long durationMs, CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE ingestion_runs
            SET finished_at = now(),
                status = @status,
                files_discovered = @filesDiscovered,
                files_processed = @filesProcessed,
                files_changed = @filesChanged,
                files_skipped = @filesSkipped,
                files_deleted_soft = @filesDeletedSoft,
                chunks_created = @chunksCreated,
                chunks_updated = @chunksUpdated,
                chunks_deleted = @chunksDeleted,
                tokens_embedded_total = @tokensEmbedded,
                embed_requests = @embedRequests,
                embed_failures = @embedFailures,
                retry_count = @retryCount,
                duration_ms = @durationMs,
                errors_json = @errors::jsonb
            WHERE id = @id;
            """;

        await using var conn = await _db.OpenConnectionAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("id", runId);
        cmd.Parameters.AddWithValue("status", status);
        cmd.Parameters.AddWithValue("filesDiscovered", counters.FilesDiscovered);
        cmd.Parameters.AddWithValue("filesProcessed", counters.FilesProcessed);
        cmd.Parameters.AddWithValue("filesChanged", counters.FilesChanged);
        cmd.Parameters.AddWithValue("filesSkipped", counters.FilesSkipped);
        cmd.Parameters.AddWithValue("filesDeletedSoft", counters.FilesDeletedSoft);
        cmd.Parameters.AddWithValue("chunksCreated", counters.ChunksCreated);
        cmd.Parameters.AddWithValue("chunksUpdated", counters.ChunksUpdated);
        cmd.Parameters.AddWithValue("chunksDeleted", counters.ChunksDeleted);
        cmd.Parameters.AddWithValue("tokensEmbedded", counters.TokensEmbedded);
        cmd.Parameters.AddWithValue("embedRequests", counters.EmbedRequests);
        cmd.Parameters.AddWithValue("embedFailures", counters.EmbedFailures);
        cmd.Parameters.AddWithValue("retryCount", counters.RetryCount);
        cmd.Parameters.AddWithValue("durationMs", durationMs);
        cmd.Parameters.AddWithValue("errors", JsonSerializer.Serialize(errors));
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
}

public sealed class RunCounters
{
    public int FilesDiscovered { get; set; }
    public int FilesProcessed { get; set; }
    public int FilesChanged { get; set; }
    public int FilesSkipped { get; set; }
    public int FilesDeletedSoft { get; set; }
    public int ChunksCreated { get; set; }
    public int ChunksUpdated { get; set; }
    public int ChunksDeleted { get; set; }
    public int TokensEmbedded { get; set; }
    public int EmbedRequests { get; set; }
    public int EmbedFailures { get; set; }
    public int RetryCount { get; set; }
}
