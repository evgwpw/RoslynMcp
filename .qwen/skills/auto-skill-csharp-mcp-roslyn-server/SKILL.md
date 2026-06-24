---
name: csharp-mcp-roslyn-server
description: How to build an MCP stdio server in C# .NET 10 using Roslyn for code navigation and modification, packaged as a single-file executable with NLog logging — includes non-obvious gotchas (MSBuild Locator package conflict, non-ASCII JSON escaping, Roslyn 5.3 API changes, symbol resolution fallback, BuildHost single-file limitation, Rename API changes).
source: auto-skill
extracted_at: '2026-06-24T20:54:06.564Z'
---

# Building an MCP stdio Server in C# .NET 10 with Roslyn

Reusable patterns and gotchas discovered while building a Roslyn-based MCP server.

## Project setup (.csproj)

```xml
<PropertyGroup>
  <OutputType>Exe</OutputType>
  <TargetFramework>net10.0</TargetFramework>
  <ImplicitUsings>enable</ImplicitUsings>
  <Nullable>enable</Nullable>
  <!-- Single-file autonomous publish -->
  <PublishSingleFile>true</PublishSingleFile>
  <SelfContained>true</SelfContained>
  <IncludeNativeLibrariesForSelfExtract>true</IncludeNativeLibrariesForSelfExtract>
  <EnableCompressionInSingleFile>true</EnableCompressionInSingleFile>
  <!-- Deterministic culture — consistent JSON output -->
  <InvariantGlobalization>true</InvariantGlobalization>
</PropertyGroup>

<ItemGroup>
  <!-- CRITICAL: Microsoft.Build.Framework must have ExcludeAssets+PrivateAssets or build fails (see gotcha below) -->
  <PackageReference Include="Microsoft.Build.Framework" Version="17.11.48" ExcludeAssets="runtime" PrivateAssets="all" />
  <PackageReference Include="Microsoft.Build.Locator" Version="1.11.2" />
  <PackageReference Include="Microsoft.CodeAnalysis.CSharp.Workspaces" Version="5.3.0" />
  <PackageReference Include="Microsoft.CodeAnalysis.Workspaces.MSBuild" Version="5.3.0" />
</ItemGroup>
```

## Gotcha 1: MSBuild Locator build error MSBL001

**Symptom:** `dotnet build` fails with:
```
error MSBL001: A PackageReference to the package 'Microsoft.Build.Framework' at version '17.11.48' is present in this project without ExcludeAssets="runtime" and PrivateAssets="all" set.
```

**Cause:** `Microsoft.Build.Locator` requires `Microsoft.Build.Framework` to NOT be copied to the output directory, because MSBuild assemblies are loaded from the registered SDK at runtime. If the NuGet copy is present, it causes assembly-loading conflicts.

**Fix:** Add an explicit `<PackageReference>` for `Microsoft.Build.Framework` with `ExcludeAssets="runtime"` and `PrivateAssets="all"`. This must be done manually — adding `Microsoft.Build.Locator` alone does not configure it.

**Also:** Call `MSBuildLocator.RegisterDefaults()` at the very start of `Program.cs`, before any Roslyn types are loaded.

## Gotcha 2: Non-ASCII JSON escaping (Cyrillic → \uXXXX)

**Symptom:** MCP tool descriptions and results containing non-ASCII characters (Cyrillic, etc.) are output as `\u041F\u0440\u043E...` instead of readable text.

**Cause:** `System.Text.Json`'s default `JavaScriptEncoder` escapes all non-ASCII characters. `JsonNode.ToJsonString()` uses these defaults.

**Fix:** Use `JsonSerializer.Serialize(node, opts)` with custom options instead of `node.ToJsonString()`:

```csharp
private static readonly JsonSerializerOptions JsonOpts = new()
{
    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    WriteIndented = false
};

// Use this everywhere instead of response.ToJsonString():
await writer.WriteLineAsync(JsonSerializer.Serialize(response, JsonOpts));
```

## MCP stdio protocol implementation pattern

