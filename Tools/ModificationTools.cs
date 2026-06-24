using Microsoft.CodeAnalysis;
using RoslynMcp.Mcp;
using RoslynMcp.Services;
using System.Text;
using System.Text.Json.Nodes;

namespace RoslynMcp.Tools;

/// <summary>
/// Инструменты изменения кода:
/// apply_text_change, rename_symbol, get_modified_documents,
/// save_document, save_all_changes.
/// </summary>
public static class ModificationTools
{
    public static void Register(McpServer server, WorkspaceService ws)
    {
        // --- apply_text_change ---
        server.RegisterTool(new McpToolDef(
            "apply_text_change",
            "Заменить диапазон текста в документе (в памяти, без записи на диск). " +
            "Координаты 1-based. После изменения используйте save_document или save_all_changes. " +
            "Параметры: filePath, startLine, startColumn, endLine, endColumn, newText.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["filePath"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Путь к файлу .cs"
                    },
                    ["startLine"] = new JsonObject
                    {
                        ["type"] = "integer",
                        ["description"] = "Начальная строка (1-based)"
                    },
                    ["startColumn"] = new JsonObject
                    {
                        ["type"] = "integer",
                        ["description"] = "Начальная колонка (1-based)"
                    },
                    ["endLine"] = new JsonObject
                    {
                        ["type"] = "integer",
                        ["description"] = "Конечная строка (1-based)"
                    },
                    ["endColumn"] = new JsonObject
                    {
                        ["type"] = "integer",
                        ["description"] = "Конечная колонка (1-based)"
                    },
                    ["newText"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Новый текст для замены диапазона"
                    }
                },
                ["required"] = new JsonArray { "filePath", "startLine", "startColumn", "endLine", "endColumn", "newText" }
            },
            async (args, ct) =>
            {
                var filePath = args["filePath"]?.GetValue<string>()
                    ?? throw new McpToolException("Параметр 'filePath' обязателен");
                var startLine = args["startLine"]?.GetValue<int>()
                    ?? throw new McpToolException("Параметр 'startLine' обязателен");
                var startColumn = args["startColumn"]?.GetValue<int>()
                    ?? throw new McpToolException("Параметр 'startColumn' обязателен");
                var endLine = args["endLine"]?.GetValue<int>()
                    ?? throw new McpToolException("Параметр 'endLine' обязателен");
                var endColumn = args["endColumn"]?.GetValue<int>()
                    ?? throw new McpToolException("Параметр 'endColumn' обязателен");
                var newText = args["newText"]?.GetValue<string>()
                    ?? throw new McpToolException("Параметр 'newText' обязателен");

                await ws.ApplyTextChangeAsync(
                    filePath, startLine, startColumn, endLine, endColumn, newText, ct);

                var sb = new StringBuilder();
                sb.AppendLine($"Изменение применено (в памяти):");
                sb.AppendLine($"  Файл: {filePath}");
                sb.AppendLine($"  Диапазон: {startLine}:{startColumn} — {endLine}:{endColumn}");
                sb.AppendLine($"  Новый текст: {newText.Length} символов");
                sb.AppendLine();
                sb.AppendLine("Вызовите save_document или save_all_changes для записи на диск.");

                return McpToolResult.Ok(sb.ToString());
            }
        ));

        // --- rename_symbol ---
        server.RegisterTool(new McpToolDef(
            "rename_symbol",
            "Переименовать символ во всём solution (Roslyn rename refactoring). " +
            "Изменяет все ссылки. Изменения в памяти до save_all_changes. " +
            "Параметры: filePath, line, column — позиция символа, newName — новое имя.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["filePath"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Путь к файлу .cs"
                    },
                    ["line"] = new JsonObject
                    {
                        ["type"] = "integer",
                        ["description"] = "Номер строки (1-based)"
                    },
                    ["column"] = new JsonObject
                    {
                        ["type"] = "integer",
                        ["description"] = "Номер колонки (1-based)"
                    },
                    ["newName"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Новое имя символа"
                    }
                },
                ["required"] = new JsonArray { "filePath", "line", "column", "newName" }
            },
            async (args, ct) =>
            {
                var filePath = args["filePath"]?.GetValue<string>()
                    ?? throw new McpToolException("Параметр 'filePath' обязателен");
                var line = args["line"]?.GetValue<int>()
                    ?? throw new McpToolException("Параметр 'line' обязателен");
                var column = args["column"]?.GetValue<int>()
                    ?? throw new McpToolException("Параметр 'column' обязателен");
                var newName = args["newName"]?.GetValue<string>()
                    ?? throw new McpToolException("Параметр 'newName' обязателен");

                var doc = ws.FindDocument(filePath)
                    ?? throw new McpToolException($"Документ не найден: {filePath}");

                var (token, _) = await SymbolFormatter.GetTokenAtPositionAsync(doc, line, column);
                var (_, changedDocs) = await ws.RenameSymbolAsync(doc, token.SpanStart, newName, ct);

                var sb = new StringBuilder();
                sb.AppendLine($"Символ переименован в '{newName}'.");
                sb.AppendLine($"Изменено документов: {changedDocs}");
                sb.AppendLine();
                sb.AppendLine("Вызовите save_all_changes для записи на диск.");

                return McpToolResult.Ok(sb.ToString());
            }
        ));

        // --- get_modified_documents ---
        server.RegisterTool(new McpToolDef(
            "get_modified_documents",
            "Список документов с несохранёнными изменениями в workspace.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject()
            },
            async (_, ct) =>
            {
                var modified = await ws.GetModifiedDocumentsAsync(ct);

                if (modified.Count == 0)
                    return McpToolResult.Ok("Нет несохранённых изменений.");

                var sb = new StringBuilder();
                sb.AppendLine($"Несохранённых документов: {modified.Count}");
                foreach (var f in modified)
                    sb.AppendLine($"  {f}");

                return McpToolResult.Ok(sb.ToString());
            }
        ));

        // --- save_document ---
        server.RegisterTool(new McpToolDef(
            "save_document",
            "Записать изменения одного документа на диск. " +
            "Параметр: filePath — путь к .cs файлу.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["filePath"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Путь к файлу .cs"
                    }
                },
                ["required"] = new JsonArray { "filePath" }
            },
            async (args, ct) =>
            {
                var filePath = args["filePath"]?.GetValue<string>()
                    ?? throw new McpToolException("Параметр 'filePath' обязателен");

                await ws.SaveDocumentAsync(filePath, ct);
                return McpToolResult.Ok($"Документ сохранён: {filePath}");
            }
        ));

        // --- save_all_changes ---
        server.RegisterTool(new McpToolDef(
            "save_all_changes",
            "Записать все несохранённые изменения solution на диск.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject()
            },
            async (_, ct) =>
            {
                var saved = await ws.SaveAllChangesAsync(ct);
                return McpToolResult.Ok($"Сохранено файлов: {saved}");
            }
        ));
    }
}
