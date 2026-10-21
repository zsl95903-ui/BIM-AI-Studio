# Clean-Room 120 Tool Rebuild

## Scope

Version 2.0.0 replaces the legacy decompiled source set with the clean-room
tool layer in `clean-room-tools/`.

- 120 third-party contract tools
- 1 project-authored extension: `upsert_level`
- Generated tool classes from `contracts/tool-contracts.json`
- Tool implementations, service layer, contracts and smoke tests
- Apache-2.0 licensing for project-authored source

The legacy `rebuilt-120-tools/` directory, original assemblies and
de-obfuscation intermediates are not part of this release.

## Architecture

The clean-room layer separates responsibilities:

1. Contract catalog defines names, categories, descriptions, JSON Schema and
   transaction flags.
2. `ToolSetGenerator` produces the typed tool classes at build time.
3. `ExtToolLib.Implementations` validates arguments and maps them to services.
4. `RevitAi.Engine.Revit` contains the real Revit API service layer.
5. `RevitAi.Engine.Core` provides registry, invocation, caching and Host routing.
6. `RevitAi.Engine.SmokeTests` verifies tool behavior with a fake adapter.

## Verification

- 120/120 contract tools have implementations.
- 121 generated tools include the self-authored extension.
- 352 smoke assertions pass with 0 failures.
- 117 tools are directly exercised by smoke tests.
- `get_all_levels`, `create_level` and `move_elements` are covered by the
  Revit host self-test.
- The complete Addin compiles against Revit 2027 with 0 errors.
- The unified package can be extracted and rebuilt from its included source.

## Build

```powershell
powershell -ExecutionPolicy Bypass -File .\clean-room-tools\build.ps1
```

Create the source and runtime package:

```powershell
powershell -ExecutionPolicy Bypass -File .\clean-room-tools\package.ps1 -Version 0.1.0
```

## Remaining Validation

The source and package build are verified. A full write matrix against real
Revit documents remains part of ongoing maintenance, particularly for complex
road families, CAD samples and project-specific MEP templates.
