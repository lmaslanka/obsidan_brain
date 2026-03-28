using System.Text;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using ObsidianBrain.App.Config;
using ObsidianBrain.App.Data;

namespace ObsidianBrain.App.Mcp;

public sealed class McpAuditLogger(Database db, AppConfig config)
{
    private readonly Database _db = db;
    private readonly AppConfig _config = config;

    public async Task<Guid> StartCallAsync(string toolName, object? parameters, string? requestId, string? clientId, CancellationToken cancellationToken)
    {
        var callId = Guid.NewGuid();
        var serialized = JsonSerializer.Serialize(parameters ?? new { });
        var truncated = TruncateByBytes(serialized, _config.Logging.Mcp.MaxParamBytes);

        const string sql = """
            INSERT INTO mcp_tool_calls
            (id, called_at, request_id, client_id, tool_name, params_json, params_size_bytes, status, result_summary_json, result_size_bytes)
            VALUES
            (@id, now(), @requestId, @clientId, @toolName, @params::jsonb, @paramSize, 'running', '{}'::jsonb, 0);
            """;

        await using var conn = await _db.OpenConnectionAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("id", callId);
        cmd.Parameters.Add(new NpgsqlParameter("requestId", NpgsqlDbType.Text) { Value = (object?)requestId ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("clientId", NpgsqlDbType.Text) { Value = (object?)clientId ?? DBNull.Value });
        cmd.Parameters.AddWithValue("toolName", toolName);
        cmd.Parameters.AddWithValue("params", truncated.Value);
        cmd.Parameters.AddWithValue("paramSize", truncated.OriginalSize);
        await cmd.ExecuteNonQueryAsync(cancellationToken);

        return callId;
    }

    public async Task FinishCallAsync(Guid callId, string status, object? resultSummary, string? rawResult, int durationMs, string? errorCode, string? errorMessage, CancellationToken cancellationToken)
    {
        var summary = JsonSerializer.Serialize(resultSummary ?? new { });
        var result = TruncateByBytes(rawResult ?? string.Empty, _config.Logging.Mcp.MaxResultBytes);

        const string sql = """
            UPDATE mcp_tool_calls
            SET status = @status,
                result_summary_json = @summary::jsonb,
                result_truncated_text = @resultText,
                result_size_bytes = @resultSize,
                duration_ms = @durationMs,
                error_code = @errorCode,
                error_message = @errorMessage
            WHERE id = @id;
            """;

        await using var conn = await _db.OpenConnectionAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("id", callId);
        cmd.Parameters.AddWithValue("status", status);
        cmd.Parameters.AddWithValue("summary", summary);
        cmd.Parameters.Add(new NpgsqlParameter("resultText", NpgsqlDbType.Text)
        {
            Value = string.IsNullOrWhiteSpace(result.Value) ? DBNull.Value : result.Value
        });
        cmd.Parameters.AddWithValue("resultSize", result.OriginalSize);
        cmd.Parameters.AddWithValue("durationMs", durationMs);
        cmd.Parameters.Add(new NpgsqlParameter("errorCode", NpgsqlDbType.Text) { Value = (object?)errorCode ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("errorMessage", NpgsqlDbType.Text) { Value = (object?)errorMessage ?? DBNull.Value });
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static (string Value, int OriginalSize) TruncateByBytes(string input, int maxBytes)
    {
        var bytes = Encoding.UTF8.GetBytes(input);
        if (bytes.Length <= maxBytes)
        {
            return (input, bytes.Length);
        }

        var truncated = Encoding.UTF8.GetString(bytes.Take(maxBytes).ToArray());
        return (truncated, bytes.Length);
    }
}
