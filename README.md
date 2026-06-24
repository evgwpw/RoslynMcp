# RoslynMcp

MCP-сервер (Model Context Protocol) для навигации и модификации C#-кода через Roslyn. Работает поверх stdio (JSON-RPC 2.0), предоставляет AI-ассистентам семантический доступ к C#-проектам: поиск символов, иерархия типов, call graph, диагностика, рефакторинг.

## Возможности

- **22 инструмента** в 6 категориях
- **Пагинация** (offset/limit) для поиска символов, ссылок, реализаций и иерархии
- **Отложенная запись** — изменения в памяти до явного save
- **Single-file publish** — автономный exe, self-contained
- **NLog** логирование с ротацией

## Инструменты

### Workspace
| Инструмент | Описание |
|---|---|
| `open_solution` | Открыть .sln |
| `open_project` | Открыть .csproj |
| `get_workspace_info` | Информация о проектах, документах, ссылках |
| `reload_workspace` | Перезагрузить с диска |

### Навигация
| Инструмент | Параметры пагинации | Описание |
|---|---|---|
| `find_symbol` | `offset`, `maxResults` | Поиск символов по regex |
| `go_to_definition` | — | Объявление символа по позиции |
| `find_references` | `offset`, `maxResults` | Все обращения к символу |
| `find_implementations` | `offset`, `limit` | Реализации интерфейса / наследники / override-ы |
| `get_type_hierarchy` | `offset`, `limit` | Базовые типы, интерфейсы, наследники |

### Структура кода
| Инструмент | Описание |
|---|---|
| `get_document_outline` | Дерево типов/методов/полей |
| `get_type_members` | Все члены типа с сигнатурами |
| `get_method_body` | Текст метода |
| `get_source_text` | Текст файла или диапазон строк |

### Анализ
| Инструмент | Описание |
|---|---|
| `get_symbol_info` | Полная информация о символе |
| `get_diagnostics` | Ошибки и предупреждения Roslyn |
| `get_callers` | Кто вызывает метод |
| `get_callees` | Кого вызывает метод |
| `get_signature_help` | Перегрузки метода в точке вызова |

### Модификация
| Инструмент | Описание |
|---|---|
| `apply_text_change` | Замена диапазона текста (в памяти) |
| `rename_symbol` | Переименование во всём solution |
| `get_modified_documents` | Список несохранённых изменений |
| `save_document` | Запись одного файла |
| `save_all_changes` | Запись всех изменений |

### Дополнительно
| Инструмент | Описание |
|---|---|
| `format_document` | Форматирование через Roslyn Formatter |
| `get_project_references` | Project-to-project и package references |
| `ping` | Проверка доступности |

## Сборка

```bash
dotnet publish RoslynMcp.csproj -c Release -r win-x64
```

Результат — single-file exe в `bin/Release/net10.0/win-x64/publish/` + директории `BuildHost-netcore/` и `BuildHost-net472/` рядом.

## Конфигурация MCP

Добавить в настройки MCP-клиента:

```json
{
  "mcpServers": {
    "roslyn": {
      "command": "E:\\path\\to\\RoslynMcp.exe",
      "args": ["--log-level", "Info"]
    }
  }
}
```

Параметры командной строки:
- `--log-level <Trace|Debug|Info|Warn|Error>` — уровень логирования (по умолч. `Info`)
- `--log-file <путь>` — файл логов (по умолч. `%LOCALAPPDATA%\RoslynMcp\server.log`)

## Технологии

- .NET 10, C# 14
- Microsoft.CodeAnalysis.CSharp.Workspaces 5.3.0 (Roslyn)
- MSBuild Locator 1.11.2
- NLog 6.1.3
- MCP protocol `2024-11-05`

## Архитектура

```
Program.cs            — точка входа, регистрация MSBuild и инструментов
├── Mcp/
│   ├── McpProtocol.cs — типы протокола (McpToolDef, McpToolResult)
│   └── McpServer.cs   — JSON-RPC 2.0 поверх stdio
├── Services/
│   ├── WorkspaceService.cs  — жизненный цикл MSBuildWorkspace
│   ├── SymbolFormatter.cs   — форматирование символов Roslyn
│   └── LoggingConfig.cs     — NLog конфигурация
└── Tools/
    ├── WorkspaceTools.cs       — open/reload/info
    ├── NavigationTools.cs      — find/go-to/references/hierarchy
    ├── StructureTools.cs       — outline/members/body/source
    ├── AnalysisTools.cs        — symbol-info/diagnostics/callers/callees
    ├── AdditionalTools.cs      — format/references/signature-help
    └── ModificationTools.cs    — apply/rename/save
```
