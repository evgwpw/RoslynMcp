using NLog;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RoslynMcp.Mcp;

/// <summary>
/// MCP-сервер поверх stdio (JSON-RPC 2.0, построчное чтение).
/// </summary>
public sealed class McpServer
{
    private const string ProtocolVersion = "2024-11-05";
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    // Не эскейпить не-ASCII символы (кириллицу и т.д.)
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false
    };

    private readonly Dictionary<string, McpToolDef> _tools = new();

    /// <summary>
    /// Регистрирует инструмент.
    /// </summary>
    public void RegisterTool(McpToolDef tool) => _tools[tool.Name] = tool;

    /// <summary>
    /// Запускает цикл обработки сообщений из stdin.
    /// Работает до закрытия stdin или получения shutdown.
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
    {
        using var reader = new StreamReader(Console.OpenStandardInput());
        using var writer = new StreamWriter(Console.OpenStandardOutput())
        {
            AutoFlush = true,
            NewLine = "\n"
        };

        string? line;
        while ((line = await reader.ReadLineAsync(ct)) != null)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            JsonObject? msg;
            try
            {
                msg = JsonNode.Parse(line) as JsonObject;
            }
            catch
            {
                continue;
            }

            if (msg is null)
                continue;

            var method = msg["method"]?.GetValue<string>();
            if (method is null)
                continue;

            var id = msg["id"]?.DeepClone();
            var @params = msg["params"];

            Logger.Debug($"→ {method}{(id is not null ? $" id={id}" : "")}");

            try
            {
                var result = await DispatchAsync(method, @params, ct);

                // На уведомления (без id) ответ не отправляется
                if (id is not null)
                    await WriteResponseAsync(writer, id, result);
            }
            catch (McpToolException ex)
            {
                Logger.Warn($"Ошибка инструмента: {ex.Message}");
                if (id is not null)
                    await WriteToolErrorAsync(writer, id, ex.Message);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, $"Необработанная ошибка: {ex.Message}");

                if (id is not null)
                    await WriteErrorAsync(writer, id, -32603, ex.Message);
            }
        }
    }

    private async Task<JsonNode?> DispatchAsync(
        string method, JsonNode? @params, CancellationToken ct)
    {
        return method switch
        {
            "initialize" => HandleInitialize(),
            "notifications/initialized" => null,
            "tools/list" => HandleToolsList(),
            "tools/call" => await HandleToolsCallAsync(@params, ct),
            "ping" => new JsonObject(),
            _ => throw new Exception($"Неизвестный метод: {method}")
        };
    }

    private static JsonObject HandleInitialize()
    {
        return new JsonObject
        {
            ["protocolVersion"] = ProtocolVersion,
            ["capabilities"] = new JsonObject
            {
                ["tools"] = new JsonObject()
            },
            ["serverInfo"] = new JsonObject
            {
                ["name"] = "roslyn-mcp",
                ["version"] = "1.0.0"
            }
        };
    }

    private JsonObject HandleToolsList()
    {
        var tools = new JsonArray();
        foreach (var (_, tool) in _tools)
        {
            tools.Add(new JsonObject
            {
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["inputSchema"] = tool.InputSchema.DeepClone()
            });
        }
        return new JsonObject { ["tools"] = tools };
    }

    private async Task<JsonObject> HandleToolsCallAsync(
        JsonNode? @params, CancellationToken ct)
    {
        var obj = @params?.AsObject();
        var name = obj?["name"]?.GetValue<string>();
        var arguments = obj?["arguments"]?.AsObject() ?? new JsonObject();

        if (name is null || !_tools.TryGetValue(name, out var tool))
            throw new McpToolException($"Неизвестный инструмент: {name}");

        Logger.Info($"Вызов инструмента: {name}");
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var result = await tool.Handler(arguments, ct);

        sw.Stop();
        Logger.Info($"Инструмент {name} выполнен за {sw.ElapsedMilliseconds} мс");

        return new JsonObject
        {
            ["content"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = result.Text
                }
            },
            ["isError"] = result.IsError
        };
    }

    private static async Task WriteResponseAsync(
        StreamWriter writer, JsonNode id, JsonNode? result)
    {
        var response = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["result"] = result
        };
        await writer.WriteLineAsync(JsonSerializer.Serialize(response, JsonOpts));
    }

    private static async Task WriteToolErrorAsync(
        StreamWriter writer, JsonNode id, string message)
    {
        var response = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["result"] = new JsonObject
            {
                ["content"] = new JsonArray
                {
                    new JsonObject { ["type"] = "text", ["text"] = message }
                },
                ["isError"] = true
            }
        };
        await writer.WriteLineAsync(JsonSerializer.Serialize(response, JsonOpts));
    }

    private static async Task WriteErrorAsync(
        StreamWriter writer, JsonNode id, int code, string message)
    {
        var response = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["error"] = new JsonObject
            {
                ["code"] = code,
                ["message"] = message
            }
        };
        await writer.WriteLineAsync(JsonSerializer.Serialize(response, JsonOpts));
    }
}
