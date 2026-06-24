using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Options;
using RoslynMcp.Mcp;
using RoslynMcp.Services;
using System.Text;
using System.Text.Json.Nodes;

namespace RoslynMcp.Tools;

/// <summary>
/// Дополнительные инструменты:
/// format_document, get_project_references, get_signature_help.
/// </summary>
public static class AdditionalTools
{
    public static void Register(McpServer server, WorkspaceService ws)
    {
        // --- format_document ---
        server.RegisterTool(new McpToolDef(
            "format_document",
            "Применить форматирование Roslyn к документу. " +
            "Возвращает отформатированный текст. " +
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

                var doc = ws.FindDocument(filePath)
                    ?? throw new McpToolException($"Документ не найден: {filePath}");

                var root = await doc.GetSyntaxRootAsync(ct)
                    ?? throw new McpToolException("Нет синтаксического дерева");

                // Форматирование через Formatter
                var formattedRoot = Formatter.Format(root, ws.Workspace!);
                var formattedText = formattedRoot.ToFullString();

                // Проверяем, были ли изменения
                var originalText = (await doc.GetTextAsync(ct)).ToString();
                var changed = originalText != formattedText;

                var sb = new StringBuilder();
                sb.AppendLine(formattedText);
                sb.AppendLine($"\n--- Форматирование применено. Изменения: {(changed ? "да" : "нет")} ---");

                return McpToolResult.Ok(sb.ToString());
            }
        ));

        // --- get_project_references ---
        server.RegisterTool(new McpToolDef(
            "get_project_references",
            "Project-to-project и package references проекта. " +
            "Параметр: optional projectName — имя проекта " +
            "(если не указан, выводит все проекты в solution).",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["projectName"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Имя проекта (если не указан — все проекты)"
                    }
                }
            },
            async (args, ct) =>
            {
                var sol = ws.RequireSolution();
                var projectName = args["projectName"]?.GetValue<string>();

                var projects = sol.Projects.ToList();
                if (projectName is not null)
                {
                    projects = projects.Where(p =>
                        string.Equals(p.Name, projectName, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (projects.Count == 0)
                        throw new McpToolException($"Проект не найден: {projectName}");
                }

                var sb = new StringBuilder();
                foreach (var project in projects)
                {
                    sb.AppendLine($"## {project.Name}");
                    sb.AppendLine($"  Путь: {project.FilePath}");
                    var tfm = project.ParseOptions is CSharpParseOptions cs
                        ? cs.LanguageVersion.ToString()
                        : "unknown";
                    sb.AppendLine($"  TargetFramework: {tfm}");

                    // Project references
                    var projRefs = project.ProjectReferences.ToList();
                    sb.AppendLine($"  Project references ({projRefs.Count}):");
                    foreach (var pr in projRefs)
                    {
                        var refProject = sol.GetProject(pr.ProjectId);
                        sb.AppendLine($"    → {refProject?.Name ?? pr.ProjectId.ToString()}");
                    }

                    // Package/assembly references
                    var metaRefs = project.MetadataReferences.ToList();
                    var packages = metaRefs
                        .Where(r => !string.IsNullOrEmpty(r.Display))
                        .Select(r => r.Display!)
                        .OrderBy(n => n)
                        .ToList();

                    // Группируем по типу: System, NuGet, прочие
                    var systemRefs = packages.Where(n => n.StartsWith("System") || n.StartsWith("Microsoft")).ToList();
                    var otherRefs = packages.Except(systemRefs).ToList();

                    sb.AppendLine($"  References ({packages.Count}):");
                    if (systemRefs.Count > 0)
                    {
                        sb.AppendLine("    Системные:");
                        foreach (var r in systemRefs)
                            sb.AppendLine($"      {r}");
                    }
                    if (otherRefs.Count > 0)
                    {
                        sb.AppendLine("    Прочие:");
                        foreach (var r in otherRefs)
                            sb.AppendLine($"      {r}");
                    }

                    // Documents
                    var docs = project.Documents
                        .Where(d => d.FilePath is not null)
                        .Select(d => d.FilePath!)
                        .OrderBy(d => d)
                        .ToList();
                    sb.AppendLine($"  Documents ({docs.Count}):");
                    foreach (var d in docs)
                        sb.AppendLine($"    {d}");

                    sb.AppendLine();
                }

                return McpToolResult.Ok(sb.ToString());
            }
        ));

