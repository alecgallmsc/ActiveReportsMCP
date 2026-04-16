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
- Layout/data patching: tables/tablix/charts/textboxes, dataset/data source updates
- Style/formatting: style patching and style-only format application
- Validation: parse/schema/lint + optional runtime verification
- Layout intelligence: model extraction, scoring, deterministic auto-refine

### `add_chart` layout op (via `report_patch_layout`)

Use `op="add_chart"` with `valueExpression` options:

- `dataset=InvoiceData`
- `category=CustomerName`
- `values=TotalAmount,TaxAmount`
- `chartType=Column|Bar|Line|Area|Pie|Doughnut|Scatter|Bubble|Stock`
- `aggregate=Sum|Avg|Min|Max|Count|CountDistinct|First|Last`
- optional `series=Region`, `legend=true|false`, `palette=Default`, `title=...`

## Notes

- The MCP server is path-based and stateless across tool calls.
- Reports are read/written only at explicit user-provided local file paths.
- See `AGENTS.md` for contributor standards and C# best practices.
