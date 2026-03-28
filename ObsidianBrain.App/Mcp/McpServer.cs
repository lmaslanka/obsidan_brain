using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using ObsidianBrain.App.Config;
using ObsidianBrain.App.Data;
using ObsidianBrain.App.Retrieval;

namespace ObsidianBrain.App.Mcp;

public sealed class McpServer(Database db, SearchService search, AppConfig config)
{
    private readonly SearchService _search = search;
    private readonly AppConfig _config = config;
    private readonly McpAuditLogger _audit = new(db, config);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var prefix = $"http://{_config.Mcp.BindHost}:{_config.Mcp.Port}/";
        using var listener = new HttpListener();
        listener.Prefixes.Add(prefix);
        listener.Start();

        Console.WriteLine($"MCP server listening on {prefix}mcp");
        using var registration = cancellationToken.Register(() =>
        {
            if (listener.IsListening)
            {
                listener.Stop();
            }
        });

        while (true)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync();
            }
            catch (HttpListenerException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            _ = Task.Run(() => HandleContextAsync(context, cancellationToken));
        }
    }

    private async Task HandleContextAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        try
        {
            if (context.Request.HttpMethod != "POST" || context.Request.Url?.AbsolutePath != "/mcp")
            {
                context.Response.StatusCode = 404;
                context.Response.Close();
                return;
            }

            var requestJson = await new StreamReader(context.Request.InputStream).ReadToEndAsync(cancellationToken);
            var request = JsonSerializer.Deserialize<JsonRpcRequest>(requestJson, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (request is null)
            {
                await WriteJsonAsync(context, new JsonRpcResponse { Error = new { code = -32700, message = "Invalid JSON" } }, cancellationToken);
                return;
            }

            var result = await HandleRequestAsync(request, requestJson, context, cancellationToken);
            if (result is null)
            {
                context.Response.StatusCode = 202;
                context.Response.Close();
                return;
            }

            await WriteJsonAsync(context, result, cancellationToken);
        }
        catch (Exception ex)
        {
            await WriteJsonAsync(context, new JsonRpcResponse
            {
                Error = new { code = -32000, message = ex.Message }
            }, cancellationToken);
        }
    }

    private async Task<JsonRpcResponse?> HandleRequestAsync(JsonRpcRequest request, string rawRequest, HttpListenerContext context, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var toolName = request.Method;
        Guid callId = Guid.Empty;
        var isNotification = request.Id is null;

        try
        {
            callId = await _audit.StartCallAsync(toolName, request.Params, request.Id?.ToString(), context.Request.RemoteEndPoint?.ToString(), cancellationToken);

            object result = request.Method switch
            {
                "initialize" => new
                {
                    protocolVersion = "2024-11-05",
                    capabilities = new
                    {
                        tools = new { }
                    },
                    serverInfo = new
                    {
                        name = "obsidian-brain",
                        version = "1.0.0"
                    }
                },
                "notifications/initialized" => new { ok = true },
                "ping" => new { },
                "tools/list" => BuildTools(),
                "tools/call" => await HandleToolCallAsync(request.Params, cancellationToken),
                _ => throw new InvalidOperationException($"Unknown method {request.Method}")
            };

            var raw = JsonSerializer.Serialize(result);
            await _audit.FinishCallAsync(callId, "success", BuildSummary(result), raw, (int)stopwatch.ElapsedMilliseconds, null, null, cancellationToken);
            if (isNotification)
            {
                return null;
            }

            return new JsonRpcResponse { Id = request.Id, Result = result };
        }
        catch (Exception ex)
        {
            if (callId != Guid.Empty)
            {
                await _audit.FinishCallAsync(callId, "error", new { message = ex.Message }, ex.ToString(), (int)stopwatch.ElapsedMilliseconds, "tool_error", ex.Message, cancellationToken);
            }
            if (isNotification)
            {
                return null;
            }

            return new JsonRpcResponse
            {
                Id = request.Id,
                Error = new { code = -32001, message = ex.Message }
            };
        }
    }

    private object BuildTools()
    {
        return new
        {
            tools = new object[]
            {
                new
                {
                    name = "notes.search",
                    description = "Hybrid search over indexed notes",
                    inputSchema = new
                    {
                        type = "object",
                        properties = new
                        {
                            query = new { type = "string" },
                            topK = new { type = "integer", minimum = 1 }
                        },
                        required = new[] { "query" }
                    }
                },
                new
                {
                    name = "notes.get_document",
                    description = "Get full document by path",
                    inputSchema = new
                    {
                        type = "object",
                        properties = new
                        {
                            path = new { type = "string" }
                        },
                        required = new[] { "path" }
                    }
                },
                new
                {
                    name = "notes.get_snippets",
                    description = "Search and return snippets",
                    inputSchema = new
                    {
                        type = "object",
                        properties = new
                        {
                            query = new { type = "string" },
                            topK = new { type = "integer", minimum = 1 }
                        },
                        required = new[] { "query" }
                    }
                },
                new
                {
                    name = "notes.list_recent",
                    description = "List recently modified notes",
                    inputSchema = new
                    {
                        type = "object",
                        properties = new
                        {
                            limit = new { type = "integer", minimum = 1 }
                        }
                    }
                }
            }
        };
    }

    private async Task<object> HandleToolCallAsync(JsonElement? toolCall, CancellationToken cancellationToken)
    {
        if (toolCall is null)
        {
            throw new InvalidOperationException("Missing params for tools/call");
        }

        var name = toolCall.Value.GetProperty("name").GetString();
        var arguments = toolCall.Value.TryGetProperty("arguments", out var args) ? args : default;

        var result = name switch
        {
            "notes.search" => await HandleSearchAsync(arguments, cancellationToken),
            "notes.get_snippets" => await HandleSearchAsync(arguments, cancellationToken),
            "notes.get_document" => await HandleGetDocumentAsync(arguments, cancellationToken),
            "notes.list_recent" => await HandleRecentAsync(arguments, cancellationToken),
            _ => throw new InvalidOperationException($"Unknown tool: {name}")
        };

        return new
        {
            content = new[]
            {
                new
                {
                    type = "text",
                    text = JsonSerializer.Serialize(result)
                }
            },
            structuredContent = result,
            isError = false
        };
    }

    private async Task<object> HandleSearchAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var query = arguments.TryGetProperty("query", out var q) ? q.GetString() : null;
        if (string.IsNullOrWhiteSpace(query))
        {
            throw new InvalidOperationException("query is required");
        }

        var topK = arguments.TryGetProperty("topK", out var tk) ? tk.GetInt32() : _config.Mcp.DefaultTopK;
        var results = await _search.SearchAsync(query, topK, cancellationToken);

        return new
        {
            query,
            count = results.Count,
            results = results.Select(r => new
            {
                path = r.Path,
                title = r.Title,
                heading = r.HeadingPath,
                snippet = r.Snippet,
                chunkId = r.ChunkId,
                score = r.Score
            })
        };
    }

    private async Task<object> HandleGetDocumentAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var path = arguments.TryGetProperty("path", out var p) ? p.GetString() : null;
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException("path is required");
        }

        var content = await _search.GetDocumentAsync(path, cancellationToken);
        return new { path, found = content is not null, content };
    }

    private async Task<object> HandleRecentAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var limit = arguments.TryGetProperty("limit", out var p) ? p.GetInt32() : 20;
        var recent = await _search.ListRecentAsync(limit, cancellationToken);
        return new { count = recent.Count, paths = recent };
    }

    private static object BuildSummary(object result)
    {
        var asJson = JsonSerializer.Serialize(result);
        return new
        {
            resultType = result.GetType().Name,
            sizeBytes = Encoding.UTF8.GetByteCount(asJson)
        };
    }

    private static async Task WriteJsonAsync(HttpListenerContext context, JsonRpcResponse response, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(response);
        var bytes = Encoding.UTF8.GetBytes(json);
        context.Response.StatusCode = 200;
        context.Response.ContentType = "application/json";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes, cancellationToken);
        context.Response.Close();
    }
}