MCP runs JSON-RPC 2.0 over stdin/stdout, one message per line.

### Key methods to handle

| Method | Purpose |
|---|---|
| `initialize` | Return `protocolVersion`, `capabilities` (with `tools: {}`), `serverInfo` |
| `notifications/initialized` | Client notification (no `id`) — no response needed |
| `tools/list` | Return array of tool definitions (name, description, inputSchema) |
| `tools/call` | Execute tool by name, return `content` array + `isError` flag |
| `ping` | Return empty object |

### Important: notifications vs requests

Messages without an `id` field are **notifications** — do NOT send a response. Only messages with an `id` get a response.

### Two kinds of errors

1. **Tool errors** — the tool itself failed (e.g., file not found). Return a normal JSON-RPC response with `result.isError = true` and the error message in `content[0].text`.
2. **Protocol errors** — malformed request, unknown method. Return a JSON-RPC `error` object with `code` and `message`.

```json
// Tool error (result-level):
{"jsonrpc":"2.0","id":1,"result":{"content":[{"type":"text","text":"File not found"}],"isError":true}}

// Protocol error (error-level):
{"jsonrpc":"2.0","id":1,"error":{"code":-32603,"message":"Internal error"}}
```

### Architecture

- `McpServer` — reads lines from stdin, dispatches to handlers, writes responses to stdout
- `McpToolDef(name, description, inputSchema, handler)` — tool registration record
- `McpToolResult(text, isError)` — tool return value
- All logging goes to **stderr** — stdout is reserved for the protocol

### MSBuild registration must be first

```csharp
// Program.cs — very first line
Microsoft.Build.Locator.MSBuildLocator.RegisterDefaults();
```

### Testing without a real MCP client

Pipe a text file with JSON-RPC messages into stdin:

```
{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}
{"jsonrpc":"2.0","method":"notifications/initialized"}
{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}
{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"ping","arguments":{}}}
```

```bash
type test_input.txt | dotnet run --no-build
```

## Gotcha 3: Roslyn 5.3 API deprecations and signature changes

Several Roslyn APIs changed in 5.x and will cause build errors if you use older patterns.

### `WorkspaceFailed` event → `RegisterWorkspaceFailedHandler`

**Symptom:** warning CS0618: `"Workspace.WorkspaceFailed" is obsolete: 'Use RegisterWorkspaceFailedHandler instead'`

**Fix:** Replace the event handler with the method-based registration:

```csharp
// ❌ Deprecated:
_workspace.WorkspaceFailed += (_, e) => Console.Error.WriteLine(e.Diagnostic.Message);

// ✅ Correct — note: takes Action<WorkspaceDiagnosticEventArgs>, NOT EventHandler<(sender, e)>
ws.RegisterWorkspaceFailedHandler(e =>
    Console.Error.WriteLine($"[Workspace] {e.Diagnostic.Kind}: {e.Diagnostic.Message}"));
```

**Gotcha within the gotcha:** the delegate signature is `Action<WorkspaceDiagnosticEventArgs>` (single arg), not `EventHandler` (two args: sender, e). Using `(_, e) =>` causes CS1593 "delegate does not take 2 arguments".

### `FindSymbolAtPosition` → `FindSymbolAtPositionAsync`

**Symptom:** warning CS0618, same deprecation pattern.

```csharp
// ❌ Deprecated (synchronous):
var symbol = SymbolFinder.FindSymbolAtPosition(model, position, workspace);

// ✅ Correct (async):
var symbol = await SymbolFinder.FindSymbolAtPositionAsync(model, position, workspace);
```

### `SymbolFinder.Find*Async` — `CancellationToken` is NOT positional arg 3

**Symptom:** `CS1503: cannot convert from 'CancellationToken' to 'bool'` (or to `IImmutableSet<Project>`).

**Cause:** `FindDerivedClassesAsync`, `FindImplementationsAsync`, `FindOverridesAsync`, `FindReferencesAsync` all have overloads where the 3rd positional parameter is `bool` or `IImmutableSet<Project>?`, not `CancellationToken`.

