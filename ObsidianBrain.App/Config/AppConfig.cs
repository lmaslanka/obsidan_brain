using System.Text.Json;

namespace ObsidianBrain.App.Config;

public sealed class AppConfig
{
    public List<string> Roots { get; set; } = [];
    public List<string> IncludeExtensions { get; set; } = [".md", ".txt"];
    public List<string> ExcludeGlobs { get; set; } = [".git", ".obsidian"];
    public PostgresConfig Postgres { get; set; } = new();
    public EmbeddingsConfig Embeddings { get; set; } = new();
    public ChunkingConfig Chunking { get; set; } = new();
    public WatchConfig Watch { get; set; } = new();
    public SearchConfig Search { get; set; } = new();
    public McpConfig Mcp { get; set; } = new();
    public LoggingConfig Logging { get; set; } = new();
}

public sealed class PostgresConfig
{
    public string ConnectionString { get; set; } = "Host=localhost;Database=obsidian_brain;Username=postgres;Password=postgres";
}

public sealed class EmbeddingsConfig
{
    public string ApiKeyEnvVar { get; set; } = "OPENAI_API_KEY";
    public string Model { get; set; } = "text-embedding-3-small";
    public string BaseUrl { get; set; } = "https://api.openai.com";
    public int Dimensions { get; set; } = 1536;
    public int TimeoutSeconds { get; set; } = 30;
    public int MaxRetries { get; set; } = 2;
    public int InitialBackoffMs { get; set; } = 250;
}

public sealed class ChunkingConfig
{
    public int MaxTokens { get; set; } = 500;
    public int OverlapTokens { get; set; } = 80;
}

public sealed class WatchConfig
{
    public int ReconcileIntervalMinutes { get; set; } = 30;
    public int DebounceMilliseconds { get; set; } = 1000;
}

public sealed class SearchConfig
{
    public double VectorWeight { get; set; } = 0.65;
    public double FtsWeight { get; set; } = 0.35;
    public int CandidatePoolSize { get; set; } = 100;
}

public sealed class McpConfig
{
    public int Port { get; set; } = 8787;
    public string BindHost { get; set; } = "127.0.0.1";
    public int DefaultTopK { get; set; } = 8;
}

public sealed class LoggingConfig
{
    public RetentionConfig Retention { get; set; } = new();
    public McpLoggingConfig Mcp { get; set; } = new();
}

public sealed class RetentionConfig
{
    public string Mode { get; set; } = "keep_all";
    public int? Days { get; set; }
}

public sealed class McpLoggingConfig
{
    public int MaxParamBytes { get; set; } = 16_384;
    public int MaxResultBytes { get; set; } = 32_768;
}

public static class AppConfigLoader
{
    public static AppConfig Load(string path)
    {
        if (!File.Exists(path))
        {
            var defaultConfig = new AppConfig();
            var json = JsonSerializer.Serialize(defaultConfig, new JsonSerializerOptions { WriteIndented = true });
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
            File.WriteAllText(path, json);
            return defaultConfig;
        }

        var text = File.ReadAllText(path);
        var config = JsonSerializer.Deserialize<AppConfig>(text, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        return config ?? new AppConfig();
    }
}
