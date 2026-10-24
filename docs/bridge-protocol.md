# Bridge protocol 3.0

App、独立 Bridge、MCP 共用 `src/Shared/BridgeProtocol.cs` 和 `ToolDispatcher`。
运行时目录目前为 145 项：119 项启用的主契约工具，加 26 项专业绘图/宿主扩展。
以实时 `tools.list` 为准。`toolset/contracts/tool-contracts.json` 是主工具参数来源，
`src/ReVitAI.Bridge/bridge-tool-contracts.json` 描述专业扩展参数。

## 请求

每个连接发送一行 UTF-8 JSON，读取一行结果。管道仅允许当前 Windows 用户。

```json
{"type":"invoke","protocolVersion":"3.0","hostId":"<pipeName>","requestId":"<unique-id>","tool":"create_level","expectedDocumentGuid":"<snapshot-documentGuid>","arguments":{"elevation":[3,6],"levelName":["L2","L3"]}}
```

1. 用 `agent.status` 或 `tools.list` 读取完整工具目录、Schema 与 dry-run 能力。
2. 调用 `get_document_snapshot` 获取文档身份。
3. 写请求必须携带 `expectedDocumentGuid`；宿主在实际执行的同一个 ExternalEvent 中检查身份。
4. 一次任务的多步操作复用同一文档身份。切换文档后必须重新计划，不能自动把写入重定向到其他文档。

支持的通用请求：`agent.status`、`tools.list`、`invoke`、`request.status`、`request.cancel`。
`agent.chat` 在 App 中使用配置的模型；独立 Bridge 仍只处理有限的确定性查询。
`agent.test` 是 App 特有能力。

## 版本与参数兼容

这是主版本升级。相同名称的工具统一采用主契约参数；`create_level.name` 保留为
`levelName` 的兼容别名。旧 Bridge 的其他未声明参数会被主契约校验拒绝，避免
几何参数被静默忽略。客户端必须重新读取 Schema，不能复用旧的 48 项目录缓存。

`create_level.elevation` 使用米；几何字段按各自 Schema 的单位。不要把所有
数字按毫米解释。批量 `batch` 子步骤同样执行 Schema 校验；保存、导出及嵌套
复合计划必须作为独立请求执行。

## 取消、重试与最终状态

```json
{"type":"request.cancel","requestId":"<original-id>"}
{"type":"request.status","requestId":"<original-id>"}
```

- 相同 requestId 和完全相同的请求载荷复用同一个执行任务与结果。
- 相同 ID 配不同载荷返回 `request_conflict`，不会再执行一次。
- 取消先到达时建立取消记录，迟到的请求不会进入执行。
- 排队请求在 Revit 线程执行前检查取消。执行中的单个同步 API 操作在安全边界结束；取消不会撤销已经提交的操作。
- 客户端断线/超时不等于回滚。MCP 返回 `outcome_unknown`，并保留 requestId 和 hostId；先查状态，再决定下一步。
- 日志每个宿主最多 10,000 个请求，不自动驱逐写入记录；满后拒绝新请求。宿主重启后日志丢失，不能把“查无记录”当作没执行过。
- 状态为 running/cancelling/completed/failed/cancelled。事务提交状态不为 Committed 时返回失败。

MCP 的 `notifications/cancelled` 会转发取消；也可调用 `revit_request_status`
和 `revit_cancel_request`。具名 tools/call 完整转发 arguments，只有
`revit_invoke_tool` 解包嵌套 arguments。

## 宿主发现

发现文件位于 `%LOCALAPPDATA%\RevitAi`；App 写入 `bridge.<PID>.json` 与
`bridge.json`，独立 Bridge 写入 `revitai-bridge.<PID>.json` 与主文件。
退出清理只删除属于自己的文件。

MCP 枚举实例并探活；失效文件不阻止尝试其他候选。多个 Revit 进程同时存在时，
必须设置 `REVIT_AI_PROCESS_ID` 或 `REVIT_AI_BRIDGE_PIPE`，不自动猜测目标。
可设置 `REVIT_AI_DOCUMENT_GUID` 固定目标文档；兼容包装器也接受 expectedDocumentGuid。

PowerShell 客户端支持：

```powershell
.\agent\revit-2027\scripts\revit-ai.ps1 tools
.\agent\revit-2027\scripts\revit-ai.ps1 invoke create_level '{"elevation":3,"levelName":"L2"}'
.\agent\revit-2027\scripts\revit-ai.ps1 request-status <original-request-id>
.\agent\revit-2027\scripts\revit-ai.ps1 cancel <original-request-id>
```

脚本在调用前读取快照并绑定同一个管道；可用 `-ExpectedDocumentGuid` 固定多步任务目标，
多进程时指定 `-PipeName` 或 `-DiscoveryPath`。

## 验证

- 无 Revit：`dotnet run --project tests/Bridge.Protocol.Tests -c Release`
- MCP 真实进程 + 假管道：先构建 MCP，再运行 `tests/mcp-forwarding.ps1`
- 有 Revit API 安装但无需打开模型：`dotnet run --project tests/Bridge.Catalog.Tests -c Release`
- 工具集：`toolset/build.ps1`，本次本机冒烟 352 项通过，0 项失败。

这些检查不替代重新加载插件后的真实模型写入、文档切换和事务回滚现场验证。