**Fix:** Use named argument `cancellationToken: ct`:

```csharp
// ❌ Fails — 3rd positional arg is NOT CancellationToken:
var derived = await SymbolFinder.FindDerivedClassesAsync(type, sol, ct);

// ✅ Correct — named argument:
var derived = await SymbolFinder.FindDerivedClassesAsync(type, sol, cancellationToken: ct);
var impls = await SymbolFinder.FindImplementationsAsync(type, sol, cancellationToken: ct);
var overrides = await SymbolFinder.FindOverridesAsync(method, sol, cancellationToken: ct);
```

### `FindSourceDeclarationsAsync` returns `IEnumerable<ISymbol>` — use `.Count()` not `.Count`

```csharp
var symbols = await SymbolFinder.FindSourceDeclarationsAsync(sol, predicate, ct);
// symbols.Count → CS0019 (method group vs int)
// symbols.Count() → correct, LINQ extension
```

## Roslyn tools design

For a code-navigation MCP server, these tool categories work well:
- **Workspace management**: open_solution, open_project, get_workspace_info, reload_workspace
- **Symbol navigation**: find_symbol, go_to_definition, find_references, find_implementations, get_type_hierarchy
- **Code structure**: get_document_outline, get_type_members, get_method_body, get_source_text
- **Semantic analysis**: get_symbol_info, get_diagnostics, get_callers, get_callees

Each tool takes a `JsonObject` of arguments and returns text (results are serialized as MCP content blocks).

### Position-based symbol resolution pattern

Many tools (go_to_definition, find_references, etc.) work from a cursor position (file + line + column, 1-based). The conversion to a Roslyn `SyntaxToken`:

```csharp
var root = await doc.GetSyntaxRootAsync();
var sourceText = await doc.GetTextAsync();
var linePosition = new LinePosition(line - 1, column - 1);  // convert 1-based → 0-based
var position = sourceText.Lines.GetPosition(linePosition);
var token = root.FindToken(position);
```

Then resolve the symbol:
```csharp
var symbol = await SymbolFinder.FindSymbolAtPositionAsync(
    await doc.GetSemanticModelAsync(), token.SpanStart, workspace);
```

### WorkspaceService singleton pattern

A single `WorkspaceService` holds the `MSBuildWorkspace` instance. Tools call `ws.RequireSolution()` (throws `McpToolException` if not loaded) and `ws.FindDocument(filePath)` (case-insensitive path matching on Windows).

## Gotcha 4: `ReferenceLocation.DocumentId` does not exist in Roslyn 5.3

**Symptom:** `CS1061: "ReferenceLocation" does not contain a definition for "DocumentId"`.

**Cause:** In older Roslyn versions, `ReferenceLocation` had a `DocumentId` property. In 5.3, this was removed or is inaccessible.

**Fix:** Find documents by matching file paths from `ReferenceLocation.Location.SourceTree?.FilePath`:

```csharp
// ❌ Fails — DocumentId not available:
var locDoc = sol.GetDocument(loc.DocumentId);

// ✅ Correct — match by file path:
var locDoc = sol.Projects
    .SelectMany(p => p.Documents)
    .FirstOrDefault(d => d.FilePath is not null
        && string.Equals(d.FilePath, loc.Location.SourceTree?.FilePath,
            StringComparison.OrdinalIgnoreCase));
```

## Gotcha 5: `FindSymbolAtPositionAsync` returns the most specific symbol, not the method

**Symptom:** When a tool is called with a position on a method name, `FindSymbolAtPositionAsync` returns a parameter, local variable, or type — not the method symbol itself. This breaks `get_callers`/`get_callees` which need `IMethodSymbol`.

**Cause:** `FindSymbolAtPositionAsync` resolves the token at the position to the most specific symbol. In a method signature like `Task RunAsync(CancellationToken ct)`, clicking on `CancellationToken` returns the type, clicking on `ct` returns the parameter — neither is the method.

