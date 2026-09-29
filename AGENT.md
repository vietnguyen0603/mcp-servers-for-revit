# Repository Agent Guide

## Scope

This repository connects MCP clients to Autodesk Revit through three runtime layers:

- `server/`: TypeScript MCP server and WebSocket client.
- `plugin/`: C# add-in hosted inside Revit; owns sockets, command discovery, and external events.
- `commandset/`: C# Revit API commands, handlers, DTOs, and extraction logic.

`command.json` is the command-set manifest. `tests/commandset/` contains live-Revit tests, while `tests/RegisterGeometry.Tests/` contains host-independent geometry tests.

## Ownership

- Route MCP schemas, TypeScript tool facades, SQLite services, and Node packaging to the server domain.
- Route socket lifecycle, command loading, settings, and Revit host-thread behavior to the plugin domain.
- Route Revit API operations, transactions, extraction services, DTOs, and geometry to the command-set domain.
- Route CI, release packaging, solution wiring, documentation, and cross-layer verification to the quality/release domain.

For cross-layer features, define the wire contract first. Keep the tool name identical and case-sensitive in the TypeScript facade, `command.json`, and `IExternalEventCommandBase.CommandName`.

## Engineering Rules

- Preserve support for Revit 2020-2026 unless the task explicitly narrows compatibility.
- Revit 2020-2024 target .NET Framework 4.8; Revit 2025-2026 target .NET 8 Windows.
- Execute Revit API work through the established external-event path and on the Revit main thread.
- Use the correct Revit transaction mode; read-only extraction must not open unnecessary transactions.
- Preserve linked-document transforms, internal-feet conversions, deterministic ordering, and explicit tolerances.
- Keep `commandset/RegisterGeometry/` free of Autodesk references and compatible with `netstandard2.0`.
- Validate untrusted MCP input at the TypeScript boundary and again where Revit-specific semantics require it.
- Make the smallest correct change and do not refactor unrelated code.
- Do not commit generated outputs, credentials, local databases, or OpenCode configuration.

## Verification

Run the narrowest relevant checks first, then the broader gates:

```powershell
cd server
npm test
npm run build
```

```powershell
dotnet test tests/RegisterGeometry.Tests -c Debug
dotnet build commandset -c "Debug R24"
dotnet build commandset -c "Debug R25"
```

Live tests under `tests/commandset/` require the .NET 10 SDK and a running licensed Revit 2025 or 2026 host. Report that prerequisite as an unavailable verification layer rather than treating it as a product failure.

Before committing, inspect `git status`, the full intended diff, and recent commit style. Never commit or push unless the user explicitly requests it.
