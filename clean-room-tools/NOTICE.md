# Notice

The project-authored source in this directory is licensed under Apache-2.0.

This module does not include or redistribute:

- external DLLs or assemblies;
- reviewed external source code;
- Autodesk Revit API binaries;
- licenses, tokens, certificates, or user data.

`contracts/tool-contracts.json` contains interoperability metadata only
(tool names, categories, descriptions, transaction flags, and parameter
schemas). It is included so the self-developed implementations can be
generated, built, and tested without shipping any external implementation.

The Revit add-in projects reference `RevitAPI.dll` and `RevitAPIUI.dll` from a
local Revit installation. Those files are not part of this repository.

The clean-room Revit engine references the MIT-licensed NuGet package
`ACadSharp` 3.4.9 for DWG/DXF parsing. The package is restored by NuGet and is
not committed to this repository.