**Fix:** Create a `ResolveMethodAtPositionAsync` helper with a fallback that walks up the syntax tree to `BaseMethodDeclarationSyntax`:

```csharp
public static async Task<IMethodSymbol?> ResolveMethodAtPositionAsync(
    Document doc, int line, int column, Workspace workspace)
{
    var (token, _) = await GetTokenAtPositionAsync(doc, line, column);
    var model = await doc.GetSemanticModelAsync();
    if (model is null) return null;

    // Try direct resolution first
    var symbol = await SymbolFinder.FindSymbolAtPositionAsync(
        model, token.SpanStart, workspace);
    if (symbol is IMethodSymbol method)
        return method;

    // Fallback: walk up syntax tree to method declaration
    var node = token.Parent;
    while (node is not null and not BaseMethodDeclarationSyntax)
        node = node.Parent;

    if (node is BaseMethodDeclarationSyntax methodDecl)
        return model.GetDeclaredSymbol(methodDecl) as IMethodSymbol;

    return null;
}
```

**Note:** `model.GetDeclaredSymbol(methodDecl)` returns `ISymbol?`, not `IMethodSymbol?` — must cast with `as IMethodSymbol`.

## Gotcha 6: String interpolation with conditional expression before `:`

**Symptom:** `CS8361: A conditional expression cannot be used directly in interpolation because the interpolation ends with ":"`.

```csharp
// ❌ Fails — ':' is interpreted as format specifier:
sb.AppendLine($"Type: {method.ReturnsVoid ? "void" : method.ReturnType.ToDisplayString()}");

// ✅ Fix — wrap in parentheses:
sb.AppendLine($"Type: {(method.ReturnsVoid ? "void" : method.ReturnType.ToDisplayString())}");
```

This applies to any `?:` ternary inside `$"..."` where the next character after the closing `?` branch is `:`. The compiler interprets the `:` as a format specifier delimiter.

## Gotcha 7: Missing `using` directives for Roslyn sub-namespaces

Several commonly-needed types live in sub-namespaces of `Microsoft.CodeAnalysis` that are NOT pulled in by `using Microsoft.CodeAnalysis;`:

| Type | Required using |
|---|---|
| `SymbolFinder` | `using Microsoft.CodeAnalysis.FindSymbols;` |
| `CSharpParseOptions` | `using Microsoft.CodeAnalysis.CSharp;` |
| `*Syntax` types (ClassDeclarationSyntax, etc.) | `using Microsoft.CodeAnalysis.CSharp.Syntax;` |
| `LinePosition`, `SourceText` | `using Microsoft.CodeAnalysis.Text;` |

Forgetting these causes `CS0103: The name 'X' does not exist in the current context` or `CS0246: Type or namespace not found`.

## Gotcha 8: BuildHost DLLs missing from single-file publish

**Symptom:** Published single-file exe fails at runtime when calling `open_solution` or `open_project`:
```
The build host could not be found at '...\publish\BuildHost-netcore\Microsoft.CodeAnalysis.Workspaces.MSBuild.BuildHost...'
```

**Cause:** `Microsoft.CodeAnalysis.Workspaces.MSBuild` uses a separate **BuildHost process** to load MSBuild and communicate with `MSBuildWorkspace`. The NuGet package includes `BuildHost-netcore\` and `BuildHost-net472\` directories (in `contentFiles\any\any\`) containing DLLs + config files. During normal `dotnet build`, these are copied to the output directory. But `PublishSingleFile=true` packs managed DLLs into the exe, leaving only the `.deps.json` and `.runtimeconfig.json` config files — the BuildHost DLLs vanish.

**Fix:** Add a post-publish MSBuild target to the `.csproj` that copies the BuildHost files from the NuGet cache:

```xml
<Target Name="CopyBuildHostToPublish" AfterTargets="Publish">
  <PropertyGroup>
    <BuildHostSource>$(NuGetPackageRoot)microsoft.codeanalysis.workspaces.msbuild\5.3.0\contentFiles\any\any</BuildHostSource>
  </PropertyGroup>
  <ItemGroup>
    <BuildHostNetCoreFiles Include="$(BuildHostSource)\BuildHost-netcore\**\*" />
    <BuildHostNet472Files Include="$(BuildHostSource)\BuildHost-net472\**\*" />
  </ItemGroup>
  <Copy SourceFiles="@(BuildHostNetCoreFiles)"
        DestinationFiles="$(PublishDir)BuildHost-netcore\%(RecursiveDir)%(FileName)%(Extension)" />
  <Copy SourceFiles="@(BuildHostNet472Files)"
        DestinationFiles="$(PublishDir)BuildHost-net472\%(RecursiveDir)%(FileName)%(Extension)" />
