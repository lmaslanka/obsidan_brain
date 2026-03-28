using System.Text.Json;
using System.Text.Json.Serialization;

namespace ObsidianBrain.App.Mcp;

public sealed class JsonRpcRequest
{
    [JsonPropertyName("jsonrpc")]
    public string Jsonrpc { get; set; } = "2.0";

    [JsonPropertyName("method")]
    public string Method { get; set; } = string.Empty;

    [JsonPropertyName("params")]
    public JsonElement? Params { get; set; }

    [JsonPropertyName("id")]
    public JsonElement? Id { get; set; }
}

public sealed class JsonRpcResponse
{
    [JsonPropertyName("jsonrpc")]
    public string Jsonrpc { get; set; } = "2.0";

    [JsonPropertyName("id")]
    public JsonElement? Id { get; set; }

    [JsonPropertyName("result")]
    public object? Result { get; set; }

    [JsonPropertyName("error")]
    public object? Error { get; set; }
}
