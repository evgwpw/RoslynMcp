using NLog;
using NLog.Config;
using NLog.Targets;
using NLog.Targets.Wrappers;

namespace RoslynMcp.Services;

/// <summary>
/// Конфигурация NLog: файл + stderr, с уровнем из аргументов командной строки.
/// </summary>
public static class LoggingConfig
{
    /// <summary>
    /// Настраивает NLog программируемо.
    /// logFilePath — путь к файлу логов (null = только stderr).
    /// level — минимальный уровень (Trace/Debug/Info/Warn/Error).
    /// </summary>
    public static void Setup(string? logFilePath, string level = "Info")
    {
        var config = new LoggingConfiguration();

        var logLevel = LogLevel.FromString(level);

        // Файл — ротация по размеру, 5 файлов по 10 МБ
        if (!string.IsNullOrEmpty(logFilePath))
        {
            var fileTarget = new FileTarget("file")
            {
                FileName = logFilePath,
                Layout = "${longdate} | ${level:uppercase=true:padding=-5} | ${logger} | ${message} ${exception:format=ToString}",
                ArchiveFileName = logFilePath + ".{#}",
                ArchiveAboveSize = 10_000_000,
                MaxArchiveFiles = 5,
                KeepFileOpen = true,
                AutoFlush = true,
                Encoding = System.Text.Encoding.UTF8
            };

            // Async wrapper — не блокирует обработку MCP-запросов
            var asyncFile = new AsyncTargetWrapper(fileTarget)
            {
                OverflowAction = AsyncTargetWrapperOverflowAction.Discard,
                QueueLimit = 1000
            };

            config.AddRule(logLevel, LogLevel.Fatal, asyncFile);
        }

        // stderr — всегда, для отладки
        var stderrTarget = new ConsoleTarget("stderr")
        {
            StdErr = true,
            Layout = "${level:uppercase=true:padding=-5} | ${logger:shortName=true} | ${message} ${exception:format=ToString}"
        };
        config.AddRule(LogLevel.Warn, LogLevel.Fatal, stderrTarget);

        LogManager.Configuration = config;
    }
}
