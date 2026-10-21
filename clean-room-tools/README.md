# RevitAI Clean-Room Tools

Apache-2.0 clean-room Revit 2027 tool layer implementing the 120-tool contract catalog.

## Contents

- `src/RevitAi.Engine.Abstractions` / `Core` / `Revit` / `ToolContracts` / `Addin`
- `src/ExtToolLib` + `src/ExtToolLib.Implementations`
- `tools/ToolSetGenerator`
- `tests/RevitAi.Engine.SmokeTests`
- `samples/revitai-ui-wrapper`

## Boundary

This module contains only project-authored source code. It does **not** contain
external binaries, reviewed external source, or Autodesk Revit API binaries.

`contracts/tool-contracts.json` is interoperability metadata used to generate
the typed tool classes. See `NOTICE.md`.

## Build

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

Create the unified source and add-in package:

```powershell
powershell -ExecutionPolicy Bypass -File .\package.ps1 -Version 0.1.0
```

The build expects a local Revit 2027 installation for `RevitAPI.dll` and
`RevitAPIUI.dll`. Those assemblies are referenced from disk and are not copied
into this repository. NuGet restore also downloads the MIT-licensed
`ACadSharp` 3.4.9 package used by the CAD layer parser.

## License

Project-authored source in this directory is licensed under Apache-2.0. See
`LICENSE` and the repository-level `THIRD_PARTY_NOTICES.md`.
