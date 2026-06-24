---
name: csharp-roslyn-analysis
description: Workflow for analyzing a C# project when a Roslyn code-analysis MCP server is registered — prefer semantic tools (open_project + get_diagnostics over all files + get_workspace_info + get_callers/get_callees) over manual read_file, which misses compiler warnings and misreports facts like language version and document count.
source: auto-skill
extracted_at: '2026-06-25T00:00:00.000Z'
---

# Analyzing a C# project via a registered Roslyn MCP server

When a Roslyn-based MCP server (or any semantic code-analysis MCP server) is
registered and reachable, **do not start code analysis with `read_file` /
`list_directory`**. The semantic tools surface facts that manual reading
cannot reliably produce and that visual inspection misses.

## Why this exists

The user corrected an analysis that began by reading files by hand:
*"это mcp сервер зарегистрирован у тебя, почему не использовал для анализа?"*

Concrete evidence from that session — manual reading **missed** three things
that the MCP tools found immediately:

| Fact | Manual read_file said | MCP `get_diagnostics`/`get_workspace_info` said |
|---|---|---|
| Compiler warnings | "clean, 0 warnings" | **CS0618** obsolete `Renamer.RenameSymbolAsync` at WorkspaceService.cs:197 |
| Language version | guessed "CSharp13" | **CSharp14** |
| Document count | "9 files" (source only) | **15** (Roslyn counts obj/ generated docs too) |

The warning in particular is invisible to a reader unless they already know the
API is deprecated — `get_diagnostics` knows because it runs the compiler.

## Procedure (run in this order)

1. **Load the workspace** — `open_project` (single .csproj) or `open_solution`
   (.sln). No analysis is possible until a workspace is loaded; most tools
   throw "Решение не загружено" otherwise.
2. **`get_workspace_info`** — authoritative project facts: language version,
   document count, reference count, project list. Do not guess these.
3. **`get_diagnostics` on every source file** — this is the highest-value step
   and the one most often skipped. It runs the Roslyn compiler in-memory and
   returns errors + warnings (CS#### codes) with file:line:col. No `dotnet
   build` needed. Batch all files in one parallel tool block.
4. **`get_document_outline`** per file — cheap structural overview (types /
   methods / fields with line numbers) without pulling full source.
5. **`get_callers` / `get_callees`** on key methods — maps the real dependency
   graph. Use this to confirm isolation claims (e.g. "modifying APIs are only
   called from one class") rather than eyeballing `using` statements.
6. **`get_symbol_info` / `get_type_members` / `get_type_hierarchy`** for deep
   dives on specific symbols the user asks about.
7. **`read_file` only when** you need the exact text of a region to edit it,
   quote it, or reason about logic the semantic tree abstracts away.

## Position sensitivity gotcha

`get_symbol_info` / `get_type_members` are column-sensitive. Aiming at the
identifier of `class Foo` may return "Символ в данной позиции не является
типом" because the token at that column is the name token, not the declaration.
If a position-based query returns "not a type"/"symbol not found", shift the
column a few places along the line before concluding the symbol is absent.
`get_document_outline` is column-free and a safer first pass to locate a
symbol's exact line before calling position-based tools.

## When manual read_file is still right

- The user asks to *modify* a specific region (you need exact bytes to edit).
- You need to inspect comments, formatting, or string literals verbatim.
- No semantic MCP server is registered for the target language.
- Reading config files, .csproj XML, .gitignore, markdown — non-code artifacts
  the Roslyn server does not index.

## Large-solution behavior (tested on EFCore.sln — 65 projects, ~5000 docs)

On a 65-project solution the MCP server stayed responsive for most tools, but
three concrete failure modes appeared. Plan around them when the solution has
more than a few hundred documents.

### `find_symbol` times out on common names

`find_symbol "^DbContext$"` on EFCore **timed out** — `FindSourceDeclarationsAsync`
walks every document in every project and runs the regex per declaration. On
5000+ documents this exceeds the MCP request timeout.

**Workarounds:**
- Use the most specific regex you can (`^DbContext$` still timed out, but
  `^DbContextOptions$` returned 2 results instantly — fewer candidate matches
  across the solution).
- For symbols you can locate by file, skip `find_symbol` entirely: `glob` for
  the file, then `get_document_outline` to find the line, then position-based
  tools (`get_symbol_info`, `get_type_members`).
- `find_symbol` only searches the *opened project's source* — it will not find
  symbols inside referenced assemblies (e.g. `RenameSymbolAsync` in
  `Microsoft.CodeAnalysis`). Use `get_signature_help` inside a call site for
  those.

### `get_type_hierarchy` returns huge payloads for popular base types

`get_type_hierarchy` on `DbContext` returned **141 KB** (1184 inheritors —
mostly test contexts). The response was auto-saved to a tmp file because it
exceeded the inline limit. On real codebases this is common: any well-known
base type/interface has hundreds-to-thousands of descendants.

**Workarounds:**
- Treat `get_type_hierarchy` on popular types as expensive; only call it when
  the user specifically asks for the hierarchy, not as part of routine analysis.
- **Pagination is now supported:** all four navigation tools (`find_symbol`,
  `find_references`, `find_implementations`, `get_type_hierarchy`) accept
  `offset` + `maxResults`/`limit` parameters. Use `offset=0&limit=100` for the
  first page, then follow the `nextOffset` value in the response for subsequent
  pages. This keeps responses compact even when there are 1000+ results.

### Parallel `reload_workspace` wipes the solution for in-flight requests

During the CS0618 fix, multiple `reload_workspace` calls were fired in parallel
against the *RoslynMcp* project while the *EFCore* solution was loaded. The
reload recreates the single `WorkspaceService._workspace` field. Concurrent
calls produced "Решение не загружено" errors for the EFCore `get_type_members`
/ `get_callers` calls that were in flight — they saw an empty workspace mid-reload.

**Rule:** never fire `reload_workspace` in the same tool block as other
analysis tools. It is a destructive global operation. Run it alone, wait for
the result, then issue the next batch.

### What worked fine on the large solution

These stayed fast and returned compact output even on EFCore:
- `open_solution` (~5 s for 65 projects)
- `get_workspace_info` (full project+reference listing, ~3 KB)
- `get_document_outline` on a 2300-line file (DbContext.cs → ~150 tree nodes)
- `get_diagnostics` per file (compiler run, sub-second)
- `get_callees` on a single method (1 callee — `SaveChanges()` → `SaveChanges(bool)`)

The pattern that scaled: `open_solution` → `get_workspace_info` →
`get_document_outline` (cheap structure) → targeted `get_diagnostics` /
`get_callees` on specific files/methods. The tools that scaled poorly
(`find_symbol`, `get_type_hierarchy` on popular types) are the ones that walk
the entire solution graph.
