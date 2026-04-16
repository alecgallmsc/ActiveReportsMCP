# AGENTS.md

Guidance for contributors and coding agents working in this repository.

## Mission

Build and maintain a local-first MCP server that creates/edits ActiveReports RDLX reports through deterministic, typed operations.

Primary goals:

- Correctness and deterministic output
- Readable, maintainable C# code
- Privacy-safe defaults (local execution, no unnecessary data exposure)
- Backward-compatible behavior for existing MCP tool contracts

## Repository Structure

- `Program.cs`: host bootstrap, DI wiring, self-test mode entry.
- `Tools/ReportTools.cs`: MCP tool surface (public contract).
- `Services/`: business logic and infrastructure
  - `RdlxDocumentService.cs`: RDLX create/patch/canonicalization
  - `LayoutIntelligenceService.cs`: style/format/layout scoring/refinement
  - `RdlxValidationService.cs`: parse/schema/lint checks
  - `RuntimeVerificationService.cs`: ActiveReports API verification path
  - `SelfTestRunner.cs`: deterministic end-to-end smoke flow
- `Models/Contracts.cs`: tool DTOs and response models.
- `scripts/`: local helper scripts (self-test wrapper).

## Development Principles

1. Keep MCP tools semantic and typed; avoid raw XML rewrite tools.
2. Prefer deterministic transforms over heuristic free-form edits.
3. Keep tools path-based and stateless; never rely on server-side report IDs.
4. Keep write operations atomic per output file path.
5. Return actionable diagnostics for all recoverable failures.

## C# Coding Standards

### General

- Target: `net10.0`, nullable reference types enabled.
- Use `required` properties in DTOs when input is mandatory.
- Keep methods single-purpose and testable.
- Use `StringComparer.OrdinalIgnoreCase` for identifiers/refs.
- Prefer explicit invariant culture handling for numeric/unit parsing.

### Naming

- Public methods/classes: `PascalCase`.
- Local vars/parameters: `camelCase`.
- Tool method names intentionally follow MCP naming conventions (`report_*`).

### Error handling

- Do not swallow exceptions silently.
- Map expected failures to structured diagnostics and clear error codes.
- Use non-throwing paths for expected user input errors where possible.

### Comments

- Add comments only for non-obvious logic (design tradeoffs, protocol quirks, compatibility behavior).
- Avoid obvious comments that restate code.

### Performance

- Avoid repeated full-document parsing within a single operation.
- Avoid expensive regex/linq loops inside nested hot paths unless necessary.
- Prefer one-pass collection scans when practical.
- Keep allocations predictable in scoring/refinement loops.

## ActiveReports Compatibility

- Verify generated RDLX through package APIs when available.
- Favor style-level formatting compatibility where validated.
- Preserve designer usability (avoid inflated table/tablix container heights).
- Respect effective printable width unless explicitly in galley mode.

## Security and Privacy

- Default to stateless operation over explicit local report paths.
- Do not persist user report data on the server between calls.
- Never log connection-string secrets in plain text diagnostics.
- Do not add remote calls for report content without explicit requirement.

## MCP Contract Safety Rules

- New tool parameters should be additive and optional when possible.
- Keep response envelope shape stable (`ok`, `message`, `diagnostics`, `artifacts`, etc.).

## Testing and Validation

Before finalizing changes, run:

1. `dotnet build`
2. `powershell -ExecutionPolicy Bypass -File "scripts/run-self-test.ps1" -NoRuntime`
3. Optional runtime path: `powershell -ExecutionPolicy Bypass -File "scripts/run-self-test.ps1"`

If build fails due to a locked `RdlxMcpServer.exe`, stop the running MCP process and rebuild.

## Change Management

- Prefer small, focused commits.
- Keep behavior changes paired with diagnostics updates when applicable.
- Update planning docs in `C:\Repos\rdlx-mcp-plan` for significant contract/architecture changes.
