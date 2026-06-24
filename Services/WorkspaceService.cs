using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.MSBuild;
using NLog;
using RoslynMcp.Mcp;

namespace RoslynMcp.Services;

/// <summary>
/// Управление жизненным циклом MSBuildWorkspace.
/// Хранит текущее открытое решение/проект и предоставляет доступ к документам.
/// </summary>
public sealed class WorkspaceService
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private MSBuildWorkspace? _workspace;
    private Solution? _solution;

    /// <summary>
    /// Текущее открытое решение (null если не загружено).
    /// </summary>
    public Solution? CurrentSolution => _solution;

    /// <summary>
    /// Текущий workspace.
    /// </summary>
    public MSBuildWorkspace? Workspace => _workspace;

    /// <summary>
    /// Создаёт новый workspace с зарегистрированным обработчиком ошибок.
    /// </summary>
    private MSBuildWorkspace CreateWorkspace()
    {
        var ws = MSBuildWorkspace.Create();
        ws.RegisterWorkspaceFailedHandler(
            e => Console.Error.WriteLine(
                $"[Workspace] {e.Diagnostic.Kind}: {e.Diagnostic.Message}"));
        return ws;
    }

    /// <summary>
    /// Открывает .sln файл и загружает все проекты.
    /// </summary>
    public async Task<Solution> OpenSolutionAsync(string path, CancellationToken ct)
    {
        _workspace = CreateWorkspace();
        _solution = await _workspace.OpenSolutionAsync(path, cancellationToken: ct);
        return _solution;
    }

    /// <summary>
    /// Открывает отдельный .csproj без решения.
    /// </summary>
    public async Task<Solution> OpenProjectAsync(string path, CancellationToken ct)
    {
        _workspace = CreateWorkspace();
        var project = await _workspace.OpenProjectAsync(path, cancellationToken: ct);
        _solution = _workspace.CurrentSolution;
        return _solution;
    }

    /// <summary>
    /// Перезагружает решение/проект с диска.
    /// Полностью пересоздаёт workspace.
    /// </summary>
    public async Task<Solution> ReloadAsync(CancellationToken ct)
    {
        if (_solution is null)
            throw new InvalidOperationException("Нет открытого решения. Сначала вызовите open_solution или open_project.");

        // Запоминаем пути перед пересозданием
        var slnPath = _solution.FilePath;
        var projectPaths = _solution.Projects
            .Where(p => p.FilePath is not null)
            .Select(p => p.FilePath!)
            .ToList();

        _workspace?.Dispose();
        _workspace = CreateWorkspace();

        if (!string.IsNullOrEmpty(slnPath))
        {
            _solution = await _workspace.OpenSolutionAsync(slnPath, cancellationToken: ct);
        }
        else if (projectPaths.Count > 0)
        {
            foreach (var p in projectPaths)
                await _workspace.OpenProjectAsync(p, cancellationToken: ct);
            _solution = _workspace.CurrentSolution;
        }

        return _solution;
    }

    /// <summary>
    /// Требует открытое решение — выбрасывает исключение если не загружено.
    /// </summary>
    public Solution RequireSolution()
    {
        if (_solution is null)
            throw new McpToolException(
                "Решение не загружено. Сначала вызовите open_solution или open_project.");
        return _solution;
    }

    /// <summary>
    /// Ищет документ по пути файла. Регистронезависимо (Windows).
    /// </summary>
    public Document? FindDocument(string filePath)
    {
        var sol = RequireSolution();
        return sol.Projects
            .SelectMany(p => p.Documents)
            .FirstOrDefault(d =>
                d.FilePath is not null &&
                string.Equals(d.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Загружает все проекты в решении и возвращает информацию о них.
    /// </summary>
    public WorkspaceInfo GetWorkspaceInfo()
    {
        var sol = RequireSolution();

        var projects = sol.Projects.Select(p => new ProjectInfo(
            Name: p.Name,
            FilePath: p.FilePath ?? "",
            TargetFramework: p.ParseOptions is CSharpParseOptions cs
                ? cs.LanguageVersion.ToString()
                : "unknown",
            Documents: p.Documents
                .Where(d => d.FilePath is not null)
                .Select(d => d.FilePath!)
                .ToList(),
            ProjectReferences: p.ProjectReferences
                .Select(r => sol.GetProject(r.ProjectId)?.Name ?? r.ProjectId.ToString())
                .ToList(),
            PackageReferences: p.MetadataReferences
                .Select(r => r.Display ?? r.GetType().Name)
                .ToList()
        )).ToList();

        return new WorkspaceInfo(
            SolutionPath: sol.FilePath ?? "",
            Projects: projects
        );
    }

    // ===== Методы модификации =====

    /// <summary>
    /// Применяет text change к документу в workspace.
    /// Изменения остаются в памяти (не на диске) до вызова SaveDocumentAsync.
    /// </summary>
    public async Task<Document> ApplyTextChangeAsync(
        string filePath, int startLine, int startColumn, int endLine, int endColumn,
        string newText, CancellationToken ct)
    {
        var sol = RequireSolution();
        var doc = FindDocument(filePath)
            ?? throw new McpToolException($"Документ не найден: {filePath}");

        var sourceText = await doc.GetTextAsync(ct);
        var start = sourceText.Lines[startLine - 1].Start + startColumn - 1;
        var end = sourceText.Lines[endLine - 1].Start + endColumn - 1;

        if (end < start)
            throw new McpToolException("Конечная позиция раньше начальной");

        var textSpan = new Microsoft.CodeAnalysis.Text.TextSpan(start, end - start);
        var change = new Microsoft.CodeAnalysis.Text.TextChange(textSpan, newText);

        // Применяем к документу в workspace
        var newDoc = doc.WithText(sourceText.WithChanges(change));
        _solution = newDoc.Project.Solution;

        Logger.Info($"ApplyTextChange: {filePath} [{startLine}:{startColumn}-{endLine}:{endColumn}] → {newText.Length} символов");
        return newDoc;
    }

    /// <summary>
    /// Переименовывает символ во всём solution (Roslyn Renamer).
    /// Изменения остаются в памяти до вызова SaveAllChangesAsync.
    /// </summary>
    public async Task<(Solution NewSolution, int ChangedDocuments)> RenameSymbolAsync(
        Document doc, int position, string newName, CancellationToken ct)
    {
        var model = await doc.GetSemanticModelAsync(ct)
            ?? throw new McpToolException("Не удалось получить семантическую модель");

        var symbol = await Microsoft.CodeAnalysis.FindSymbols.SymbolFinder
            .FindSymbolAtPositionAsync(model, position, _workspace!)
            ?? throw new McpToolException("Символ не найден в данной позиции");

        var newSolution = await Microsoft.CodeAnalysis.Rename.Renamer.RenameSymbolAsync(
            _solution!, symbol, default(Microsoft.CodeAnalysis.Rename.SymbolRenameOptions), newName, ct);

        // Подсчёт изменённых документов
        var changes = _solution!.GetChanges(newSolution);
        var changedDocs = changes.GetProjectChanges()
            .SelectMany(pc => pc.GetChangedDocuments())
            .Count();

        _solution = newSolution;
        Logger.Info($"RenameSymbol: {symbol.Name} → {newName}, изменено документов: {changedDocs}");
        return (newSolution, changedDocs);
    }

    /// <summary>
    /// Записывает изменения одного документа на диск.
    /// </summary>
    public async Task SaveDocumentAsync(string filePath, CancellationToken ct)
    {
        var sol = RequireSolution();
        var doc = FindDocument(filePath)
            ?? throw new McpToolException($"Документ не найден: {filePath}");

        var sourceText = await doc.GetTextAsync(ct);
        await File.WriteAllTextAsync(doc.FilePath!, sourceText.ToString(), ct);
        Logger.Info($"SaveDocument: {filePath}");
    }

    /// <summary>
    /// Записывает изменения всех документов solution на диск.
    /// Возвращает количество сохранённых файлов.
    /// </summary>
    public async Task<int> SaveAllChangesAsync(CancellationToken ct)
    {
        var sol = RequireSolution();
        var saved = 0;
        var changedDocs = sol.Projects
            .SelectMany(p => p.Documents)
            .Where(d => d.FilePath is not null)
            .ToList();

        foreach (var doc in changedDocs)
        {
            var originalText = "";
            if (File.Exists(doc.FilePath))
                originalText = await File.ReadAllTextAsync(doc.FilePath!, ct);

            var newText = (await doc.GetTextAsync(ct)).ToString();
            if (originalText != newText)
            {
                await File.WriteAllTextAsync(doc.FilePath!, newText, ct);
                saved++;
                Logger.Info($"SaveAll: {doc.FilePath}");
            }
        }

        Logger.Info($"SaveAllChanges: сохранено {saved} файлов из {changedDocs.Count}");
        return saved;
    }

    /// <summary>
    /// Возвращает список документов с несохранёнными изменениями.
    /// </summary>
    public async Task<List<string>> GetModifiedDocumentsAsync(CancellationToken ct)
    {
        var sol = RequireSolution();
        var result = new List<string>();

        foreach (var doc in sol.Projects.SelectMany(p => p.Documents))
        {
            if (doc.FilePath is null || !File.Exists(doc.FilePath))
                continue;

            var originalText = await File.ReadAllTextAsync(doc.FilePath!, ct);
            var currentText = (await doc.GetTextAsync(ct)).ToString();

            if (originalText != currentText)
                result.Add(doc.FilePath);
        }

        return result;
    }
}

/// <summary>
/// Информация о Workspace для возврата клиенту.
/// </summary>
public sealed record WorkspaceInfo(
    string SolutionPath,
    List<ProjectInfo> Projects);

/// <summary>
/// Информация об отдельном проекте.
/// </summary>
public sealed record ProjectInfo(
    string Name,
    string FilePath,
    string TargetFramework,
    List<string> Documents,
    List<string> ProjectReferences,
    List<string> PackageReferences);
