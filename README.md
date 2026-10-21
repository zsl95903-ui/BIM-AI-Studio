# BIM-AI Studio

**BIM+AI 智能建筑协同创新套件**

[![License: Apache-2.0](https://img.shields.io/badge/License-Apache_2.0-blue.svg)](LICENSE)
[![Revit](https://img.shields.io/badge/Revit-2027-186BFF)](docs/compatibility.md)
[![.NET](https://img.shields.io/badge/.NET-10-512BD4)](docs/compatibility.md)

面向 Autodesk Revit 2027 的本地优先 BIM 自动化套件：类型化工具层、命名管道桥接、MCP 转发、Agent 技能包，以及一套可扩展的工具编写指南。

> Not affiliated with or endorsed by Autodesk. Autodesk、Revit 为其各自所有者的商标。

---

## 它解决什么问题

让 AI Agent 安全地改 Revit 模型，难点从来不是"生成代码"，而是：

- Revit API 只能在 Revit 主线程调用；
- Agent 直接拼 C# 难以审计、难以复现；
- 写入需要事务、回滚和读回，否则你不知道到底改成了什么；
- 每台机器的工具集不同，硬编码工具清单必然错位。

BIM-AI Studio 把这些收敛成一层稳定的结构化工具契约：Agent 只发 JSON，宿主统一处理线程、事务、回滚与读回。

## 组成

| 模块 | 目录 | 说明 |
| --- | --- | --- |
| Bridge | `src/ReVitAI.Bridge` | 命名管道宿主 + 48 个稳定工具 + 事务调度 |
| MCP Server | `src/RevitAi.McpServer` | 把实时的桥接工具目录暴露为 MCP tools |
| Engine | `clean-room-tools` | 自研工具引擎：契约、注册表、宿主、服务层、冒烟测试 |
| Toolset | `clean-room-tools` | 120 个工具的 Apache-2.0 源码；工具类由契约生成 |
| Workbench | `worktree` | 混合工作台：WebView2 UI、PDF Worker、Codex 桥、打包脚本 |
| Agent Skill | `agent/revit-2027` | Codex 技能包：调用脚本、建模配方、实时目录 |

## 架构

```text
Codex / MCP client / script
            │
            ▼
  Agent Skill  (agent/revit-2027)
            │  命名管道（当前用户）
            ▼
   Bridge / MCP Server
            │  ToolCallInfo(tool, arguments)
            ▼
   AIToolInvocationHandler
            │  需要改模型 → 回主线程
            ▼
   ExternalEvent + Transaction
            │  Start → 工具 → Regenerate → Commit / Rollback
            ▼
   Service Layer  →  Autodesk Revit API
```

细节见 [架构说明](docs/architecture.md) 与 [桥接协议](docs/bridge-protocol.md)。

## 快速开始

```powershell
# 1. 构建并测试 120 工具集
powershell -ExecutionPolicy Bypass -File .\clean-room-tools\build.ps1

# 2. 构建 Bridge 与 MCP
powershell -ExecutionPolicy Bypass -File .\build\build.ps1

# 3. 打包 Bridge
powershell -ExecutionPolicy Bypass -File .\build\package.ps1

# 4. 安装（关闭 Revit 后执行，产物在 artifacts\）
powershell -ExecutionPolicy Bypass -File .\installer\install.ps1
```

Revit 装在非默认目录时：

```powershell
.\build\build.ps1 -RevitApiDir "D:\Autodesk\Revit 2027"
```

启动 Revit 并打开工程后，桥接会写发现文件：

```text
%LOCALAPPDATA%\RevitAi\bridge.json
%LOCALAPPDATA%\RevitAi\codex-bridge.json   # Codex 高精度桥
```

> 仓库历史版本里写的是 `%LOCALAPPDATA%\ReVitAI\revitai-bridge.json`，那是 2.1.0 之前的路径，已不再使用。迁移见 [docs/migration.md](docs/migration.md)。

## 第一次调用

```powershell
.\agent\revit-2027\scripts\revit-ai.ps1 ping
.\agent\revit-2027\scripts\revit-ai.ps1 tools
.\agent\revit-2027\scripts\revit-ai.ps1 schema create_straight_wall
.\agent\revit-2027\scripts\revit-ai.ps1 invoke create_straight_wall '{"start_x":0,"start_y":0,"end_x":6000,"end_y":0,"level_id":311,"height":3000,"wall_type_id":398}'
```

Agent 工作流（读上下文 → 解析 ID → dry-run → 写入 → 读回）见 [docs/agent-workflow.zh-CN.md](docs/agent-workflow.zh-CN.md)。

## 工具层

| 层 | 数量 | 说明 |
| --- | ---: | --- |
| Bridge 稳定工具 | 48 | [docs/tool-catalog.json](docs/tool-catalog.json)，随 Bridge 发布 |
| 完整工具集 | 120 | `clean-room-tools/`，覆盖建筑、结构、MEP、注释、视图、导出 |
| 运行时目录 | 119 | 由已安装插件实时返回，以 `tools list` 为准 |

工具数量以**运行时返回**为准，文档里的数字只用于说明规模。新增工具请看 [工具编写指南](docs/tool-authoring-guide.md)。

## 扩展工具名单

工具层是可扩展的：新增一个工具只需要三步。

1. **定契约**：给工具起一个 `snake_case` 名字，写清参数 JSON Schema 与单位。
2. **写实现**：实现 `IAITool`，在 `ExecuteAsync` 里读参数、调服务、返回 `AIToolResult`。
3. **接宿主**：标上 `RequiresTransaction / RequiresModification`，由宿主统一开事务、提交或回滚。

完整写法（含墙/板/详图线/文字/尺寸五类模板、批量参数、读回校验、检查清单）见 [docs/tool-authoring-guide.md](docs/tool-authoring-guide.md)。

`clean-room-tools/` 是唯一工具层源码。120 个工具类在构建时由 `contracts/tool-contracts.json` 生成，业务实现位于 `ExtToolLib.Implementations`、`RevitAi.Engine.Revit` 和对应服务层。界面、账号、计费、更新等业务模块不在开源范围内。

## 兼容性

| 项 | 状态 |
| --- | --- |
| Autodesk Revit 2027.3 | ✅ 已验证 |
| Windows 11 x64 | ✅ 已验证 |
| .NET 10 | ✅ |
| Revit 2025 / 2026 | ⚠️ 未验证 |
| 多 Revit 进程并发 | ⚠️ 未验证 |

## 开源边界

本仓库包含：桥接、调度、MCP 转发、自研引擎、工具集源码、技能包、构建脚本与文档。

**不包含**：

- Autodesk Revit API 二进制（构建时引用本机安装）；
- 代码签名证书、PFX、密钥；
- 用户模型、日志、会话与个人设置；
- 第三方安装包、原始程序集与处理中间产物。

`clean-room-tools/` 为本项目维护的干净重建实现，代码采用 Apache-2.0。唯一新增第三方运行时依赖为 MIT 许可的 `ACadSharp 3.4.9`，详见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。

## 文档

| 文档 | 内容 |
| --- | --- |
| [technical-overview.md](docs/technical-overview.md) | 技术说明：架构、模型调用工具链路、可扩展性 |
| [development-guide.md](docs/development-guide.md) | 开发说明：问题定义、能力域、扩展机制、底层契约 |
| [architecture.md](docs/architecture.md) | 四层架构与调用链 |
| [clean-room-rebuild.md](docs/clean-room-rebuild.md) | 120 工具干净重建、测试与发布边界 |
| [bridge-protocol.md](docs/bridge-protocol.md) | 管道、请求/响应、错误码、串行化 |
| [tool-authoring-guide.md](docs/tool-authoring-guide.md) | 新工具从 schema 到读回的完整写法 |
| [modeling-recipes.md](docs/modeling-recipes.md) | 标高 → 轴网 → 墙 → 房间 → 注释 |
| [tool-catalog.json](docs/tool-catalog.json) | Bridge 的 48 个稳定工具 |
| [migration.md](docs/migration.md) | 从 Revit-AI-Bridge 迁移 |
| [compatibility.md](docs/compatibility.md) | 兼容矩阵 |

## 参与贡献

见 [CONTRIBUTING.md](CONTRIBUTING.md)。提交前请确认不含证书、密钥、模型、日志与第三方二进制。

## 许可

项目自有代码、120 工具集、契约、测试和构建脚本采用 [Apache-2.0](LICENSE)。第三方组件见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。
