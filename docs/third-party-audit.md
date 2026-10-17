# Third-Party Audit

Audit date: 2026-09-19
Release: `2.1.0`
Scope: BIM-AI Studio source tree and release packages.

## 1. Repository References

| File | Reference | License | Distribution decision |
|---|---|---|---|
| `src/ReVitAI.Bridge/ReVitAI.Bridge.csproj` | `RevitAPI.dll` | Autodesk proprietary | Build from local installation; do not redistribute |
| `src/ReVitAI.Bridge/ReVitAI.Bridge.csproj` | `RevitAPIUI.dll` | Autodesk proprietary | Build from local installation; do not redistribute |
| Project C# files | `System.*` | .NET runtime license | Used as platform libraries |
| Project C# files | `Autodesk.Revit.*` | Autodesk proprietary API | Compile-time reference only |

## 2. Package Boundary

- The source repository contains project-authored source, documentation, and build scripts.
- `rebuilt-120-tools/` contains a tool-layer source set derived from a third-party Revit add-in; it is not covered by this repository's Apache-2.0 license.
- UI, account, billing, entitlement and update modules were removed from that source set.
- The release package contains one project-authored assembly: `ReVitAI.Bridge.dll`.
- Autodesk API binaries are not included.
- No NuGet `PackageReference` is required.
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
