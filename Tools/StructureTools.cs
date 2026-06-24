using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Mcp;
using RoslynMcp.Services;
using System.Text;
using System.Text.Json.Nodes;

namespace RoslynMcp.Tools;

/// <summary>
/// Инструменты структуры кода:
/// get_document_outline, get_type_members, get_method_body, get_source_text.
/// </summary>
public static class StructureTools
{
    public static void Register(McpServer server, WorkspaceService ws)
    {
        // --- get_document_outline ---
        server.RegisterTool(new McpToolDef(
            "get_document_outline",
            "Дерево типов/методов/полей документа с позициями — для быстрого обзора. " +
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

                var sb = new StringBuilder();
                var sourceText = await doc.GetTextAsync(ct);

                foreach (var member in root.ChildNodes().OfType<MemberDeclarationSyntax>())
                {
                    AppendOutline(sb, member, sourceText, indent: 0);
                }

                return McpToolResult.Ok(sb.ToString());
            }
        ));

        // --- get_type_members ---
        server.RegisterTool(new McpToolDef(
            "get_type_members",
            "Все члены типа (поля, свойства, методы, события) с сигнатурами. " +
            "Параметры: filePath, line, column — позиция типа.",
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
                if (symbol is not INamedTypeSymbol type)
                    return McpToolResult.Ok("Символ в данной позиции не является типом");

                var sb = new StringBuilder();
                sb.AppendLine($"Тип: {type.ToDisplayString()}");
                sb.AppendLine($"Kind: {type.TypeKind}");
                sb.AppendLine();

                // Статические члены сверху, потом экземплярные, группировка по виду
                var members = type.GetMembers()
                    .Where(m => m.Kind != SymbolKind.NamedType) // вложенные типы отдельно
                    .OrderBy(m => m.IsStatic ? 0 : 1)
                    .ThenBy(m => m.Kind.ToString())
                    .ToList();

                // Поля и константы
                var fields = members.Where(m => m.Kind == SymbolKind.Field).ToList();
                if (fields.Count > 0)
                {
                    sb.AppendLine("Поля:");
                    foreach (var f in fields.OfType<IFieldSymbol>())
                    {
                        var mods = FormatModifiers(f);
                        sb.AppendLine($"  {mods}{f.Type.ToDisplayString()} {f.Name}");
                    }
                    sb.AppendLine();
                }

                // Свойства
                var props = members.Where(m => m.Kind == SymbolKind.Property).ToList();
                if (props.Count > 0)
                {
                    sb.AppendLine("Свойства:");
                    foreach (var p in props.OfType<IPropertySymbol>())
                    {
                        var mods = FormatModifiers(p);
                        var accessors = p.IsReadOnly ? " { get; }"
                            : p.IsWriteOnly ? " { set; }"
                            : p.IsReadOnly ? " { get; }"
                            : " { get; set; }";
                        sb.AppendLine($"  {mods}{p.Type.ToDisplayString()} {p.Name}{accessors}");
                    }
                    sb.AppendLine();
                }

                // События
                var events = members.Where(m => m.Kind == SymbolKind.Event).ToList();
                if (events.Count > 0)
                {
                    sb.AppendLine("События:");
                    foreach (var e in events.OfType<IEventSymbol>())
                    {
                        var mods = FormatModifiers(e);
                        sb.AppendLine($"  {mods}event {e.Type.ToDisplayString()} {e.Name}");
                    }
                    sb.AppendLine();
                }

                // Методы
                var methods = members.Where(m => m.Kind == SymbolKind.Method).ToList();
                if (methods.Count > 0)
                {
                    sb.AppendLine("Методы:");
                    foreach (var m in methods.OfType<IMethodSymbol>())
                    {
                        var mods = FormatModifiers(m);
                        var returnType = m.ReturnType.ToDisplayString();
                        var parameters = string.Join(", ",
                            m.Parameters.Select(p => $"{p.Type.ToDisplayString()} {p.Name}"));
                        var kind = m.MethodKind == MethodKind.Constructor
                            ? type.Name
                            : m.MethodKind == MethodKind.Destructor
                                ? "~" + type.Name
                                : $"{returnType} {m.Name}";
                        sb.AppendLine($"  {mods}{kind}({parameters})");
                    }
                    sb.AppendLine();
                }

                // Вложенные типы
                var nested = type.GetTypeMembers();
                if (nested.Length > 0)
                {
                    sb.AppendLine("Вложенные типы:");
                    foreach (var n in nested)
                        sb.AppendLine($"  {n.TypeKind} {n.Name}");
                }

                return McpToolResult.Ok(sb.ToString());
            }
        ));

        // --- get_method_body ---
        server.RegisterTool(new McpToolDef(
            "get_method_body",
            "Извлечь тело метода как текст (для контекста LLM). " +
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

                var (token, root) = await SymbolFormatter.GetTokenAtPositionAsync(doc, line, column);

                // Поднимаемся к节点 Method/Constructor/Operator и т.п.
                var node = token.Parent;
                while (node is not null and not BaseMethodDeclarationSyntax)
                    node = node.Parent;

                if (node is not BaseMethodDeclarationSyntax methodDecl)
                    return McpToolResult.Ok("В данной позиции нет метода");

                var sb = new StringBuilder();

                // Сигнатура
                var sigStart = methodDecl.SpanStart;
                var sourceText = await doc.GetTextAsync(ct);

                // Полный текст метода, включая сигнатуру и тело
                var fullSpan = methodDecl.Span;
                var fullText = sourceText.ToString(TextSpan.FromBounds(
                    fullSpan.Start, fullSpan.End));

                sb.AppendLine(fullText);

                // Если тело отсутствует — помечаем
                if (methodDecl.Body is null && methodDecl.ExpressionBody is null)
                    sb.AppendLine("\n(метод без тела — abstract/interface/extern)");

                return McpToolResult.Ok(sb.ToString());
            }
        ));

        // --- get_source_text ---
        server.RegisterTool(new McpToolDef(
            "get_source_text",
            "Получить текст документа целиком или диапазон строк. " +
            "Параметры: filePath, optional startLine, optional endLine (1-based).",
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
                        ["description"] = "Начальная строка (1-based, по умолч. 1)"
                    },
                    ["endLine"] = new JsonObject
                    {
                        ["type"] = "integer",
                        ["description"] = "Конечная строка (1-based, по умолч. конец файла)"
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

                var sourceText = await doc.GetTextAsync(ct);
                var startLine = args["startLine"]?.GetValue<int>() ?? 1;
                var endLine = args["endLine"]?.GetValue<int>() ?? sourceText.Lines.Count;

                // Корректируем границы
                startLine = Math.Max(1, startLine);
                endLine = Math.Min(sourceText.Lines.Count, endLine);

                var sb = new StringBuilder();
                for (var i = startLine - 1; i < endLine; i++)
                {
                    sb.AppendLine(sourceText.Lines[i].ToString());
                }

                return McpToolResult.Ok(sb.ToString());
            }
        ));
    }

    /// <summary>
    /// Рекурсивно выводит структуру: типы, методы, свойства, поля.
    /// </summary>
    private static void AppendOutline(StringBuilder sb, MemberDeclarationSyntax member,
        SourceText sourceText, int indent)
    {
        var prefix = new string(' ', indent * 2);
        var lineSpan = member.GetLocation().GetLineSpan();

        switch (member)
        {
            case ClassDeclarationSyntax cls:
                sb.AppendLine($"{prefix}class {cls.Identifier.Text} " +
                              $"@ :{lineSpan.StartLinePosition.Line + 1}");
                foreach (var child in cls.Members)
                    AppendOutline(sb, child, sourceText, indent + 1);
                break;

            case RecordDeclarationSyntax rec:
                sb.AppendLine($"{prefix}record {rec.Identifier.Text} " +
                              $"@ :{lineSpan.StartLinePosition.Line + 1}");
                foreach (var child in rec.Members)
                    AppendOutline(sb, child, sourceText, indent + 1);
                break;

            case InterfaceDeclarationSyntax iface:
                sb.AppendLine($"{prefix}interface {iface.Identifier.Text} " +
                              $"@ :{lineSpan.StartLinePosition.Line + 1}");
                foreach (var child in iface.Members)
                    AppendOutline(sb, child, sourceText, indent + 1);
                break;

            case StructDeclarationSyntax str:
                sb.AppendLine($"{prefix}struct {str.Identifier.Text} " +
                              $"@ :{lineSpan.StartLinePosition.Line + 1}");
                foreach (var child in str.Members)
                    AppendOutline(sb, child, sourceText, indent + 1);
                break;

            case EnumDeclarationSyntax en:
                sb.AppendLine($"{prefix}enum {en.Identifier.Text} " +
                              $"@ :{lineSpan.StartLinePosition.Line + 1}");
                break;

            case MethodDeclarationSyntax method:
                sb.AppendLine($"{prefix}method {method.ReturnType} {method.Identifier.Text}" +
                              $"({string.Join(", ", method.ParameterList.Parameters.Select(p => p.Type?.ToString()))}) " +
                              $"@ :{lineSpan.StartLinePosition.Line + 1}");
                break;

            case PropertyDeclarationSyntax prop:
                sb.AppendLine($"{prefix}property {prop.Type} {prop.Identifier.Text} " +
                              $"@ :{lineSpan.StartLinePosition.Line + 1}");
                break;

            case FieldDeclarationSyntax field:
                var fieldType = field.Declaration.Type;
                var fieldNames = string.Join(", ", field.Declaration.Variables.Select(v => v.Identifier.Text));
                sb.AppendLine($"{prefix}field {fieldType} {fieldNames} " +
                              $"@ :{lineSpan.StartLinePosition.Line + 1}");
                break;

            case EventFieldDeclarationSyntax ev:
                var evType = ev.Declaration.Type;
                var evNames = string.Join(", ", ev.Declaration.Variables.Select(v => v.Identifier.Text));
                sb.AppendLine($"{prefix}event {evType} {evNames} " +
                              $"@ :{lineSpan.StartLinePosition.Line + 1}");
                break;

            case NamespaceDeclarationSyntax ns:
                sb.AppendLine($"{prefix}namespace {ns.Name} " +
                              $"@ :{lineSpan.StartLinePosition.Line + 1}");
                foreach (var child in ns.Members)
                    AppendOutline(sb, child, sourceText, indent + 1);
                break;

            case FileScopedNamespaceDeclarationSyntax fns:
                sb.AppendLine($"{prefix}namespace {fns.Name}; " +
                              $"@ :{lineSpan.StartLinePosition.Line + 1}");
                foreach (var child in fns.Members)
                    AppendOutline(sb, child, sourceText, indent + 1);
                break;

            default:
                sb.AppendLine($"{prefix}{member.Kind()} @ :{lineSpan.StartLinePosition.Line + 1}");
                break;
        }
    }

    /// <summary>
    /// Форматирует модификаторы символа в строку.
    /// </summary>
    private static string FormatModifiers(ISymbol symbol)
    {
        var parts = new List<string>();

        if (symbol.IsStatic)
            parts.Add("static");
        if (symbol.IsAbstract)
            parts.Add("abstract");
        if (symbol.IsVirtual)
            parts.Add("virtual");
        if (symbol.IsOverride)
            parts.Add("override");
        if (symbol.IsSealed)
            parts.Add("sealed");
        if (symbol is IMethodSymbol { IsAsync: true })
            parts.Add("async");

        if (symbol.DeclaredAccessibility != Accessibility.NotApplicable
            && symbol.DeclaredAccessibility != Accessibility.Public)
            parts.Add(symbol.DeclaredAccessibility.ToString().ToLowerInvariant());

        return parts.Count > 0 ? string.Join(" ", parts) + " " : "";
    }
}
