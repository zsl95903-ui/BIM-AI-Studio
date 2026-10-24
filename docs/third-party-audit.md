# Third-Party Audit

Audit date: 2026-10-22
Release: `toolset 2.0.0 / bridge 2.1.0`
Scope: BIM-AI Studio source tree and release packages.

## 1. Repository References

| File | Reference | License | Distribution decision |
|---|---|---|---|
| `src/ReVitAI.Bridge/ReVitAI.Bridge.csproj` | `RevitAPI.dll` | Autodesk proprietary | Build from local installation; do not redistribute |
| `src/ReVitAI.Bridge/ReVitAI.Bridge.csproj` | `RevitAPIUI.dll` | Autodesk proprietary | Build from local installation; do not redistribute |
| `toolset/src/RevitAi.Engine.Revit/RevitAi.Engine.Revit.csproj` | `ACadSharp` 3.4.9 | MIT | NuGet restore; package is not committed |
| Project C# files | `System.*` | .NET runtime license | Used as platform libraries |
| Project C# files | `Autodesk.Revit.*` | Autodesk proprietary API | Compile-time reference only |

## 2. Package Boundary

- The source repository contains project-authored source, documentation, and build scripts.
- `toolset/` contains the Apache-2.0 120-tool source set, generated classes, tests and services.
- The repository contains project-authored source only.
- UI, account, billing, entitlement and update modules are not included.
- The release contains the Bridge and required project-authored toolset assemblies, plus MIT-licensed ACadSharp.
- Autodesk API binaries are not included.
- `ReVitAI.Bridge` requires no NuGet `PackageReference`; `toolset` uses MIT-licensed `ACadSharp` 3.4.9.
- No external installer or compiled dependency assembly is committed to the repository.

## 3. Release Checklist

- [x] Confirm the assembly name and namespace are `ReVitAI.Bridge`.
- [x] Confirm the bridge assembly version is `2.1.0.0`.
- [x] Verify unified runtime dependencies are present and Autodesk API assemblies are excluded.
- [x] Confirm Autodesk API binaries are not packaged.
- [x] Confirm no local model, log, screenshot, or export file is packaged.
- [x] Confirm no credential or test endpoint is packaged.
- [x] Generate SBOM and SHA256 checksums.
- [ ] Add code-signing certificate before a signed release.

## 4. Redistribution Rule

Any future external binary must be reviewed before packaging. A component with unclear redistribution terms must not be added without a separate audit and updated notice file.
