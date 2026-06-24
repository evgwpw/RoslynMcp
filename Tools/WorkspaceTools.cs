using RoslynMcp.Mcp;
using RoslynMcp.Services;
using System.Text.Json.Nodes;

namespace RoslynMcp.Tools;

/// <summary>
/// Инструменты управления workspace: open_solution, open_project,
/// get_workspace_info, reload_workspace.
/// </summary>
public static class WorkspaceTools
{
    public static void Register(McpServer server, WorkspaceService ws)
    {
        // --- open_solution ---
        server.RegisterTool(new McpToolDef(
            "open_solution",
            "Открыть .sln файл. Загружает все проекты в Roslyn workspace. " +
            "Параметр: path — абсолютный путь к .sln.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["path"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Абсолютный путь к .sln файлу"
                    }
                },
                ["required"] = new JsonArray { "path" }
            },
            async (args, ct) =>
            {
                var path = args["path"]?.GetValue<string>()
                    ?? throw new McpToolException("Параметр 'path' обязателен");

                if (!File.Exists(path))
                    throw new McpToolException($"Файл не найден: {path}");

                await ws.OpenSolutionAsync(path, ct);
                return McpToolResult.Ok($"Решение загружено: {path}");
            }
        ));

        // --- open_project ---
        server.RegisterTool(new McpToolDef(
            "open_project",
            "Открыть отдельный .csproj без решения. " +
            "Параметр: path — абсолютный путь к .csproj.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["path"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Абсолютный путь к .csproj файлу"
                    }
                },
                ["required"] = new JsonArray { "path" }
            },
            async (args, ct) =>
            {
                var path = args["path"]?.GetValue<string>()
                    ?? throw new McpToolException("Параметр 'path' обязателен");

                if (!File.Exists(path))
                    throw new McpToolException($"Файл не найден: {path}");

                await ws.OpenProjectAsync(path, ct);
                return McpToolResult.Ok($"Проект загружен: {path}");
            }
        ));

        // --- get_workspace_info ---
        server.RegisterTool(new McpToolDef(
            "get_workspace_info",
            "Возвращает информацию о текущем workspace: путь к решению, " +
            "список проектов с целевыми фреймворками, документами и ссылками.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject()
            },
            (_, _) =>
            {
                var info = ws.GetWorkspaceInfo();

                // Формируем читаемый текстовый отчёт
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"Решение: {info.SolutionPath}");
                sb.AppendLine($"Проектов: {info.Projects.Count}");
                sb.AppendLine();

                foreach (var p in info.Projects)
                {
                    sb.AppendLine($"## {p.Name}");
                    sb.AppendLine($"  Путь: {p.FilePath}");
                    sb.AppendLine($"  LangVersion: {p.TargetFramework}");
                    sb.AppendLine($"  Документов: {p.Documents.Count}");
                    sb.AppendLine($"  Project refs: {p.ProjectReferences.Count}");
                    sb.AppendLine($"  Package/assembly refs: {p.PackageReferences.Count}");
                    if (p.ProjectReferences.Count > 0)
                        sb.AppendLine($"  → {string.Join(", ", p.ProjectReferences)}");
                    sb.AppendLine();
                }

                return Task.FromResult(McpToolResult.Ok(sb.ToString()));
            }
        ));

        // --- reload_workspace ---
        server.RegisterTool(new McpToolDef(
            "reload_workspace",
            "Перезагрузить решение/проект с диска. " +
            "Полностью пересоздаёт workspace. Использовать после изменений в коде.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject()
            },
            async (_, ct) =>
            {
                await ws.ReloadAsync(ct);
                return McpToolResult.Ok("Workspace перезагружен");
            }
        ));
    }
}
