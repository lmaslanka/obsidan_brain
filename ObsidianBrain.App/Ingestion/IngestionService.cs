using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using ObsidianBrain.App.Config;
using ObsidianBrain.App.Data;
using ObsidianBrain.App.Embeddings;
using ObsidianBrain.App.Utils;

namespace ObsidianBrain.App.Ingestion;

public sealed class IngestionService(Database db, IEmbeddingProvider embeddings, AppConfig config)
{
    private readonly Database _db = db;
    private readonly IEmbeddingProvider _embeddings = embeddings;
    private readonly AppConfig _config = config;
    private readonly IngestionLogger _logger = new(db);
    private readonly ConcurrentDictionary<string, byte> _pendingPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _pendingSignal = new(0);

    public async Task RunOnceAsync(string mode, CancellationToken cancellationToken)
    {
        var files = DiscoverFiles().ToList();
        await RunForPathsAsync(mode, files, applySoftDelete: true, cancellationToken);
    }

    public async Task WatchAsync(CancellationToken cancellationToken)
    {
        await RunOnceAsync("watch-initial", cancellationToken);

        var watchers = new List<FileSystemWatcher>();
        foreach (var root in _config.Roots)
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            var watcher = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true,
                EnableRaisingEvents = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite
            };

            watcher.Changed += (_, e) => EnqueueWatchPath(e.FullPath);
            watcher.Created += (_, e) => EnqueueWatchPath(e.FullPath);
            watcher.Deleted += (_, e) => EnqueueWatchPath(e.FullPath);
            watcher.Renamed += (_, e) => EnqueueWatchPath(e.FullPath);
            watchers.Add(watcher);
        }

        try
        {
            var eventLoop = ProcessPendingLoopAsync(cancellationToken);
            var reconcileLoop = ReconcileLoopAsync(cancellationToken);
            await Task.WhenAll(eventLoop, reconcileLoop);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            foreach (var watcher in watchers)
            {
                watcher.Dispose();
            }
        }
    }

    private void EnqueueWatchPath(string fullPath)
    {
        if (!ShouldIncludePath(fullPath))
        {
            return;
        }

        if (_pendingPaths.TryAdd(fullPath, 0))
        {
            _pendingSignal.Release();
        }
    }

    private async Task ProcessPendingLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await _pendingSignal.WaitAsync(cancellationToken);
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(50, _config.Watch.DebounceMilliseconds)), cancellationToken);

            var paths = DrainPendingPaths();
            if (paths.Count == 0)
            {
                continue;
            }

            await RunForPathsAsync("watch-event-batch", paths, applySoftDelete: false, cancellationToken);
        }
    }

    private async Task ReconcileLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromMinutes(_config.Watch.ReconcileIntervalMinutes), cancellationToken);
            await RunOnceAsync("watch-reconcile", cancellationToken);
        }
    }

    private List<string> DrainPendingPaths()
    {
        var paths = new List<string>();
        foreach (var kvp in _pendingPaths)
        {
            if (_pendingPaths.TryRemove(kvp.Key, out _))
            {
                paths.Add(kvp.Key);
            }
        }

        return paths;
    }

    private async Task RunForPathsAsync(string mode, IReadOnlyCollection<string> paths, bool applySoftDelete, CancellationToken cancellationToken)
    {
        var errors = new List<string>();
        var counters = new RunCounters
        {
            FilesDiscovered = paths.Count
        };
        var timer = Stopwatch.StartNew();
        var runId = await _logger.StartRunAsync(mode, cancellationToken);

        try
        {
            var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in paths.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!ShouldIncludePath(file))
                {
                    continue;
                }

                var fileTimer = Stopwatch.StartNew();
                seenPaths.Add(file);

                IngestionFileStat stat;
                if (File.Exists(file))
                {
                    stat = await ProcessFileAsync(file, counters, cancellationToken);
                }
                else
                {
                    var deleted = await SoftDeletePathIfExistsAsync(file, cancellationToken);
                    if (!deleted)
                    {
                        continue;
                    }

                    counters.FilesDeletedSoft++;
                    stat = new IngestionFileStat
                    {
                        Path = file,
                        Action = "deleted",
                        DurationMs = 0
                    };
                }

                stat.DurationMs = (int)fileTimer.ElapsedMilliseconds;
                await _logger.LogFileAsync(runId, stat, cancellationToken);
            }

            if (applySoftDelete)
            {
                counters.FilesDeletedSoft += await SoftDeleteMissingAsync(seenPaths, runId, cancellationToken);
                await ApplyRetentionPolicyAsync(cancellationToken);
            }

            var status = errors.Count == 0 ? "success" : "partial_success";
            await _logger.FinishRunAsync(runId, status, counters, errors, timer.ElapsedMilliseconds, cancellationToken);
        }
        catch (Exception ex)
        {
            errors.Add(ex.Message);
            await _logger.FinishRunAsync(runId, "failed", counters, errors, timer.ElapsedMilliseconds, CancellationToken.None);
            throw;
        }
    }

    private async Task<IngestionFileStat> ProcessFileAsync(string path, RunCounters counters, CancellationToken cancellationToken)
    {
        var text = await File.ReadAllTextAsync(path, cancellationToken);
        var hash = Hashing.Sha256(text);

        var existing = await FindDocumentAsync(path, cancellationToken);
        if (existing is not null && existing.Value.Hash == hash && !existing.Value.IsDeleted)
        {
            counters.FilesSkipped++;
            return new IngestionFileStat
            {
                Path = path,
                FileHash = hash,
                Action = "unchanged",
                BytesRead = text.Length,
                DurationMs = 0
            };
        }

        var parsed = MarkdownParser.Parse(text);
        var chunks = Chunker.ChunkMarkdown(parsed, _config.Chunking.MaxTokens, _config.Chunking.OverlapTokens);

        var embeddedChunks = new List<(Chunk Chunk, float[] Embedding)>(chunks.Count);
        var embeddedTokens = 0;
        for (var i = 0; i < chunks.Count; i++)
        {
            var chunk = chunks[i];
            counters.EmbedRequests++;

            float[] embedding;
            try
            {
                embedding = await _embeddings.EmbedAsync(chunk.Text, cancellationToken);
                ValidateEmbeddingLength(path, i, embedding);
            }
            catch
            {
                counters.EmbedFailures++;
                throw;
            }

            embeddedChunks.Add((chunk, embedding));
            embeddedTokens += chunk.TokenCount;
        }

        var docId = existing?.Id ?? Guid.NewGuid();
        var oldChunks = 0;

        await using (var conn = await _db.OpenConnectionAsync(cancellationToken))
        await using (var tx = await conn.BeginTransactionAsync(cancellationToken))
        {
            await UpsertDocumentAsync(conn, tx, docId, path, hash, parsed, cancellationToken);
            oldChunks = await DeleteExistingChunksAsync(conn, tx, docId, cancellationToken);

            for (var i = 0; i < embeddedChunks.Count; i++)
            {
                var data = embeddedChunks[i];
                await InsertChunkAsync(conn, tx, docId, i, data.Chunk, data.Embedding, cancellationToken);
            }

            await tx.CommitAsync(cancellationToken);
        }

        counters.FilesProcessed++;
        counters.FilesChanged++;
        counters.ChunksCreated += chunks.Count;
        counters.ChunksDeleted += oldChunks;
        counters.TokensEmbedded += embeddedTokens;

        return new IngestionFileStat
        {
            Path = path,
            FileHash = hash,
            Action = existing is null ? "created" : "updated",
            BytesRead = text.Length,
            ChunksCreated = chunks.Count,
            ChunksDeleted = oldChunks,
            TokensEmbedded = embeddedTokens,
            DurationMs = 0
        };
    }

    private IEnumerable<string> DiscoverFiles()
    {
        foreach (var root in _config.Roots)
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Where(ShouldIncludePath);
            foreach (var file in files)
            {
                yield return file;
            }
        }
    }

    private bool ShouldIncludePath(string path)
    {
        var extension = Path.GetExtension(path);
        if (!_config.IncludeExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        var normalized = path.Replace('\\', '/');
        return !_config.ExcludeGlobs.Any(ex => normalized.Contains(ex, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<(Guid Id, string Hash, bool IsDeleted)?> FindDocumentAsync(string path, CancellationToken cancellationToken)
    {
        const string sql = "SELECT id, file_hash_sha256, is_deleted FROM documents WHERE path = @path LIMIT 1;";
        await using var conn = await _db.OpenConnectionAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("path", path);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return (reader.GetGuid(0), reader.GetString(1), reader.GetBoolean(2));
    }

    private static async Task UpsertDocumentAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        Guid id,
        string path,
        string hash,
        ParsedDocument parsed,
        CancellationToken cancellationToken)
    {
        var fi = new FileInfo(path);

        const string sql = """
            INSERT INTO documents
            (id, path, filename, file_hash_sha256, content_text, title, frontmatter_json, headings_json, created_at_fs, modified_at_fs, ingested_at, updated_at, is_deleted)
            VALUES
            (@id, @path, @filename, @hash, @content, @title, @frontmatter::jsonb, @headings::jsonb, @created, @modified, now(), now(), false)
            ON CONFLICT (path) DO UPDATE SET
                file_hash_sha256 = EXCLUDED.file_hash_sha256,
                content_text = EXCLUDED.content_text,
                title = EXCLUDED.title,
                frontmatter_json = EXCLUDED.frontmatter_json,
                headings_json = EXCLUDED.headings_json,
                created_at_fs = EXCLUDED.created_at_fs,
                modified_at_fs = EXCLUDED.modified_at_fs,
                ingested_at = now(),
                updated_at = now(),
                is_deleted = false;
            """;

        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("path", path);
        cmd.Parameters.AddWithValue("filename", fi.Name);
        cmd.Parameters.AddWithValue("hash", hash);
        cmd.Parameters.AddWithValue("content", parsed.Content);
        cmd.Parameters.Add(new NpgsqlParameter("title", NpgsqlDbType.Text) { Value = (object?)parsed.Title ?? DBNull.Value });
        cmd.Parameters.AddWithValue("frontmatter", JsonSerializer.Serialize(parsed.Frontmatter));
        cmd.Parameters.AddWithValue("headings", JsonSerializer.Serialize(parsed.Headings));
        cmd.Parameters.AddWithValue("created", fi.CreationTimeUtc);
        cmd.Parameters.AddWithValue("modified", fi.LastWriteTimeUtc);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<int> DeleteExistingChunksAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid documentId, CancellationToken cancellationToken)
    {
        const string sql = """
            WITH deleted AS (
                DELETE FROM document_chunks WHERE document_id = @id RETURNING 1
            )
            SELECT COUNT(*) FROM deleted;
            """;
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        cmd.Parameters.AddWithValue("id", documentId);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken));
    }

    private async Task InsertChunkAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        Guid documentId,
        int chunkIndex,
        Chunk chunk,
        float[] embedding,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO document_chunks
            (id, document_id, chunk_index, heading_path, chunk_text, chunk_hash_sha256, token_count, embedding, embedding_model, embedding_version, created_at)
            VALUES
            (@id, @docId, @idx, @headingPath, @text, @hash, @tokens, @embedding::vector, @model, @version, now());
            """;

        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        cmd.Parameters.AddWithValue("id", Guid.NewGuid());
        cmd.Parameters.AddWithValue("docId", documentId);
        cmd.Parameters.AddWithValue("idx", chunkIndex);
        cmd.Parameters.Add(new NpgsqlParameter("headingPath", NpgsqlDbType.Text)
        {
            Value = string.IsNullOrWhiteSpace(chunk.HeadingPath) ? DBNull.Value : chunk.HeadingPath
        });
        cmd.Parameters.AddWithValue("text", chunk.Text);
        cmd.Parameters.AddWithValue("hash", Hashing.Sha256(chunk.Text));
        cmd.Parameters.AddWithValue("tokens", chunk.TokenCount);
        cmd.Parameters.AddWithValue("embedding", ToVectorLiteral(embedding));
        cmd.Parameters.AddWithValue("model", _config.Embeddings.Model);
        cmd.Parameters.AddWithValue("version", "v1");
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<int> SoftDeleteMissingAsync(HashSet<string> seenPaths, Guid runId, CancellationToken cancellationToken)
    {
        var roots = _config.Roots
            .Where(Directory.Exists)
            .Select(NormalizePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (roots.Length == 0)
        {
            return 0;
        }

        const string findSql = """
            SELECT path
            FROM documents d
            WHERE d.is_deleted = false
              AND EXISTS (
                  SELECT 1
                  FROM unnest(@roots) AS root
                  WHERE replace(d.path, '\', '/') = root OR replace(d.path, '\', '/') LIKE root || '/%'
              );
            """;

        await using var conn = await _db.OpenConnectionAsync(cancellationToken);
        await using var findCmd = new NpgsqlCommand(findSql, conn);
        findCmd.Parameters.AddWithValue("roots", roots);
        await using var reader = await findCmd.ExecuteReaderAsync(cancellationToken);

        var stale = new List<string>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var path = reader.GetString(0);
            if (!seenPaths.Contains(path))
            {
                stale.Add(path);
            }
        }

        await reader.CloseAsync();
        if (stale.Count == 0)
        {
            return 0;
        }

        var count = 0;
        const string updateSql = "UPDATE documents SET is_deleted = true, updated_at = now() WHERE path = @path AND is_deleted = false;";
        foreach (var path in stale)
        {
            await using var updateCmd = new NpgsqlCommand(updateSql, conn);
            updateCmd.Parameters.AddWithValue("path", path);
            var affected = await updateCmd.ExecuteNonQueryAsync(cancellationToken);
            if (affected == 0)
            {
                continue;
            }

            await _logger.LogFileAsync(runId, new IngestionFileStat
            {
                Path = path,
                Action = "deleted",
                DurationMs = 0
            }, cancellationToken);
            count++;
        }

        return count;
    }

    private async Task<bool> SoftDeletePathIfExistsAsync(string path, CancellationToken cancellationToken)
    {
        const string sql = "UPDATE documents SET is_deleted = true, updated_at = now() WHERE path = @path AND is_deleted = false;";
        await using var conn = await _db.OpenConnectionAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("path", path);
        return await cmd.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    private async Task ApplyRetentionPolicyAsync(CancellationToken cancellationToken)
    {
        var retention = _config.Logging.Retention;
        if (!string.Equals(retention.Mode, "days", StringComparison.OrdinalIgnoreCase) || retention.Days is null)
        {
            return;
        }

        const string sql = """
            DELETE FROM mcp_tool_calls WHERE called_at < now() - make_interval(days => @days);
            DELETE FROM ingestion_run_files WHERE processed_at < now() - make_interval(days => @days);
            DELETE FROM ingestion_runs WHERE started_at < now() - make_interval(days => @days);
            """;

        await using var conn = await _db.OpenConnectionAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("days", retention.Days.Value);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private void ValidateEmbeddingLength(string path, int chunkIndex, float[] embedding)
    {
        if (embedding.Length != _config.Embeddings.Dimensions)
        {
            throw new InvalidOperationException(
                $"Embedding dimensions mismatch for '{path}' chunk {chunkIndex}. Expected {_config.Embeddings.Dimensions}, got {embedding.Length}.");
        }
    }

    private static string ToVectorLiteral(float[] embedding)
    {
        return "[" + string.Join(',', embedding.Select(v => v.ToString(System.Globalization.CultureInfo.InvariantCulture))) + "]";
    }

    private static string NormalizePath(string path)
    {
        return path.Replace('\\', '/').TrimEnd('/');
    }
}
