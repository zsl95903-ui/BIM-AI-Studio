# BIM-AI Studio

面向 Autodesk Revit 2027 的本地优先 BIM 自动化工具层：类型化工具契约、
Revit 主线程调度、事务与回滚、命名管道桥接、MCP 转发和 Agent 调用脚本。

[![License: Apache-2.0](https://img.shields.io/badge/License-Apache_2.0-blue.svg)](LICENSE)
[![Revit](https://img.shields.io/badge/Revit-2027-186BFF)](docs/compatibility.md)
[![.NET](https://img.shields.io/badge/.NET-10-512BD4)](docs/compatibility.md)

> Not affiliated with or endorsed by Autodesk. Autodesk 和 Revit 为其各自
> 所有者的商标。

## 项目状态

- 主要目标平台为 Autodesk Revit 2027、Windows x64 和 .NET 10。
- 本项目由个人开发者维护，代码、兼容性和文档会持续改进。
- 工具层自动验证为 355 项通过、0 项失败；Bridge 与 MCP Server 可独立编译。
- 自动测试主要使用模拟 Revit 适配器，不等同于对所有 Revit 项目的完整现场
  验证。
- 复杂 MEP、道路、楼梯、地形、CAD 附件、批量族参数、明细表和多进程并发
  仍需在代表性项目中逐项验证。
- 写入模型前应备份项目，并先在测试模型中验证工具调用。

完整状态说明见 [docs/project-status.md](docs/project-status.md)。

## 组成

| 模块 | 目录 | 说明 |
| --- | --- | --- |
| Toolset | `toolset` | 120 项工具契约、121 个生成工具入口、服务层、注册表和测试 |
| Bridge | `src/ReVitAI.Bridge` | Revit 命名管道宿主、事务调度和 48 个桥接工具 |
| MCP Server | `src/RevitAi.McpServer` | 将实时 Revit 工具目录暴露为 MCP tools |
| Agent Skill | `agent/revit-2027` | Codex 调用脚本、建模配方与工具目录 |
| Application | `BIM-AI-Studio-App` | WebView2 UI、OpenAI-compatible 模型客户端和 Revit 加载入口 |

## 架构

```text
Codex / MCP client / script
            |
            v
  Agent Skill / Application UI
            |  named pipe (current user)
            v
   Bridge / MCP Server
            |  ToolCallInfo(tool, arguments)
            v
   AIToolInvocationHandler
            |  model mutation
            v
   ExternalEvent + Transaction
            |  start -> tool -> regenerate -> commit / rollback
            v
   Service Layer -> Autodesk Revit API
```

工具契约同时服务三个入口：

- WebView2 应用通过兼容层调用 `IAIToolRegistry`；
- Bridge 通过命名管道提供稳定的结构化工具目录；
- MCP Server 将实时目录转发给 Codex 或其他 MCP 客户端。

## 工具集

| 项目 | 数量 | 说明 |
| --- | ---: | --- |
| 工具契约 | 120 | `toolset/contracts/tool-contracts.json` |
| 生成工具入口 | 121 | 120 项契约工具和 `upsert_level` 扩展 |
| 自动验证 | 355 | 契约、参数解析、服务与工具行为 |
| Bridge 工具 | 48 | 实时命名管道桥接目录 |

工具数量以运行时返回为准。新增工具时先定义 JSON Schema，再实现 `IAITool`
并通过宿主设定事务和线程要求。

## 构建与测试

前置条件：

- Autodesk Revit 2027
- .NET 10 SDK

```powershell
# Toolset build and smoke tests
powershell -ExecutionPolicy Bypass -File .\toolset\build.ps1

# Bridge and MCP server
powershell -ExecutionPolicy Bypass -File .\build\build.ps1

# Release package
powershell -ExecutionPolicy Bypass -File .\build\package.ps1
```

Revit 安装在非默认目录时：

```powershell
.\build\build.ps1 -RevitApiDir "D:\Autodesk\Revit 2027"
```

## 桥接发现

不同宿主会发布各自的发现文件：

```text
%LOCALAPPDATA%\RevitAi\bridge.json
%LOCALAPPDATA%\ReVitAI\revitai-bridge.json
```

第一个由应用仓库的 WebView2 宿主发布，第二个由独立 Bridge 加载项发布。
MCP Server 会读取可用的发现文件并连接当前用户命名管道。

## 扩展工具

1. 在契约目录中定义名称、分类、描述和 JSON Schema。
2. 实现 `IAITool` 并通过 `ToolImplementationRegistry` 注册。
3. 根据行为设置 `RequiresTransaction`、`RequiresModification` 和
   `RequiresActiveDocument`。
4. 为只读、dry-run、事务、回滚和结果读回增加测试。

见 [docs/tool-authoring-guide.md](docs/tool-authoring-guide.md)。

## 兼容性

| 项 | 状态 |
| --- | --- |
| Autodesk Revit 2027 | 编译和自动测试通过；真实项目验证持续进行 |
| Windows x64 | 已验证 |
| .NET 10 | 已验证 |
| Revit 2025 / 2026 | 未验证 |
| 多 Revit 进程并发 | 未充分验证 |
| 复杂 MEP / CAD / 道路 / 楼梯 | 部分自动测试，现场验证有限 |

## 开源边界

本仓库包含项目自有工具层、Bridge、MCP Server、Agent 脚本、测试和文档。
仓库不分发 Autodesk Revit API 二进制、代码签名材料、用户模型、日志、会话或
API Key。

项目自有代码采用 Apache-2.0。第三方依赖继续适用各自许可证，详见
[THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。

## 文档

- [docs/project-status.md](docs/project-status.md)
- [docs/architecture.md](docs/architecture.md)
- [docs/bridge-protocol.md](docs/bridge-protocol.md)
- [docs/toolset.md](docs/toolset.md)
- [docs/tool-authoring-guide.md](docs/tool-authoring-guide.md)
- [docs/compatibility.md](docs/compatibility.md)
- [docs/open-source-scope.md](docs/open-source-scope.md)
