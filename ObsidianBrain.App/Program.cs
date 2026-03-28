using ObsidianBrain.App;
using ObsidianBrain.App.Config;
using ObsidianBrain.App.Data;
using ObsidianBrain.App.Embeddings;
using ObsidianBrain.App.Ingestion;
using ObsidianBrain.App.Mcp;
using ObsidianBrain.App.Retrieval;

var command = args.Length > 0 ? args[0] : "";
var configPath = ResolveConfigPath(args);
var config = AppConfigLoader.Load(configPath);
if (config.Embeddings.Dimensions <= 0)
{
    throw new InvalidOperationException("Embeddings.Dimensions must be greater than 0.");
}

await using var db = new Database(config.Postgres.ConnectionString);
var schema = new SchemaInitializer(db);
await schema.EnsureSchemaAsync(config.Embeddings.Dimensions);

using var embeddingProvider = new OpenAiEmbeddingProvider(config.Embeddings);
var ingestor = new IngestionService(db, embeddingProvider, config);
var search = new SearchService(db, embeddingProvider, config);
var mcpServer = new McpServer(db, search, config);

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

switch (command)
{
    case "scan-once":
        await ingestor.RunOnceAsync("scan-once", cts.Token);
        break;
    case "watch":
        await ingestor.WatchAsync(cts.Token);
        break;
    case "mcp-serve":
        await mcpServer.RunAsync(cts.Token);
        break;
    case "healthcheck":
        await Healthcheck.RunAsync(db, embeddingProvider, cts.Token);
        break;
    default:
        PrintUsage();
        Environment.ExitCode = 1;
        break;
}

static string ResolveConfigPath(string[] args)
{
    var idx = Array.IndexOf(args, "--config");
    if (idx >= 0 && idx + 1 < args.Length)
    {
        return args[idx + 1];
    }

    return Path.Combine(AppContext.BaseDirectory, "appsettings.json");
}

static void PrintUsage()
{
    Console.WriteLine("Usage:");
    Console.WriteLine("  app scan-once --config <path>");
    Console.WriteLine("  app watch --config <path>");
    Console.WriteLine("  app mcp-serve --config <path>");
    Console.WriteLine("  app healthcheck --config <path>");
}
