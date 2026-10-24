# Toolset

## Scope

`toolset/` contains the project-owned Revit 2027 tool layer:

- 120 contract tools;
- one project extension: `upsert_level`;
- generated tool classes from `contracts/tool-contracts.json`;
- registry, invocation handler, Revit host and service layer;
- smoke tests and build scripts.

All project-authored source in this directory is licensed under Apache-2.0.

## Architecture

1. The contract catalog defines names, categories, descriptions, JSON Schema
   and transaction flags.
2. `ToolSetGenerator` creates the typed tool classes at build time.
3. `ExtToolLib.Implementations` validates arguments and maps them to services.
4. `RevitAi.Engine.Revit` contains the Revit API service layer.
5. `RevitAi.Engine.Core` provides registry, invocation, caching and host
   routing.
6. `RevitAi.Engine.SmokeTests` verifies behavior through a simulated adapter.

## Verification

- 120/120 contract tools have implementations.
- 121 generated tool entry points include the project extension.
- The current smoke suite reports 352 passed assertions and 0 failures.
- 117 tools are directly exercised by smoke tests.
- Revit-internal self-tests cover the remaining host-bound tools.
- The Addin compiles against the local Revit 2027 API.

## Limitations

Automated tests do not replace live project validation. Complex MEP systems,
project-specific families, CAD content, roads, stairs, topography, batch
operations and multi-process scenarios still require representative Revit
models and manual review.

## Build

```powershell
powershell -ExecutionPolicy Bypass -File .\toolset\build.ps1
```

Create a source and runtime package:

```powershell
powershell -ExecutionPolicy Bypass -File .\toolset\package.ps1 -Version 2.0.0
```
