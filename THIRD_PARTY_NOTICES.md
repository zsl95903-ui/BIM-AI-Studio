# Third-Party Notices

## Tool-layer source set

`rebuilt-120-tools/` contains the tool layer of a third-party Revit add-in (tools, services and contracts), retained for reference and maintenance.

- It is a derivative work. The Apache-2.0 license of this repository does **not** automatically apply to it.
- Original installers, compiled assemblies and de-obfuscation intermediates are **not** distributed.
- UI, account, billing, entitlement and update modules are **not** included.
- Online service endpoints in the retained code are placeholders; nothing connects to a real backend.

Anyone redistributing this directory must independently verify the upstream license and applicable law.

## Build dependency

- Autodesk Revit API (`RevitAPI.dll`, `RevitAPIUI.dll`)
  - Proprietary Autodesk dependency.
  - Not distributed by this repository.
  - Provide it from a local Autodesk Revit installation.

## Published source

Project-authored code references only:

- `System.*` from the .NET runtime;
- `Autodesk.Revit.*` from the locally installed Revit API.

There are no NuGet `PackageReference` entries.

## Release packages

Release packages contain only the project-authored `ReVitAI.Bridge` assembly and installer scripts. Any future package that adds a third-party binary must record the component name, version, source URL, license and redistribution status here before release.

See [docs/third-party-audit.md](docs/third-party-audit.md).