</Target>
```

**Note:** The version `5.3.0` in the path must match the `PackageReference` version. The published output is not a *pure* single file — `BuildHost-netcore\` (5 files, ~750 KB) and `BuildHost-net472\` (15 files, ~2 MB) must sit next to the exe. This is an inherent limitation of `MSBuildWorkspace` — the BuildHost is a separate process that needs its own assemblies.

## Published output structure

```
publish/
├── RoslynMcp.exe              # ~47 MB single-file, self-contained
├── RoslynMcp.pdb
├── BuildHost-netcore/         # 5 files — .NET Core build host (required)
└── BuildHost-net472/          # 15 files — .NET Framework build host (required)
```

Total size: ~50 MB. The exe alone will start and handle `initialize`/`tools/list`/`ping`, but `open_solution`/`open_project` will fail without the BuildHost directories.

## Gotcha 9: BuildHost cannot be embedded into the single-file exe

**Attempted approach:** Exclude `contentFiles` from the MSBuild package, embed BuildHost DLLs as `EmbeddedResource`, and extract them to disk at runtime.

**Why it fails:** The .NET SDK auto-includes items matching `EmbeddedResource` glob patterns from the project directory AND from NuGet `contentFiles`. The result is `NETSDK1022: Duplicate "EmbeddedResource" items` — the same DLL files appear both from the NuGet `contentFiles\any\any\BuildHost-*\` path and from the explicit `<EmbeddedResource Include="...">`.

Setting `<EnableDefaultEmbeddedResourceItems>false</EnableDefaultEmbeddedResourceItems>` would disable ALL default embedded resources globally, breaking other functionality. There is no per-item opt-out.

**Conclusion:** The post-publish copy target (Gotcha 8) is the only working approach. BuildHost must remain as separate files next to the exe.

## Logging with NLog (programmatic configuration)

For an MCP stdio server, logging must go to **files and stderr only** — stdout is reserved for the JSON-RPC protocol. NLog 6.x with programmatic config (no `nlog.config` file — better for single-file publish):

```csharp
public static void Setup(string? logFilePath, string level = "Info")
{
    var config = new LoggingConfiguration();
    var logLevel = LogLevel.FromString(level);

    if (!string.IsNullOrEmpty(logFilePath))
    {
        var fileTarget = new FileTarget("file")
        {
            FileName = logFilePath,
            Layout = "${longdate} | ${level:uppercase=true:padding=-5} | ${logger} | ${message} ${exception:format=ToString}",
            ArchiveFileName = logFilePath + ".{#}",
            ArchiveAboveSize = 10_000_000,  // 10 MB
            MaxArchiveFiles = 5,
            KeepFileOpen = true,
            AutoFlush = true,
            Encoding = Encoding.UTF8
        };
        // Async wrapper — don't block MCP request processing
        var asyncFile = new AsyncTargetWrapper(fileTarget)
        {
            OverflowAction = AsyncTargetWrapperOverflowAction.Discard,
            QueueLimit = 1000
        };
        config.AddRule(logLevel, LogLevel.Fatal, asyncFile);
    }

    // stderr — always, for live debugging; Warn+ only to avoid noise
    var stderrTarget = new ConsoleTarget("stderr")
    {
        StdErr = true,
        Layout = "${level:uppercase=true:padding=-5} | ${logger:shortName=true} | ${message}"
    };
    config.AddRule(LogLevel.Warn, LogLevel.Fatal, stderrTarget);

    LogManager.Configuration = config;
}
```

CLI args for log configuration:
```csharp
// --log-level <Trace|Debug|Info|Warn|Error>  (default: Info)
// --log-file <path>                          (default: %LOCALAPPDATA%\RoslynMcp\server.log)
```

Log every tool invocation with timing:
```csharp
Logger.Info($"Вызов инструмента: {name}");
var sw = Stopwatch.StartNew();
var result = await tool.Handler(arguments, ct);
sw.Stop();
Logger.Info($"Инструмент {name} выполнен за {sw.ElapsedMilliseconds} мс");
```

### Gotcha 9a: NLog 6 `ColoredConsoleTarget.ErrorStream` → `ConsoleTarget.StdErr`

`ColoredConsoleTarget.ErrorStream = true` is obsolete in NLog 6. Use `ConsoleTarget` with `StdErr = true`:

```csharp
// ❌ Deprecated (NLog 5+):
var target = new ColoredConsoleTarget { ErrorStream = true };

