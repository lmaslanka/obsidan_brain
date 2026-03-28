using Npgsql;
using ObsidianBrain.App.Config;
using ObsidianBrain.App.Data;
using ObsidianBrain.App.Embeddings;

namespace ObsidianBrain.App.Retrieval;

public sealed class SearchService(Database db, IEmbeddingProvider embeddings, AppConfig config)
{
    private readonly Database _db = db;
    private readonly IEmbeddingProvider _embeddings = embeddings;
    private readonly AppConfig _config = config;

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(string query, int? topK, CancellationToken cancellationToken)
    {
        var queryEmbedding = await _embeddings.EmbedAsync(query, cancellationToken);
        var k = Math.Max(1, topK.GetValueOrDefault(_config.Mcp.DefaultTopK));
        var candidatePool = Math.Max(k, _config.Search.CandidatePoolSize);
        var vectorWeight = _config.Search.VectorWeight;
        var ftsWeight = _config.Search.FtsWeight;
        var weightSum = vectorWeight + ftsWeight;
        if (weightSum <= 0)
        {
            vectorWeight = 0.65;
            ftsWeight = 0.35;
            weightSum = 1.0;
        }

        vectorWeight /= weightSum;
        ftsWeight /= weightSum;

        const string sql = """
            WITH vector_candidates AS (
                SELECT c.id AS chunk_id,
                       d.path,
                       d.title,
                       c.heading_path,
                       c.chunk_text,
                       1 - (c.embedding <=> @qEmbedding::vector) AS vector_score
                FROM document_chunks c
                JOIN documents d ON d.id = c.document_id
                WHERE d.is_deleted = false
                ORDER BY c.embedding <=> @qEmbedding::vector
                LIMIT @candidatePool
            ),
            text_candidates AS (
                SELECT c.id AS chunk_id,
                       d.path,
                       d.title,
                       c.heading_path,
                       c.chunk_text,
                       ts_rank_cd(c.chunk_text_tsv, plainto_tsquery('english', @query)) AS fts_score
                FROM document_chunks c
                JOIN documents d ON d.id = c.document_id
                WHERE d.is_deleted = false
                  AND c.chunk_text_tsv @@ plainto_tsquery('english', @query)
                ORDER BY fts_score DESC
                LIMIT @candidatePool
            ),
            scored AS (
                SELECT v.chunk_id,
                       v.path,
                       v.title,
                       v.heading_path,
                       v.chunk_text,
                       v.vector_score,
                       COALESCE(t.fts_score, 0) AS fts_score
                FROM vector_candidates v
                LEFT JOIN text_candidates t ON t.chunk_id = v.chunk_id
                UNION ALL
                SELECT t.chunk_id,
                       t.path,
                       t.title,
                       t.heading_path,
                       t.chunk_text,
                       0,
                       t.fts_score
                FROM text_candidates t
                LEFT JOIN vector_candidates v ON v.chunk_id = t.chunk_id
                WHERE v.chunk_id IS NULL
            )
            SELECT chunk_id,
                   path,
                   title,
                   heading_path,
                   left(chunk_text, 800) AS snippet,
                   (@vectorWeight * GREATEST(vector_score, 0) + @ftsWeight * GREATEST(fts_score, 0)) AS score
            FROM scored
            ORDER BY score DESC
            LIMIT @k;
            """;

        await using var conn = await _db.OpenConnectionAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("qEmbedding", ToVectorLiteral(queryEmbedding));
        cmd.Parameters.AddWithValue("query", query);
        cmd.Parameters.AddWithValue("candidatePool", candidatePool);
        cmd.Parameters.AddWithValue("vectorWeight", vectorWeight);
        cmd.Parameters.AddWithValue("ftsWeight", ftsWeight);
        cmd.Parameters.AddWithValue("k", k);

        var results = new List<SearchResult>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new SearchResult(
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetString(4),
                reader.GetGuid(0),
                reader.GetDouble(5)
            ));
        }

        return results;
    }

    public async Task<string?> GetDocumentAsync(string path, CancellationToken cancellationToken)
    {
        const string sql = "SELECT content_text FROM documents WHERE path = @path AND is_deleted = false LIMIT 1;";
        await using var conn = await _db.OpenConnectionAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("path", path);
        var value = await cmd.ExecuteScalarAsync(cancellationToken);
        return value as string;
    }

    public async Task<IReadOnlyList<string>> ListRecentAsync(int limit, CancellationToken cancellationToken)
    {
        const string sql = "SELECT path FROM documents WHERE is_deleted = false ORDER BY modified_at_fs DESC NULLS LAST LIMIT @limit;";
        await using var conn = await _db.OpenConnectionAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("limit", limit);
        var paths = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            paths.Add(reader.GetString(0));
        }

        return paths;
    }

    private static string ToVectorLiteral(float[] embedding)
    {
        return "[" + string.Join(',', embedding.Select(v => v.ToString(System.Globalization.CultureInfo.InvariantCulture))) + "]";
    }
}
