# Architecture

App UI and standalone Bridge use the same `ToolDispatcher`, canonical contract catalog,
Revit adapter and transaction execution path. App's `ToolInvokerBridge` is a thin adapter
from Core DTOs to `BridgeRuntime`. Its ExternalEvent is constructed when AI Console is
opened in a valid Revit API context, not lazily on a pipe worker thread.

```text
App AgentLoop -> ToolInvokerBridge -> BridgeRuntime --------+
                                                          |
MCP / script -> named pipe -> BridgeServer ----------------+-> ToolDispatcher
                                                              | canonical toolset or drawing extension
                                                              v
                                                       ExternalEventInvoker
                                                              | cancellation + document identity check
                                                              v
                                                       Revit API + checked transaction
```

The 120 main contracts remain in toolset. The satellite import tool is disabled in this
runtime. Together with 26 specialized tools the shared live catalog has 145 entries.
Specialized drawing-plan operations retain their phased geometry implementation; named
standalone/App calls use canonical contracts. Read live schemas after upgrading.

Both pipe hosts maintain a bounded request journal. Request IDs prevent duplicate execution
within the same host lifetime; requests support status queries and cancellation. A timeout
never proves rollback. Document identity is checked immediately before execution on the
Revit thread. Each in-app Agent turn pins one document and persists completed tool results
on cancellation. A service-wide gate prevents external chat and UI session mutations from
concurrently replacing the active session.

Lengths follow field schemas: most geometry uses millimetres, level elevation uses metres,
and internal Revit length is decimal feet. dry-run is a per-tool capability, not a global
promise. Saving/exporting are separate requests outside transaction batches.

See [bridge-protocol.md](bridge-protocol.md) for wire compatibility, discovery, recovery and
validation. The release now includes the toolset assemblies and ACadSharp dependency;
Autodesk API binaries are supplied by the user's Revit installation and are never packaged.