// ✅ Correct (NLog 6):
var target = new ConsoleTarget { StdErr = true };
```

## Code modification tools (token economy)

MCP modification tools save tokens dramatically compared to read-file + edit + write-file cycles:

| Approach | Token cost |
|---|---|
| Client reads 500-line file, edits, writes back | ~500 in + ~500 out |
| `apply_text_change` (position + new text) | ~10 in + ~5 out |
| `rename_symbol` (position + new name, Roslyn handles all references) | ~5 in + ~5 out |

### Tool categories for modification

- `apply_text_change` — replace a text range (startLine:startCol – endLine:endCol) with new text, in-memory
- `rename_symbol` — Roslyn `Renamer` refactoring across entire solution, in-memory
- `get_modified_documents` — list documents with unsaved changes
- `save_document` / `save_all_changes` — persist to disk

### In-memory modification pattern

Changes stay in the Roslyn `Solution` object in memory. The workflow is:
1. `open_project` — load workspace
2. `apply_text_change` or `rename_symbol` — modify in memory
3. `get_diagnostics` — verify correctness instantly (no `dotnet build` needed)
4. `save_all_changes` — persist to disk
5. `reload_workspace` — discard unsaved changes if needed

### `ApplyTextChangeAsync` implementation

```csharp
var sourceText = await doc.GetTextAsync(ct);
var start = sourceText.Lines[startLine - 1].Start + startColumn - 1;
var end = sourceText.Lines[endLine - 1].Start + endColumn - 1;
var textSpan = new TextSpan(start, end - start);
var change = new TextChange(textSpan, newText);
var newDoc = doc.WithText(sourceText.WithChanges(change));
_solution = newDoc.Project.Solution;  // update the workspace solution
```

## Gotcha 10: `Renamer.RenameSymbolAsync` returns `Solution`, not a tuple

**Symptom:** `CS8129: No suitable Deconstruct instance` / `CS0411: Type arguments cannot be inferred`.

**Cause:** In older Roslyn versions, `RenameSymbolAsync` returned a tuple `(Solution, ChangedDocuments)`. In 5.3, it returns just `Solution`.

```csharp
// ❌ Fails — tuple deconstruction no longer works:
var (newSolution, changedDocs) = await Renamer.RenameSymbolAsync(
    solution, symbol, newName, workspace.Options, ct);

// ✅ Correct — returns Solution only:
var newSolution = await Renamer.RenameSymbolAsync(
    solution, symbol, /* options */, newName, ct);

// Count changed documents manually via SolutionChanges:
var changes = oldSolution.GetChanges(newSolution);
var changedDocs = changes.GetProjectChanges()
    .SelectMany(pc => pc.GetChangedDocuments())
    .Count();
