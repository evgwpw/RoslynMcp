using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using RoslynMcp.Mcp;
using RoslynMcp.Services;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace RoslynMcp.Tools;

/// <summary>
/// Инструменты навигации по символам:
/// find_symbol, go_to_definition, find_references,
/// find_implementations, get_type_hierarchy.
/// </summary>
public static class NavigationTools
{
    public static void Register(McpServer server, WorkspaceService ws)
    {
        // --- find_symbol ---
        server.RegisterTool(new McpToolDef(
            "find_symbol",
            "Поиск символов по имени (wildcards/regex). " +
            "Возвращает: имя, вид, файл, позицию, XML-документацию. " +
            "Параметры: pattern — шаблон имени (regex), " +
            "optional offset (по умолч. 0), optional maxResults (по умолч. 50).",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["pattern"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Regex-шаблон имени символа"
                    },
                    ["offset"] = new JsonObject
                    {
                        ["type"] = "integer",
                        ["description"] = "Смещение для пагинации (по умолч. 0)",
                        ["default"] = 0
                    },
                    ["maxResults"] = new JsonObject
                    {
                        ["type"] = "integer",
                        ["description"] = "Максимум результатов (по умолч. 50)",
                        ["default"] = 50
                    }
                },
                ["required"] = new JsonArray { "pattern" }
            },
            async (args, ct) =>
            {
                var pattern = args["pattern"]?.GetValue<string>()
                    ?? throw new McpToolException("Параметр 'pattern' обязателен");
                var offset = Math.Max(0, args["offset"]?.GetValue<int>() ?? 0);
                var maxResults = args["maxResults"]?.GetValue<int>() ?? 50;

                var sol = ws.RequireSolution();

                // Поиск объявлений в исходниках решения
                var symbols = await SymbolFinder.FindSourceDeclarationsAsync(
                    sol, regex => Regex.IsMatch(regex, pattern, RegexOptions.IgnoreCase), ct);

                // Материализуем один раз — избегаем двойного перечисления
                var all = symbols.ToList();
                var page = all.Skip(offset).Take(maxResults).ToList();
                var shown = page.Count;
                var total = all.Count;
                var nextOffset = offset + shown;

                var sb = new StringBuilder();
                sb.AppendLine($"Найдено символов: {total}");
                if (total > shown)
                    sb.AppendLine($"Показано: {shown} (offset={offset}, maxResults={maxResults})" +
                                  $"{(nextOffset < total ? $", следующая страница: offset={nextOffset}" : "")}");
                sb.AppendLine();

                foreach (var sym in page)
                {
                    sb.Append(SymbolFormatter.FormatSymbol(sym, includeDoc: true));
                    sb.AppendLine();
                }

                return McpToolResult.Ok(sb.ToString());
            }
        ));

        // --- go_to_definition ---
        server.RegisterTool(new McpToolDef(
            "go_to_definition",
            "По файлу и позиции (строка/колонка, 1-based) — найти объявление символа. " +
            "Параметры: filePath, line, column.",
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
                    ?? throw new McpToolException($"Документ не найден в workspace: {filePath}");

                var (token, _) = await SymbolFormatter.GetTokenAtPositionAsync(doc, line, column);
                var model = await doc.GetSemanticModelAsync(ct)
                    ?? throw new McpToolException("Не удалось получить семантическую модель");

                var symInfo = await SymbolFinder.FindSymbolAtPositionAsync(model, token.SpanStart, ws.Workspace!);
                if (symInfo is null)
                    return McpToolResult.Ok("Символ не найден в данной позиции");

                // OriginalDefinition — для generic-методов и типов
                var target = symInfo.OriginalDefinition;

                var sb = new StringBuilder();
                sb.AppendLine("Объявление символа:");
                sb.AppendLine();
                sb.Append(SymbolFormatter.FormatSymbol(target, includeDoc: true));

                // Если определение в метаданных (внешняя сборка)
                var loc = SymbolFormatter.GetPrimaryLocation(target);
                if (loc is not null && !loc.IsInSource)
                    sb.AppendLine("  (определение в метаданных / внешней сборке)");

                return McpToolResult.Ok(sb.ToString());
            }
        ));

        // --- find_references ---
        server.RegisterTool(new McpToolDef(
            "find_references",
            "Найти все обращения к символу по файлу и позиции. " +
            "Параметры: filePath, line, column, " +
            "optional offset (по умолч. 0), optional maxResults (по умолч. 100) — " +
            "пагинация списка ссылок.",
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
                    ["offset"] = new JsonObject
                    {
                        ["type"] = "integer",
                        ["description"] = "Смещение для пагинации (по умолч. 0)",
                        ["default"] = 0
                    },
                    ["maxResults"] = new JsonObject
                    {
                        ["type"] = "integer",
                        ["description"] = "Максимум ссылок (по умолч. 100)",
                        ["default"] = 100
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
                var offset = Math.Max(0, args["offset"]?.GetValue<int>() ?? 0);
                var maxResults = args["maxResults"]?.GetValue<int>() ?? 100;

                var sol = ws.RequireSolution();
                var doc = ws.FindDocument(filePath)
                    ?? throw new McpToolException($"Документ не найден: {filePath}");

                var (token, _) = await SymbolFormatter.GetTokenAtPositionAsync(doc, line, column);
                var model = await doc.GetSemanticModelAsync(ct)
                    ?? throw new McpToolException("Не удалось получить семантическую модель");

                var symbol = await SymbolFinder.FindSymbolAtPositionAsync(model, token.SpanStart, ws.Workspace!);
                if (symbol is null)
                    return McpToolResult.Ok("Символ не найден в данной позиции");

                var refs = await SymbolFinder.FindReferencesAsync(symbol, sol, ct);

                // Собираем все локации ссылок
                var allLocs = refs.SelectMany(r => r.Locations).ToList();
                var total = allLocs.Count;
                var page = allLocs.Skip(offset).Take(maxResults).ToList();
                var shown = page.Count;
                var nextOffset = offset + shown;

                var sb = new StringBuilder();
                sb.AppendLine($"Символ: {symbol.ToDisplayString()}");
                sb.AppendLine($"Мест использования: {total}");
                if (total > shown)
                    sb.AppendLine($"Показано: {shown} (offset={offset}, maxResults={maxResults})" +
                                  $"{(nextOffset < total ? $", следующая страница: offset={nextOffset}" : "")}");
                sb.AppendLine();

                foreach (var loc in page)
                {
                    var lineSpan = loc.Location.GetLineSpan();
                    // Получаем строку кода для контекста
                    var sourceTree = loc.Location.SourceTree;
                    var codeLine = "";
                    if (sourceTree is not null)
                    {
                        var text = await sourceTree.GetTextAsync(ct);
                        codeLine = text.Lines[lineSpan.StartLinePosition.Line].ToString().Trim();
                    }
                    sb.AppendLine($"  {loc.Location.SourceTree?.FilePath}:{lineSpan.StartLinePosition.Line + 1}");
                    sb.AppendLine($"    {codeLine}");
                }

                return McpToolResult.Ok(sb.ToString());
            }
        ));

        // --- find_implementations ---
        server.RegisterTool(new McpToolDef(
            "find_implementations",
            "Найти реализации интерфейса / наследники класса / override-ы метода " +
            "по файлу и позиции. Параметры: filePath, line, column, " +
            "optional offset (по умолч. 0), optional limit (по умолч. 100) — " +
            "пагинация списка результатов.",
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
                    ["offset"] = new JsonObject
                    {
                        ["type"] = "integer",
                        ["description"] = "Смещение для пагинации (по умолч. 0)",
                        ["default"] = 0
                    },
                    ["limit"] = new JsonObject
                    {
                        ["type"] = "integer",
                        ["description"] = "Максимум результатов (по умолч. 100)",
                        ["default"] = 100
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
                var offset = Math.Max(0, args["offset"]?.GetValue<int>() ?? 0);
                var limit = args["limit"]?.GetValue<int>() ?? 100;

                var sol = ws.RequireSolution();
                var doc = ws.FindDocument(filePath)
                    ?? throw new McpToolException($"Документ не найден: {filePath}");

                var (token, _) = await SymbolFormatter.GetTokenAtPositionAsync(doc, line, column);
                var model = await doc.GetSemanticModelAsync(ct)
                    ?? throw new McpToolException("Не удалось получить семантическую модель");

                var symbol = await SymbolFinder.FindSymbolAtPositionAsync(model, token.SpanStart, ws.Workspace!);
                if (symbol is null)
                    return McpToolResult.Ok("Символ не найден в данной позиции");

                var sb = new StringBuilder();
                sb.AppendLine($"Символ: {symbol.ToDisplayString()}");
                sb.AppendLine();

                switch (symbol)
                {
                    case INamedTypeSymbol type when type.TypeKind == TypeKind.Interface:
                    {
                        var impls = await SymbolFinder.FindImplementationsAsync(type, sol, cancellationToken: ct);
                        AppendPaged(sb, "Реализации интерфейса", impls, offset, limit,
                            impl => SymbolFormatter.FormatSymbolCompact(impl));
                        break;
                    }
                    case INamedTypeSymbol type when type.TypeKind == TypeKind.Class:
                    {
                        var derived = await SymbolFinder.FindDerivedClassesAsync(type, sol, cancellationToken: ct);
                        AppendPaged(sb, "Наследники класса", derived, offset, limit,
                            d => SymbolFormatter.FormatSymbolCompact(d));
                        break;
                    }
                    case IMethodSymbol method:
                    {
                        var overrides = await SymbolFinder.FindOverridesAsync(method, sol, cancellationToken: ct);
                        AppendPaged(sb, "Override-ы метода", overrides, offset, limit,
                            o => SymbolFormatter.FormatSymbolCompact(o));
                        break;
                    }
                    default:
                        sb.AppendLine("Символ не поддерживает поиск реализаций " +
                                      "(поддерживаются интерфейсы, классы, методы).");
                        break;
                }

                return McpToolResult.Ok(sb.ToString());
            }
        ));

        // --- get_type_hierarchy ---
        server.RegisterTool(new McpToolDef(
            "get_type_hierarchy",
            "Получить иерархию типов для типа по файлу и позиции: " +
            "базовые типы, интерфейсы, наследники. " +
            "Параметры: filePath, line, column, " +
            "optional offset (по умолч. 0), optional limit (по умолч. 100) — " +
            "пагинация списка наследников/реализаций.",
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
                    ["offset"] = new JsonObject
                    {
                        ["type"] = "integer",
                        ["description"] = "Смещение для пагинации наследников/реализаций (по умолч. 0)",
                        ["default"] = 0
                    },
                    ["limit"] = new JsonObject
                    {
                        ["type"] = "integer",
                        ["description"] = "Максимум наследников/реализаций (по умолч. 100)",
                        ["default"] = 100
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
                var offset = Math.Max(0, args["offset"]?.GetValue<int>() ?? 0);
                var limit = args["limit"]?.GetValue<int>() ?? 100;

                var sol = ws.RequireSolution();
                var doc = ws.FindDocument(filePath)
                    ?? throw new McpToolException($"Документ не найден: {filePath}");

                var (token, _) = await SymbolFormatter.GetTokenAtPositionAsync(doc, line, column);
                var model = await doc.GetSemanticModelAsync(ct)
                    ?? throw new McpToolException("Не удалось получить семантическую модель");

                var symbol = await SymbolFinder.FindSymbolAtPositionAsync(model, token.SpanStart, ws.Workspace!);
                if (symbol is not INamedTypeSymbol type)
                    return McpToolResult.Ok("Символ в данной позиции не является типом");

                var sb = new StringBuilder();
                sb.AppendLine($"Тип: {type.ToDisplayString()}");
                sb.AppendLine();

                // Цепочка базовых типов
                sb.AppendLine("Базовые типы:");
                var baseType = type.BaseType;
                var indent = "  ";
                while (baseType is not null)
                {
                    sb.AppendLine($"{indent}{baseType.ToDisplayString()}");
                    indent += "  ";
                    baseType = baseType.BaseType;
                }
                sb.AppendLine();

                // Интерфейсы
                if (type.AllInterfaces.Length > 0)
                {
                    sb.AppendLine("Интерфейсы:");
                    foreach (var iface in type.AllInterfaces)
                        sb.AppendLine($"  {iface.ToDisplayString()}");
                    sb.AppendLine();
                }

                // Наследники (для классов) / реализации (для интерфейсов) — с пагинацией
                if (type.TypeKind == TypeKind.Class)
                {
                    var derived = await SymbolFinder.FindDerivedClassesAsync(type, sol, cancellationToken: ct);
                    AppendPaged(sb, "Наследники", derived, offset, limit,
                        d => SymbolFormatter.FormatSymbolCompact(d));
                }
                else if (type.TypeKind == TypeKind.Interface)
                {
                    var impls = await SymbolFinder.FindImplementationsAsync(type, sol, cancellationToken: ct);
                    AppendPaged(sb, "Реализации", impls, offset, limit,
                        impl => SymbolFormatter.FormatSymbolCompact(impl));
                }

                return McpToolResult.Ok(sb.ToString());
            }
        ));
    }

    /// <summary>
    /// Выводит список элементов с пагинацией: заголовок с total/shown/nextOffset,
    /// затем элементы текущей страницы.
    /// </summary>
    private static void AppendPaged<T>(
        StringBuilder sb, string label, IEnumerable<T> items,
        int offset, int limit, Func<T, string> formatter)
    {
        var all = items.ToList();
        var total = all.Count;
        var page = all.Skip(offset).Take(limit).ToList();
        var shown = page.Count;
        var nextOffset = offset + shown;

        sb.AppendLine($"{label} ({total}):");
        if (total > shown)
            sb.AppendLine($"  Показано: {shown} (offset={offset}, limit={limit})" +
                          $"{(nextOffset < total ? $", следующая страница: offset={nextOffset}" : "")}");

        foreach (var item in page)
            sb.AppendLine($"  {formatter(item)}");
    }
}
