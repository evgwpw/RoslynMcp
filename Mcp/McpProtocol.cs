using System.Text.Json.Nodes;

namespace RoslynMcp.Mcp;

/// <summary>
/// Делегат обработчика инструмента MCP.
/// </summary>
public delegate Task<McpToolResult> McpToolHandler(JsonObject args, CancellationToken ct);

/// <summary>
/// Определение инструмента для регистрации на сервере.
/// </summary>
public sealed record McpToolDef(
    string Name,
    string Description,
    JsonObject InputSchema,
    McpToolHandler Handler);

/// <summary>
/// Результат выполнения инструмента.
/// </summary>
public sealed record McpToolResult(string Text, bool IsError = false)
{
    public static McpToolResult Ok(string text) => new(text);
    public static McpToolResult Error(string message) => new(message, IsError: true);
}

/// <summary>
/// Ошибка уровня инструмента — отправляется клиенту с isError=true.
/// Отличается от серверной ошибки (JSON-RPC error).
/// </summary>
public sealed class McpToolException : Exception
{
    public McpToolException(string message) : base(message) { }
}
