# RdlxMcpServer

Local-first MCP server for creating and editing ActiveReports RDLX reports through typed tool operations.

## Requirements

- .NET SDK 10+
- ActiveReports NuGet packages (resolved through project references)

## Build

```powershell
dotnet build
```

## Run (MCP stdio)

```powershell
dotnet run --project .\RdlxMcpServer.csproj
```

## Self-test

```powershell
powershell -ExecutionPolicy Bypass -File "scripts/run-self-test.ps1" -NoRuntime
```

Optional runtime-inclusive verification:

```powershell
powershell -ExecutionPolicy Bypass -File "scripts/run-self-test.ps1"
```

Self-test output is written to:

- `bin\Debug\net10.0\data\self-test-last.json`

## Core MCP Tool Areas

- Report lifecycle: create, structure, diff, canonical save
- Layout/data patching: tables/tablix/textboxes, dataset/data source updates
- Style/formatting: style patching and style-only format application
- Validation: parse/schema/lint + optional runtime verification
- Layout intelligence: model extraction, scoring, deterministic auto-refine

## Notes

- Default artifact storage path is configurable via `RDLX_STORE_PATH`.
- See `AGENTS.md` for contributor standards and C# best practices.
