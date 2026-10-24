# Bridge correctness regression checks

- `dotnet run --project tests/Bridge.Protocol.Tests -c Release`: schema validation,
  duplicate requests, conflicting IDs, late requests after cancellation, target-document guard.
- `dotnet build src/RevitAi.McpServer -c Release` then `./tests/mcp-forwarding.ps1`:
  actual MCP child process and isolated named pipes; scalar/array/nested arguments,
  legacy wrapper, target binding, concurrent cancellation and unknown-outcome recovery.
- `dotnet run --project tests/Bridge.Catalog.Tests -c Release`: requires local Revit 2027
  API binaries, but does not open or modify a model. Checks actual merged catalog,
  aliases/drawing schemas and rejected non-committed transaction statuses.
- `./toolset/build.ps1`: existing engine tests; current local run: 352 passed, 0 failed.

The catalog test copies locally installed Autodesk DLLs only into ignored test output.
Never distribute that directory. Release packaging separately rejects Autodesk API DLLs.

Real Revit validation after reinstall/reload remains required for model writes,
view changes, multi-process use and native transaction failures.