        // --- get_signature_help ---
        server.RegisterTool(new McpToolDef(
            "get_signature_help",
            "Сигнатуры перегрузок метода в точке вызова. " +
            "Параметры: filePath, line, column — позиция внутри вызова (скобки).",
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
                    }
                },
                ["required"] = new JsonArray { "filePath", "line", "column" }
            },
            async (args, ct) =>
            {
                var filePath = args["filePath"]?.GetValue<string>()
                    ?? throw new McpToolException("Параметр 'filePath' обязателен");
                var line = args["line"]?.GetValue<int>()
                    ?? throw new McpToolException("Параметр 'line' обязателен");
                var column = args["column"]?.GetValue<int>()
                    ?? throw new McpToolException("Параметр 'column' обязателен");

                var doc = ws.FindDocument(filePath)
                    ?? throw new McpToolException($"Документ не найден: {filePath}");

                var (token, root) = await SymbolFormatter.GetTokenAtPositionAsync(doc, line, column);
                var model = await doc.GetSemanticModelAsync(ct)
                    ?? throw new McpToolException("Не удалось получить семантическую модель");

                // Находим вызов, в котором находится позиция
                var node = token.Parent;
                InvocationExpressionSyntax? invocation = null;
                ObjectCreationExpressionSyntax? creation = null;

                while (node is not null)
                {
                    if (node is InvocationExpressionSyntax inv)
                    {
                        invocation = inv;
                        break;
                    }
                    if (node is ObjectCreationExpressionSyntax cre)
                    {
                        creation = cre;
                        break;
                    }
                    node = node.Parent;
                }

                if (invocation is null && creation is null)
                    return McpToolResult.Ok("Позиция не находится внутри вызова метода или конструктора");

                // Получаем информацию о символе
                SymbolInfo info;
                if (invocation is not null)
                    info = model.GetSymbolInfo(invocation);
                else
                    info = model.GetSymbolInfo(creation!);

                var sb = new StringBuilder();

                // Выбранный символ (если Ambiguous — берём первый кандидат)
                var selected = info.Symbol;
                var candidates = info.CandidateSymbols;

                if (selected is null && candidates.Length > 0)
                    selected = candidates[0];

                if (selected is null)
                    return McpToolResult.Ok("Не удалось определить метод. Возможно, вызов некорректен.");

                var methodGroup = selected is IMethodSymbol m
                    ? m.ContainingType.GetMembers(selected.Name)
                        .OfType<IMethodSymbol>()
                        .ToList()
                    : new List<IMethodSymbol>();

                if (methodGroup.Count == 0 && selected is IMethodSymbol single)
                    methodGroup.Add(single);

                sb.AppendLine($"Метод: {selected.Name}");
                sb.AppendLine($"Перегрузок: {methodGroup.Count}");
                sb.AppendLine();

                for (var i = 0; i < methodGroup.Count; i++)
                {
                    var method = methodGroup[i];
                    var isCurrent = selected.Equals(method, SymbolEqualityComparer.Default);
                    var marker = isCurrent ? " ← выбранная" : "";

                    var mods = new List<string>();
                    if (method.IsStatic) mods.Add("static");
                    if (method.IsAsync) mods.Add("async");
                    if (method.IsExtensionMethod) mods.Add("extension");

                    var returnType = method.ReturnsVoid ? "void" : method.ReturnType.ToDisplayString();
                    var parameters = string.Join(", ",
                        method.Parameters.Select(p =>
                        {
                            var ps = $"{p.Type.ToDisplayString()} {p.Name}";
                            if (p.IsOptional) ps += " = ...";
                            if (p.RefKind == RefKind.Ref) ps = "ref " + ps;
                            if (p.RefKind == RefKind.Out) ps = "out " + ps;
                            if (p.RefKind == RefKind.In) ps = "in " + ps;
                            if (p.IsParams) ps = "params " + ps;
                            return ps;
                        }));

                    var modStr = mods.Count > 0 ? string.Join(" ", mods) + " " : "";
                    sb.AppendLine($"  {i + 1}. {modStr}{returnType} {method.Name}({parameters}){marker}");
                }

                return McpToolResult.Ok(sb.ToString());
            }
        ));
    }
}
