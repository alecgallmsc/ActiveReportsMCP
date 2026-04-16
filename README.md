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

## Client Configuration

### Codex (`config.toml`)

Add an MCP server entry that starts this project over stdio:

```toml
[mcp_servers.activereports]
command = "dotnet"
args = ["run", "--project", "C:/Repos/RdlxMcpServer/RdlxMcpServer.csproj"]
```

If your checkout path differs, update the project path in `args`.

### OpenCode (MCP settings)

Add the same server in your OpenCode MCP settings:

```json
{
  "mcpServers": {
    "activereports": {
      "command": "dotnet",
      "args": [
        "run",
        "--project",
        "C:/Repos/RdlxMcpServer/RdlxMcpServer.csproj"
      ]
    }
  }
}
```

If your OpenCode setup supports an explicit working directory field, set it to `C:/Repos/RdlxMcpServer`.

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
- Schema inspection: metadata-only table/column discovery with explicit consent gate

### `add_chart` layout op (via `report_patch_layout`)

Use `op="add_chart"` with `valueExpression` options:

- `dataset=InvoiceData`
- `category=CustomerName`
- `values=TotalAmount,TaxAmount`
- `chartType=Column|Bar|Line|Area|Pie|Doughnut|Scatter|Bubble|Stock`
- `aggregate=Sum|Avg|Min|Max|Count|CountDistinct|First|Last`
- optional `series=Region`, `legend=true|false`, `palette=Default`, `title=...`

### `add_table` / `add_tablix` expression options

Use `valueExpression` to override row expressions for headers/details/footers and grouped rows:

- Base options: `dataset=...`, `columns=...`, `groupBy=...`
- Row list overrides (pipe-separated):
  - `headerexprs=...|...|...`
  - `detailexprs=...|...|...`
  - `footerexprs=...|...|...`
  - `groupheaderexprs=...|...|...`
  - `groupfooterexprs=...|...|...`
- Per-column overrides:
  - ordinal keys: `headerexpr1=...`, `groupfooterexpr3=...`
  - name keys: `footerexpr.TotalAmount=...`, `groupheaderexpr.CustomerName=...`
- Expression handling:
  - values starting with `=` are used as-is
  - aggregate shortcuts like `Sum(TotalAmount)` are expanded to valid field expressions
  - plain text is converted to string literals (for example `Grand Total` -> `="Grand Total"`)
  - `report_patch_formatting` now auto-inlines `Format(...)` for matching numeric segments inside mixed label+number expressions (for example `="Value: " & Sum(...)`)

### `report_inspect_schema` consent flow

- The tool never runs without explicit `confirm=true`.
- A non-confirmed call returns `CONSENT_REQUIRED` and a summary of what can be seen.
- Scope is schema metadata only (tables/views/columns/types/nullability/ordinals).
- The tool does not run user SQL and does not read table row values.
- Use `tableAllowList`, `maxTables`, and `maxColumnsPerTable` for least-privilege inspection.
- For `Provider=Microsoft.Jet.OLEDB.*`, schema inspection uses a bundled `x86` helper process (`JetSchemaHelper`) to support legacy Jet provider loading while keeping the main MCP host on `net10.0`.

## Notes

- The MCP server is path-based and stateless across tool calls.
- Reports are read/written only at explicit user-provided local file paths.
- See `AGENTS.md` for contributor standards and C# best practices.
