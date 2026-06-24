using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using RoslynMcp.Mcp;
using RoslynMcp.Services;
using System.Text;
using System.Text.Json.Nodes;

namespace RoslynMcp.Tools;

/// <summary>
/// Инструменты семантического анализа:
/// get_symbol_info, get_diagnostics, get_callers, get_callees.
/// </summary>
public static class AnalysisTools
{
    public static void Register(McpServer server, WorkspaceService ws)
    {
        // --- get_symbol_info ---
        server.RegisterTool(new McpToolDef(
            "get_symbol_info",
            "Полная информация о символе по позиции: вид, тип, модификаторы, " +
            "объявление, XML-документация, содержащий тип. " +
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
                    ?? throw new McpToolException($"Документ не найден: {filePath}");

                var (token, root) = await SymbolFormatter.GetTokenAtPositionAsync(doc, line, column);
                var model = await doc.GetSemanticModelAsync(ct)
                    ?? throw new McpToolException("Не удалось получить семантическую модель");

                var symbol = await SymbolFinder.FindSymbolAtPositionAsync(model, token.SpanStart, ws.Workspace!);
                if (symbol is null)
                    return McpToolResult.Ok("Символ не найден в данной позиции");

                var sb = new StringBuilder();
                sb.AppendLine($"Имя: {symbol.Name}");
                sb.AppendLine($"Полное имя: {symbol.ToDisplayString()}");
                sb.AppendLine($"Вид: {symbol.Kind}");
                sb.AppendLine($"Доступность: {symbol.DeclaredAccessibility}");
                sb.AppendLine($"Статический: {symbol.IsStatic}");
                sb.AppendLine($"Абстрактный: {symbol.IsAbstract}");
                sb.AppendLine($"Виртуальный: {symbol.IsVirtual}");
                sb.AppendLine($"Override: {symbol.IsOverride}");
                sb.AppendLine($"Sealed: {symbol.IsSealed}");

                // Содержащий тип
                if (symbol.ContainingType is not null)
                    sb.AppendLine($"Содержащий тип: {symbol.ContainingType.ToDisplayString()}");

                // Содержащий namespace
                if (symbol.ContainingNamespace is not null)
                    sb.AppendLine($"Namespace: {symbol.ContainingNamespace.ToDisplayString()}");

                // Дополнительно для методов
                if (symbol is IMethodSymbol method)
                {
                    sb.AppendLine($"Возвращаемый тип: {(method.ReturnsVoid ? "void" : method.ReturnType.ToDisplayString())}");
                    sb.AppendLine($"IsAsync: {method.IsAsync}");
                    sb.AppendLine($"MethodKind: {method.MethodKind}");
                    if (method.Parameters.Length > 0)
                    {
                        sb.AppendLine("Параметры:");
                        foreach (var p in method.Parameters)
                            sb.AppendLine($"  {p.Type.ToDisplayString()} {p.Name}" +
                                          (p.HasExplicitDefaultValue ? $" = {p.ExplicitDefaultValue}" : ""));
                    }
                }

                // Дополнительно для полей/свойств
                if (symbol is IFieldSymbol field)
                {
                    sb.AppendLine($"Тип: {field.Type.ToDisplayString()}");
                    sb.AppendLine($"Const: {field.IsConst}");
                    sb.AppendLine($"Readonly: {field.IsReadOnly}");
                    if (field.HasConstantValue)
                        sb.AppendLine($"Значение: {field.ConstantValue}");
                }

                if (symbol is IPropertySymbol prop)
                {
                    sb.AppendLine($"Тип: {prop.Type.ToDisplayString()}");
                    sb.AppendLine($"Get: {prop.GetMethod is not null}");
                    sb.AppendLine($"Set: {prop.SetMethod is not null}");
                }

                // Дополнительно для типов
                if (symbol is INamedTypeSymbol type)
                {
                    sb.AppendLine($"TypeKind: {type.TypeKind}");
                    if (type.BaseType is not null)
                        sb.AppendLine($"BaseType: {type.BaseType.ToDisplayString()}");
                    if (type.AllInterfaces.Length > 0)
                        sb.AppendLine($"Интерфейсы: {string.Join(", ", type.AllInterfaces.Select(i => i.ToDisplayString()))}");
                    if (type.IsGenericType)
                        sb.AppendLine($"Generic: {string.Join(", ", type.TypeParameters.Select(t => t.Name))}");
                }

                // Локация
                var loc = SymbolFormatter.GetPrimaryLocation(symbol);
                if (loc is not null)
                {
                    var lineSpan = loc.GetLineSpan();
                    sb.AppendLine($"Объявление: {loc.SourceTree?.FilePath}:{lineSpan.StartLinePosition.Line + 1}:{lineSpan.StartLinePosition.Character + 1}");
                }

                // XML-документация
                var xml = symbol.GetDocumentationCommentXml();
                if (!string.IsNullOrEmpty(xml))
                    sb.AppendLine($"Документация:\n{xml.Trim()}");

                return McpToolResult.Ok(sb.ToString());
            }
        ));

