# Third-Party Notices

## Tool-layer source set

`clean-room-tools/` is the project-maintained clean-room tool layer for the 120-tool catalog.

- The source, contracts, tests, generators and build scripts are licensed under Apache-2.0.
- Tool classes are generated from the checked-in JSON contract catalog at build time.
- Legacy decompiled source, original assemblies and de-obfuscation intermediates are not distributed.
- UI, account, billing, entitlement and update modules are not included.

## NuGet dependency

- `ACadSharp` 3.4.9
  - MIT license
  - Used for DWG/DXF parsing
  - Restored through NuGet; not committed to this repository

## Build dependency

- Autodesk Revit API (`RevitAPI.dll`, `RevitAPIUI.dll`)
  - Proprietary Autodesk dependency.
  - Not distributed by this repository.
  - Provide it from a local Autodesk Revit installation.

## Published source

Project-authored code references only:

- `System.*` from the .NET runtime;
- `Autodesk.Revit.*` from the locally installed Revit API.

The `ReVitAI.Bridge` projects have no NuGet `PackageReference` entries.
`clean-room-tools` uses the MIT-licensed `ACadSharp` 3.4.9 package.

## Release packages

Release packages may contain the project-authored `ReVitAI.Bridge` assembly and the Apache-2.0 clean-room tool layer. Any future package that adds a third-party binary must record the component name, version, source URL, license and redistribution status here before release.

See [docs/third-party-audit.md](docs/third-party-audit.md).
