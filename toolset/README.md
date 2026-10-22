# BIM-AI Studio Toolset

Apache-2.0 Revit 2027 tool layer implementing the 120-tool contract catalog.

## Contents

- `src/RevitAi.Engine.Abstractions`, `Core`, `Revit`, `ToolContracts` and
  `Addin`
- `src/ExtToolLib` and `src/ExtToolLib.Implementations`
- `tools/ToolSetGenerator`
- `tests/RevitAi.Engine.SmokeTests`
- `samples/revitai-ui-wrapper`

## Build

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

Create the source and add-in package:

```powershell
powershell -ExecutionPolicy Bypass -File .\package.ps1 -Version 2.0.0
```

The build expects a local Revit 2027 installation. NuGet restore downloads the
MIT-licensed `ACadSharp` 3.4.9 package used by the CAD service.

## Verification

The current smoke suite reports 355 passed assertions and 0 failures. Most
tools are exercised through a simulated Revit adapter. Complex project-specific
families, templates and multi-step modeling workflows still require live Revit
validation.

## License

Project-authored source is licensed under Apache-2.0. See `LICENSE` and the
repository-level `THIRD_PARTY_NOTICES.md`.
