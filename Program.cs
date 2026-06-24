using RoslynMcp.Mcp;
using RoslynMcp.Services;
using RoslynMcp.Tools;
using System.Text.Json.Nodes;

// --- Парсинг аргументов командной строки ---
// --log-level <Trace|Debug|Info|Warn|Error>  (по умолч. Info)
// --log-file <путь>                          (по умолч. лог в %LOCALAPPDATA%\RoslynMcp\server.log)
var logLevel = "Info";
var logFile = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "RoslynMcp", "server.log");

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--log-level" when i + 1 < args.Length:
            logLevel = args[++i];
            break;
        case "--log-file" when i + 1 < args.Length:
            logFile = args[++i];
            break;
    }
}

// --- Инициализация логирования ---
var logDir = Path.GetDirectoryName(logFile);
if (!string.IsNullOrEmpty(logDir))
    Directory.CreateDirectory(logDir);

LoggingConfig.Setup(logFile, logLevel);

var logger = NLog.LogManager.GetCurrentClassLogger();
logger.Info($"RoslynMcp запускается. logLevel={logLevel}, logFile={logFile}");

// --- Регистрация MSBuild — до загрузки типов Roslyn ---
Microsoft.Build.Locator.MSBuildLocator.RegisterDefaults();
logger.Info("MSBuild зарегистрирован");

// --- Создание сервера ---
var server = new McpServer();
var workspace = new WorkspaceService();

// Регистрация инструментов
server.RegisterTool(new McpToolDef(
    "ping",
    "Проверка доступности сервера",
    new JsonObject
    {
        ["type"] = "object",
        ["properties"] = new JsonObject()
    },
    (_, _) => Task.FromResult(McpToolResult.Ok("pong"))));

WorkspaceTools.Register(server, workspace);
NavigationTools.Register(server, workspace);
StructureTools.Register(server, workspace);
AnalysisTools.Register(server, workspace);
AdditionalTools.Register(server, workspace);
ModificationTools.Register(server, workspace);

logger.Info("Все инструменты зарегистрированы. Ожидание запросов...");

// Запуск цикла обработки
await server.RunAsync(CancellationToken.None);

logger.Info("Сервер остановлен");