        // --- get_diagnostics ---
        server.RegisterTool(new McpToolDef(
            "get_diagnostics",
            "Ошибки и предупреждения Roslyn по документу. " +
            "Параметры: filePath — путь к .cs файлу.",
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

                // Семантическая модель — даёт синтаксические + семантические диагностики
                var model = await doc.GetSemanticModelAsync(ct)
                    ?? throw new McpToolException("Не удалось получить семантическую модель");

                var diagnostics = model.GetDiagnostics()
                    .Where(d => d.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning)
                    .ToList();

                if (diagnostics.Count == 0)
                    return McpToolResult.Ok("Ошибок и предупреждений нет.");

                var sb = new StringBuilder();
                sb.AppendLine($"Диагностик: {diagnostics.Count}");
                sb.AppendLine();

                var sourceText = await doc.GetTextAsync(ct);

                foreach (var d in diagnostics)
                {
                    var lineSpan = d.Location.GetLineSpan();
                    var lineNum = lineSpan.StartLinePosition.Line + 1;
                    var colNum = lineSpan.StartLinePosition.Character + 1;
                    var codeLine = "";
                    if (lineSpan.StartLinePosition.Line < sourceText.Lines.Count)
                        codeLine = sourceText.Lines[lineSpan.StartLinePosition.Line].ToString().Trim();

                    var severity = d.Severity == DiagnosticSeverity.Error ? "ERROR" : "WARN";
                    sb.AppendLine($"[{severity}] {lineNum}:{colNum} {d.Id}: {d.GetMessage()}");
                    if (!string.IsNullOrEmpty(codeLine))
                        sb.AppendLine($"  {codeLine}");
                    sb.AppendLine();
                }

                return McpToolResult.Ok(sb.ToString());
            }
        ));

        // --- get_callers ---
        server.RegisterTool(new McpToolDef(
            "get_callers",
            "Найти все вызовы данного метода по всему solution. " +
            "Параметры: filePath, line, column — позиция метода.",
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

                var sol = ws.RequireSolution();
                var doc = ws.FindDocument(filePath)
                    ?? throw new McpToolException($"Документ не найден: {filePath}");

                var methodSymbol = await SymbolFormatter.ResolveMethodAtPositionAsync(doc, line, column, ws.Workspace!);
                if (methodSymbol is null)
                    return McpToolResult.Ok("Метод не найден в данной позиции");

                var refs = await SymbolFinder.FindReferencesAsync(methodSymbol, sol, ct);

                var sb = new StringBuilder();
                sb.AppendLine($"Метод: {methodSymbol.ToDisplayString()}");
                sb.AppendLine();

                var callerSet = new HashSet<string>();
                var totalCalls = 0;

                foreach (var refGroup in refs)
                {
                    foreach (var loc in refGroup.Locations)
                    {
                        totalCalls++;

                        // Находим метод, содержащий этот вызов
                        var locSpan = loc.Location.SourceSpan;
                        var locDoc = sol.Projects
                            .SelectMany(p => p.Documents)
                            .FirstOrDefault(d => d.FilePath is not null
                                && string.Equals(d.FilePath, loc.Location.SourceTree?.FilePath, StringComparison.OrdinalIgnoreCase))
                            ?? sol.Projects
                                .SelectMany(p => p.AdditionalDocuments)
                                .FirstOrDefault(d => d.FilePath is not null
                                    && string.Equals(d.FilePath, loc.Location.SourceTree?.FilePath, StringComparison.OrdinalIgnoreCase))
                                as Document;
                        if (locDoc is null) continue;

                        var locRoot = await locDoc.GetSyntaxRootAsync(ct);
                        if (locRoot is null) continue;

                        var node = locRoot.FindToken(loc.Location.SourceSpan.Start).Parent;
                        while (node is not null and not BaseMethodDeclarationSyntax)
                            node = node.Parent;

                        if (node is BaseMethodDeclarationSyntax callerMethod)
                        {
                            var callerModel = await locDoc.GetSemanticModelAsync(ct);
                            if (callerModel is null) continue;

                            var callerSymbol = callerModel.GetDeclaredSymbol(callerMethod);
                            if (callerSymbol is null) continue;

                            var callerStr = callerSymbol.ToDisplayString();
                            var lineSpan = loc.Location.GetLineSpan();
                            var key = $"{callerStr}@{locDoc.FilePath}:{lineSpan.StartLinePosition.Line + 1}";

                            if (callerSet.Add(key))
                            {
                                sb.AppendLine($"  {callerStr}");
                                sb.AppendLine($"    📍 {locDoc.FilePath}:{lineSpan.StartLinePosition.Line + 1}");
                            }
                        }
                        else
                        {
                            // Вызов вне метода (например, в поле или top-level)
                            var lineSpan = loc.Location.GetLineSpan();
                            sb.AppendLine($"  (вне метода)");
                            sb.AppendLine($"    📍 {locDoc.FilePath}:{lineSpan.StartLinePosition.Line + 1}");
                        }
                    }
                }

                sb.Insert(0, $"Вызовов: {totalCalls}\nУникальных вызывающих методов: {callerSet.Count}\n\n");

                return McpToolResult.Ok(sb.ToString());
            }
        ));

        // --- get_callees ---
        server.RegisterTool(new McpToolDef(
            "get_callees",
            "Найти все методы, вызываемые из данного метода. " +
            "Параметры: filePath, line, column — позиция метода.",
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

                var methodSymbol = await SymbolFormatter.ResolveMethodAtPositionAsync(doc, line, column, ws.Workspace!);
                if (methodSymbol is null)
                    return McpToolResult.Ok("Метод не найден в данной позиции");

                var model = await doc.GetSemanticModelAsync(ct)
                    ?? throw new McpToolException("Не удалось получить семантическую модель");

                // Находим объявление метода для поиска вызовов в теле
                var (token, _) = await SymbolFormatter.GetTokenAtPositionAsync(doc, line, column);
                var node = token.Parent;
                while (node is not null and not BaseMethodDeclarationSyntax)
                    node = node.Parent;

                if (node is not BaseMethodDeclarationSyntax methodDecl)
                    return McpToolResult.Ok("Не удалось найти тело метода");

                // Ищем все вызовы (InvocationExpression) и создания объектов (ObjectCreation)
                var invocations = methodDecl.DescendantNodes()
                    .OfType<InvocationExpressionSyntax>()
                    .ToList();

                var objectCreations = methodDecl.DescendantNodes()
                    .OfType<ObjectCreationExpressionSyntax>()
                    .ToList();

                var sb = new StringBuilder();
                sb.AppendLine($"Метод: {methodSymbol.ToDisplayString()}");
                sb.AppendLine();

                var callees = new Dictionary<string, List<string>>(StringComparer.Ordinal);

                // Обработка вызовов
                foreach (var inv in invocations)
                {
                    var info = model.GetSymbolInfo(inv);
                    var sym = info.Symbol ?? info.CandidateSymbols.FirstOrDefault();
                    if (sym is null) continue;

                    var display = sym.ToDisplayString();
                    var lineSpan = inv.GetLocation().GetLineSpan();
                    var loc = $"{inv.GetLocation().SourceTree?.FilePath}:{lineSpan.StartLinePosition.Line + 1}";

                    if (!callees.TryGetValue(display, out var locations))
                        callees[display] = locations = new List<string>();
                    locations.Add(loc);
                }

                // Обработка созданий объектов (конструкторы)
                foreach (var creation in objectCreations)
                {
                    var info = model.GetSymbolInfo(creation);
                    var sym = info.Symbol ?? info.CandidateSymbols.FirstOrDefault();
                    if (sym is null) continue;

                    var display = sym.ToDisplayString();
                    var lineSpan = creation.GetLocation().GetLineSpan();
                    var loc = $"{creation.GetLocation().SourceTree?.FilePath}:{lineSpan.StartLinePosition.Line + 1}";

                    if (!callees.TryGetValue(display, out var locations))
                        callees[display] = locations = new List<string>();
                    locations.Add(loc);
                }

                sb.AppendLine($"Вызываемых методов: {callees.Count}");
                sb.AppendLine();

                foreach (var (callee, locations) in callees.OrderBy(kv => kv.Key))
                {
                    sb.AppendLine($"  {callee}");
                    foreach (var loc in locations)
                        sb.AppendLine($"    📍 {loc}");
                }

                return McpToolResult.Ok(sb.ToString());
            }
        ));
    }
}
