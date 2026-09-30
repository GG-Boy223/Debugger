using System.Net;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DogeDebugger.Core.AI;

public sealed class McpServer : IAsyncDisposable
{
    private readonly McpToolRegistry _tools;
    private readonly HttpListener _listener = new();
    private CancellationTokenSource? _shutdown;
    private Task? _acceptLoop;
    private bool _disposed;

    public McpServer(McpToolRegistry tools)
    {
        _tools = tools;
    }

    public bool IsRunning { get; private set; }

    public string ListenPrefix { get; private set; } = string.Empty;

    public event EventHandler<string>? Diagnostic;

    public Task StartAsync(
        string listenAddress,
        int port,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsRunning)
        {
            return Task.CompletedTask;
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(listenAddress);
        if (port is < 1 or > 65_535)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }

        string host = listenAddress is "*" or "0.0.0.0" or "::"
            ? "+"
            : listenAddress;
        ListenPrefix = $"http://{host}:{port}/mcp/";
        _listener.Prefixes.Clear();
        _listener.Prefixes.Add(ListenPrefix);
        _listener.Start();
        _shutdown = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_shutdown.Token), CancellationToken.None);
        IsRunning = true;
        Diagnostic?.Invoke(this, $"MCP server listening on {ListenPrefix}");
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        if (!IsRunning)
        {
            return;
        }

        _shutdown?.Cancel();
        _listener.Stop();
        if (_acceptLoop is not null)
        {
            try
            {
                await _acceptLoop.ConfigureAwait(false);
            }
            catch (HttpListenerException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }

        IsRunning = false;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await StopAsync().ConfigureAwait(false);
        _shutdown?.Dispose();
        _listener.Close();
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (HttpListenerException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            _ = Task.Run(
                () => HandleRequestAsync(context, cancellationToken),
                CancellationToken.None);
        }
    }

    private async Task HandleRequestAsync(
        HttpListenerContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!string.Equals(context.Request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
                return;
            }

            if (!string.Equals(context.Request.ContentType, "application/json", StringComparison.OrdinalIgnoreCase) &&
                !context.Request.ContentType?.StartsWith("application/json;", StringComparison.OrdinalIgnoreCase) == true)
            {
                context.Response.StatusCode = (int)HttpStatusCode.UnsupportedMediaType;
                return;
            }

            string requestJson;
            using (StreamReader reader = new(
                       context.Request.InputStream,
                       context.Request.ContentEncoding ?? Encoding.UTF8,
                       detectEncodingFromByteOrderMarks: true,
                       leaveOpen: false))
            {
                requestJson = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            }

            JsonNode? requestNode = JsonNode.Parse(requestJson);
            JsonObject? request = requestNode as JsonObject;
            if (request is null)
            {
                await WriteErrorAsync(
                        context,
                        id: null,
                        code: -32600,
                        message: "Invalid Request",
                        cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            JsonNode? id = request["id"]?.DeepClone();
            string? method = request["method"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(method))
            {
                await WriteErrorAsync(
                        context,
                        id,
                        code: -32600,
                        message: "Method is required.",
                        cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            if (method.StartsWith("notifications/", StringComparison.Ordinal))
            {
                context.Response.StatusCode = (int)HttpStatusCode.Accepted;
                return;
            }

            JsonNode response = method switch
            {
                "initialize" => CreateResult(
                    id,
                    new
                    {
                        protocolVersion = "2024-11-05",
                        capabilities = new { tools = new { listChanged = true } },
                        serverInfo = new { name = "DogeDebugger", version = "5.0.0" }
                    }),
                "ping" => CreateResult(id, new { }),
                "tools/list" => CreateResult(id, new
                {
                    tools = _tools.Tools.Select(tool => new
                    {
                        name = tool.Name,
                        description = tool.Description,
                        inputSchema = tool.InputSchema
                    }).ToArray()
                }),
                "tools/call" => await CallToolAsync(request, id, cancellationToken)
                    .ConfigureAwait(false),
                _ => CreateError(id, -32601, $"Method not found: {method}")
            };

            await WriteJsonAsync(context, response, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Diagnostic?.Invoke(this, exception.ToString());
            if (context.Response.OutputStream.CanWrite)
            {
                await WriteErrorAsync(
                        context,
                        id: null,
                        code: -32603,
                        message: exception.Message,
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            context.Response.Close();
        }
    }

    private async Task<JsonObject> CallToolAsync(
        JsonObject request,
        JsonNode? id,
        CancellationToken cancellationToken)
    {
        JsonObject? parameters = request["params"] as JsonObject;
        string? name = parameters?["name"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(name) || !_tools.TryGet(name, out McpToolDefinition? tool) || tool is null)
        {
            return CreateError(id, -32602, $"Unknown tool: {name}");
        }

        JsonElement arguments = parameters?["arguments"] is JsonNode argumentsNode
            ? JsonSerializer.SerializeToElement(argumentsNode)
            : JsonSerializer.SerializeToElement(new { });

        McpToolResult result = await tool.Handler(arguments, cancellationToken).ConfigureAwait(false);
        List<object> content = [];
        if (!string.IsNullOrEmpty(result.Text))
        {
            content.Add(new { type = "text", text = result.Text });
        }

        JsonObject response = CreateResult(id, new
        {
            content,
            isError = result.IsError,
            structuredContent = result.StructuredContent
        });
        return response;
    }

    private static JsonObject CreateResult(JsonNode? id, object result) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["result"] = JsonSerializer.SerializeToNode(result)
    };

    private static JsonObject CreateError(JsonNode? id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["error"] = new JsonObject
        {
            ["code"] = code,
            ["message"] = message
        }
    };

    private static async Task WriteJsonAsync(
        HttpListenerContext context,
        JsonNode response,
        CancellationToken cancellationToken)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(response.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = false
        }));
        context.Response.StatusCode = (int)HttpStatusCode.OK;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task WriteErrorAsync(
        HttpListenerContext context,
        JsonNode? id,
        int code,
        string message,
        CancellationToken cancellationToken)
    {
        await WriteJsonAsync(context, CreateError(id, code, message), cancellationToken)
            .ConfigureAwait(false);
    }
}
