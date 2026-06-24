using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace RoslynMcp.Services;

/// <summary>
/// Вспомогательные методы для форматирования данных Roslyn в текст.
/// </summary>
public static class SymbolFormatter
{
    /// <summary>
    /// Форматирует символ: имя, вид, модификаторы, сигнатура, файл:строка:колонка.
    /// </summary>
    public static string FormatSymbol(ISymbol symbol, bool includeDoc = false)
    {
        var kind = symbol.Kind switch
        {
            SymbolKind.NamedType => symbol is ITypeSymbol type
                ? type.TypeKind.ToString().ToLowerInvariant()
                : "type",
            SymbolKind.Method => symbol is IMethodSymbol m
                ? m.MethodKind.ToString().ToLowerInvariant()
                : "method",
            SymbolKind.Property => "property",
            SymbolKind.Field => "field",
            SymbolKind.Event => "event",
            SymbolKind.Local => "local",
            SymbolKind.Parameter => "parameter",
            SymbolKind.Namespace => "namespace",
            _ => symbol.Kind.ToString().ToLowerInvariant()
        };

        var accessibility = symbol.DeclaredAccessibility != Accessibility.NotApplicable
            ? symbol.DeclaredAccessibility.ToString().ToLowerInvariant() + " "
            : "";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"{accessibility}{kind} {symbol.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat)}");

        var loc = GetPrimaryLocation(symbol);
        if (loc is not null)
        {
            var lineSpan = loc.GetLineSpan();
            sb.AppendLine($"  📍 {loc.SourceTree?.FilePath}:{lineSpan.StartLinePosition.Line + 1}:{lineSpan.StartLinePosition.Character + 1}");
        }

        if (includeDoc)
        {
            var doc = symbol.GetDocumentationCommentXml();
            if (!string.IsNullOrEmpty(doc))
                sb.AppendLine($"  📄 {doc.Trim()}");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Компактная сводка символа в одну строку.
    /// </summary>
    public static string FormatSymbolCompact(ISymbol symbol)
    {
        var loc = GetPrimaryLocation(symbol);
        var pos = loc is not null
            ? $"{loc.SourceTree?.FilePath}:{loc.GetLineSpan().StartLinePosition.Line + 1}"
            : "?";
        return $"{symbol.Name} ({symbol.Kind}) @ {pos}";
    }

    /// <summary>
    /// Возвращает основную локацию символа (первую, не в метаданных).
    /// </summary>
    public static Location? GetPrimaryLocation(ISymbol symbol)
    {
        return symbol.Locations.FirstOrDefault(l => l.IsInSource)
            ?? symbol.Locations.FirstOrDefault();
    }

    /// <summary>
    /// Форматирует ссылку на символ: файл, строка, колонка, TextSpan.
    /// </summary>
    public static string FormatReference(Location loc)
    {
        var lineSpan = loc.GetLineSpan();
        return $"{loc.SourceTree?.FilePath}:{lineSpan.StartLinePosition.Line + 1}:{lineSpan.StartLinePosition.Character + 1}";
    }

    /// <summary>
    /// Получить SyntaxToken в указанной позиции (строка/колонка, 1-based).
    /// </summary>
    public static async Task<(SyntaxToken token, SyntaxNode root)> GetTokenAtPositionAsync(
        Document doc, int line, int column)
    {
        var root = await doc.GetSyntaxRootAsync() ?? throw new InvalidOperationException("Нет синтаксического дерева");
        var sourceText = await doc.GetTextAsync();
        var linePosition = new LinePosition(line - 1, column - 1);
        var position = sourceText.Lines.GetPosition(linePosition);
        var token = root.FindToken(position);
        return (token, root);
    }

    /// <summary>
    /// Разрешает символ метода по позиции с fallback:
    /// если FindSymbolAtPosition вернул не метод (параметр, тип и т.д.),
    /// поднимается по синтаксическому дереву к объявлению метода.
    /// </summary>
    public static async Task<IMethodSymbol?> ResolveMethodAtPositionAsync(
        Document doc, int line, int column, Microsoft.CodeAnalysis.Workspace workspace)
    {
        var (token, root) = await GetTokenAtPositionAsync(doc, line, column);
        var model = await doc.GetSemanticModelAsync();
        if (model is null) return null;

        // Сначала пробуем прямой поиск
        var symbol = await Microsoft.CodeAnalysis.FindSymbols.SymbolFinder
            .FindSymbolAtPositionAsync(model, token.SpanStart, workspace);

        if (symbol is IMethodSymbol method)
            return method;

        // Fallback: поднимаемся к объявлению метода
        var node = token.Parent;
        while (node is not null and not BaseMethodDeclarationSyntax)
            node = node.Parent;

        if (node is BaseMethodDeclarationSyntax methodDecl)
            return model.GetDeclaredSymbol(methodDecl) as IMethodSymbol;

        return null;
    }
}
