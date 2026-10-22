# Revit 2027 Bridge

## Prerequisites

- Autodesk Revit 2027 is running.
- An active document is open.
- The `Revit AI` add-in is loaded and the ribbon contains `Revit AI > AI Console`.
- A live discovery file exists at `%LOCALAPPDATA%\RevitAi\bridge.json` or
  `%LOCALAPPDATA%\ReVitAI\revitai-bridge.json`.

The bridge uses a Windows Named Pipe with `PipeOptions.CurrentUserOnly`; it does not expose an HTTP port.

## Client Commands

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\revit-ai.ps1 ping
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\revit-ai.ps1 tools
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\revit-ai.ps1 schema <tool-name>
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\revit-ai.ps1 invoke <tool-name> '<json-arguments>'
```

`ping` and `tools` both return the live tool catalog. `schema` returns one tool definition including `inputSchema`. `invoke` returns the complete result envelope and exits with code `2` when the bridge reports failure.

## Built-In Natural-Language Agent

Inspect the plugin Agent:

```powershell
.\scripts\revit-ai.ps1 agent-status
```

Delegate a natural-language task:

```powershell
.\scripts\revit-ai.ps1 chat "先读取现有轴网和墙体，再按这些尺寸创建缺失部分；完成后读回验证，不要保存。"
```

The plugin Agent uses the model configured in its API settings. It can call the same named-pipe tools and may use `execute_code` when a standard tool is broken. Natural-language instructions should state the working level, exact dimensions, duplicate-prevention rules, fallback behavior, readback evidence, and the no-save/close/sync boundary.

## Transport

The client reads `%LOCALAPPDATA%\RevitAi\bridge.json`, checks that the recorded process is still alive, connects to the named pipe, writes one UTF-8 JSON line, and reads one JSON response line.

Supported request types are:

- `tools.list`
- `agent.status`
- `invoke`
- `agent.test`
- `agent.chat`

For model control, use `tools.list` and `invoke` only.

## Errors

- `revit_context_not_ready`: Open `Revit AI > AI Console` once and retry.
- `revit_tool_busy`: Another call is using the bridge external-event slot. Wait and retry.
- 工具执行失败（错误码前缀随版本变化，以 live 返回为准）：Read the returned payload and error message; do not retry a mutation blindly.
- `tools.list` returns zero tools: the adapter or ExternalEvent has not initialized. Open the AI Console and retry.
- Stale discovery file: The recorded Revit process no longer exists. Resolve view and level IDs from the current document, or restart Revit and retry.
- Named-pipe access denied: Run the client in the same Windows user context as Revit.

## Precision Modeling Rules

1. Query before writing. Resolve IDs from the live document rather than assuming defaults.
2. Read `inputSchema` for every tool. Do not infer argument names or units from a similar tool.
3. Prefer millimetres for public coordinates when the schema does not state otherwise. Revit internal units are decimal feet.
4. Preview all supported mutations with `dry_run`.
5. Keep a write request scoped to one logical operation. Do not mix unrelated edits in one call.
6. Read back element identity, location, type, level, and relevant parameters.
7. For a multi-step model, create and verify one dependency layer at a time: levels, grids, walls/floors, openings/families, then annotations and views.
8. Never use the same Revit file concurrently from multiple writers. The bridge serializes tool invocation and rejects overlapping requests.

## Tool Discovery

Do not hard-code a tool list into the model. The installed catalog can vary by workstation and Revit add-in version. Always use the live catalog. Query categories and element types from the active document when a tool requires IDs.
