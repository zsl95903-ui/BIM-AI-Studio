# Third-Party Audit

Audit date: 2026-09-19
Release: `2.1.0`
Scope: BIM-AI Studio source tree and release packages.

## 1. Repository References

| File | Reference | License | Distribution decision |
|---|---|---|---|
| `src/ReVitAI.Bridge/ReVitAI.Bridge.csproj` | `RevitAPI.dll` | Autodesk proprietary | Build from local installation; do not redistribute |
| `src/ReVitAI.Bridge/ReVitAI.Bridge.csproj` | `RevitAPIUI.dll` | Autodesk proprietary | Build from local installation; do not redistribute |
| `clean-room-tools/src/RevitAi.Engine.Revit/RevitAi.Engine.Revit.csproj` | `ACadSharp` 3.4.9 | MIT | NuGet restore; package is not committed |
| Project C# files | `System.*` | .NET runtime license | Used as platform libraries |
| Project C# files | `Autodesk.Revit.*` | Autodesk proprietary API | Compile-time reference only |

## 2. Package Boundary

- The source repository contains project-authored source, documentation, and build scripts.
- `clean-room-tools/` contains the Apache-2.0 120-tool source set, generated classes, tests and services.
- Legacy decompiled source and original assemblies are not stored in the repository.
- UI, account, billing, entitlement and update modules are not included.
- The release package contains one project-authored assembly: `ReVitAI.Bridge.dll`.
- Autodesk API binaries are not included.
- `ReVitAI.Bridge` requires no NuGet `PackageReference`; `clean-room-tools` uses MIT-licensed `ACadSharp` 3.4.9.
- No original third-party installer, compiled assembly or de-obfuscation intermediate is stored in the repository.

## 3. Release Checklist

- [x] Confirm the assembly name and namespace are `ReVitAI.Bridge`.
- [x] Confirm release assembly version is `2.1.0.0`.
- [x] Confirm the release package contains one distributed assembly.
- [x] Confirm Autodesk API binaries are not packaged.
- [x] Confirm no local model, log, screenshot, or export file is packaged.
- [x] Confirm no credential or test endpoint is packaged.
- [x] Generate SBOM and SHA256 checksums.
- [ ] Add code-signing certificate before a signed release.

## 4. Redistribution Rule

Any future third-party binary must be reviewed before packaging. A component with unclear redistribution terms must not be added without a separate audit and updated notice file.