```

### Gotcha 10a: migrating off the obsolete `OptionSet` overload (CS0618)

The old overload `RenameSymbolAsync(Solution, ISymbol, string newName, OptionSet?, CancellationToken)` is **obsolete** (CS0618: "Use overload taking RenameOptions"). Migrating to the non-deprecated overload is **not** a simple type swap — three traps make it trial-and-error:

**Trap 1 — `RenameOptions` is itself obsolete.** The deprecation message says "Use `RenameOptions`", but `RenameOptions` is a `static class` (cannot be instantiated) and is *also* obsolete: "Use `SymbolRenameOptions` or `DocumentRenameOptions` instead". The type you actually want is **`Microsoft.CodeAnalysis.Rename.SymbolRenameOptions`**.

**Trap 2 — the parameter ORDER changed, not just the type.** This is the killer. The two overloads have different argument orders:

| Overload | Arg 3 | Arg 4 | Status |
|---|---|---|---|
| Old | `string newName` | `OptionSet?` | obsolete (CS0618) |
| New | `SymbolRenameOptions` | `string newName` | current |

Because `newName` moved from position 3 → 4, a naive in-place type swap (`OptionSet` → `SymbolRenameOptions` keeping `newName` in position 3) fails overload resolution and silently falls back to the obsolete overload, re-triggering CS0618. You **must** reorder the arguments.

**Trap 3 — `SymbolRenameOptions` is a struct, not nullable.** Passing `null` is ambiguous and resolves to the obsolete `OptionSet?` overload. Pass `default(SymbolRenameOptions)` instead.

**Correct call (Roslyn 5.3):**

```csharp
// ❌ Obsolete — old arg order, OptionSet 4th:
var newSolution = await Renamer.RenameSymbolAsync(
    solution, symbol, newName, workspace.Options, ct);  // CS0618

// ❌ Wrong — type swapped but order kept, falls back to obsolete overload:
var newSolution = await Renamer.RenameSymbolAsync(
    solution, symbol, newName, default(SymbolRenameOptions), ct);  // still CS0618

// ✅ Correct — SymbolRenameOptions 3rd, newName 4th:
var newSolution = await Renamer.RenameSymbolAsync(
    solution, symbol,
    default(Microsoft.CodeAnalysis.Rename.SymbolRenameOptions),  // arg 3
    newName,                                                        // arg 4
    ct);
