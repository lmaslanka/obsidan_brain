using Npgsql;
using ObsidianBrain.App.Data;
using ObsidianBrain.App.Embeddings;

namespace ObsidianBrain.App;

public static class Healthcheck
{
    public static async Task RunAsync(Database db, IEmbeddingProvider embeddings, CancellationToken cancellationToken)
    {
        await using var conn = await db.OpenConnectionAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand("SELECT 1;", conn);
        _ = await cmd.ExecuteScalarAsync(cancellationToken);
        var embeddingOk = await embeddings.HealthcheckAsync(cancellationToken);

        Console.WriteLine("database: ok");
        Console.WriteLine($"embeddings: {(embeddingOk ? "ok" : "failed")}");
    }
}