```

**How to discover the real overload signatures:** `find_symbol` only searches the *opened project's* source symbols — it cannot see methods inside referenced assemblies like `Microsoft.CodeAnalysis.Workspaces`. Use **`get_signature_help`** positioned inside the call parentheses (e.g. column ~80 on the `RenameSymbolAsync(` line) to list all overloads with their exact parameter names and order. This is what revealed the reordering.

## Gotcha 11: `SolutionChanges.GetProjects()` does not exist — use `GetProjectChanges()`

**Symptom:** `CS1061: "SolutionChanges" does not contain a definition for "GetProjects"`.

```csharp
var changes = oldSolution.GetChanges(newSolution);

// ❌ Fails — GetProjects() doesn't exist:
changes.GetProjects().Select(p => changes.GetProjectChanges(p)...)

// ✅ Correct — GetProjectChanges() returns IEnumerable<ProjectChanges>:
changes.GetProjectChanges()
    .SelectMany(pc => pc.GetChangedDocuments())
    .Count();
```

## Complete tool inventory (26 tools)

| Category | Tools |
|---|---|
| Workspace | `open_solution`, `open_project`, `get_workspace_info`, `reload_workspace` |
| Navigation | `find_symbol`, `go_to_definition`, `find_references`, `find_implementations`, `get_type_hierarchy` |
| Structure | `get_document_outline`, `get_type_members`, `get_method_body`, `get_source_text` |
| Analysis | `get_symbol_info`, `get_diagnostics`, `get_callers`, `get_callees` |
| Additional | `format_document`, `get_project_references`, `get_signature_help` |
| Modification | `apply_text_change`, `rename_symbol`, `get_modified_documents`, `save_document`, `save_all_changes` |
| Utility | `ping` |

### `get_diagnostics` works without compilation

Roslyn's semantic model provides diagnostics in-memory — no `dotnet build` needed. This enables a fast feedback loop: modify code → `get_diagnostics` → see errors instantly.

### In-memory changes vs disk

Roslyn modifications (`WithText`, `RenameSymbolAsync`) update the `Solution` object in memory only. `reload_workspace` discards all unsaved changes by re-reading from disk. `save_all_changes` compares each document's in-memory text against the disk file and writes only changed files.

## Gotcha 12: `dotnet build` is not enough — the MCP server runs from `publish/`

**Symptom:** After editing server source code and running `dotnet build`, MCP tool calls still use the old schema/behavior. New parameters (e.g. `offset`, `limit`) are silently ignored, and `tool_search` shows the old parameter list.

**Cause:** The MCP client (Qwen Code, Claude Desktop, etc.) launches the server from the **published** binary, not the build output. Check with:

```bash
wmic process where "name='RoslynMcp.exe'" get ProcessId,ExecutablePath /format:list
# → ExecutablePath=E:\...\bin\Release\net10.0\win-x64\publish\RoslynMcp.exe
```

`dotnet build` writes to `bin/Release/net10.0/`; `dotnet publish` writes to `bin/Release/net10.0/win-x64/publish/`. The client points at the latter.

**Fix — the restart cycle:**

```bash
# 1. Kill the running server process
taskkill /f /im RoslynMcp.exe          # Windows
# pkill -f RoslynMcp                    # Linux/macOS

# 2. Publish the new binary (includes BuildHost copy target)
dotnet publish "path\to\RoslynMcp.csproj" -c Release -r win-x64 --nologo -v q

# 3. Make any MCP tool call (ping, open_solution, etc.) — the client
#    detects the dead process and auto-restarts from publish/
```

**Side effects of restart:**
- **Workspace is lost** — the server restarts fresh. You must `open_solution` / `open_project` again before any analysis tools work.
- **Tool schemas are refreshed** — `tool_search` picks up new parameters added to `inputSchema` JSON only after the restart. Pre-restart `tool_search` shows cached old schemas.
- **Parallel MCP calls during restart fail** — if the client issues multiple calls before the new process is ready, some get "Решение не загружено" errors. Issue calls sequentially after restart until `open_solution` returns success.

## Pagination on navigation tools (large-solution support)

Four navigation tools accept `offset` + a limit parameter to handle large solutions (tested on EFCore: 65 projects, ~5000 docs, `DbContext` has 1184 inheritors):

| Tool | Parameters | Defaults |
|---|---|---|
| `find_symbol` | `offset`, `maxResults` | 0, 50 |
| `find_references` | `offset`, `maxResults` | 0, 100 |
| `find_implementations` | `offset`, `limit` | 0, 100 |
| `get_type_hierarchy` | `offset`, `limit` | 0, 100 |

When results are truncated, the response includes `total`, `shown`, and `nextOffset`:
```
Наследники (1184):
  Показано: 100 (offset=0, limit=100), следующая страница: offset=100
  Context11885 (NamedType) @ ...:825
  ...
```

### `AppendPaged<T>` helper

For tools returning uniform lists (`find_implementations`, `get_type_hierarchy`), a shared helper handles pagination formatting:

```csharp
private static void AppendPaged<T>(
    StringBuilder sb, string label, IEnumerable<T> items,
    int offset, int limit, Func<T, string> formatter)
{
    var all = items.ToList();  // materialize once
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
```

**Why `ToList()` before `Skip/Take`:** Roslyn returns `IEnumerable<T>` from `FindDerivedClassesAsync` etc. Calling `.Count()` separately forces a second enumeration. Materializing once avoids this and is required for `total` + page slice to be consistent.

**`find_symbol` and `find_references` use inline pagination** (not `AppendPaged`) because their output format differs — `find_symbol` includes XML docs per symbol, `find_references` includes code context lines per location.
