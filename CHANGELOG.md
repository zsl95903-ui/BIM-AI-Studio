# Changelog

## 1.0.0 - 2026-10-17 - BIM-AI Studio

- 项目更名为 **BIM-AI Studio**（BIM+AI 智能建筑协同创新套件）。
- 新增 `agent/revit-2027/`：Codex 技能包（调用脚本、桥接说明、建模配方、实时工具目录）。
- 新增 `docs/tool-authoring-guide.md`：工具从 schema 到读回的完整编写指南。
- 工具层裁剪为仅工具相关项目（Abstractions / Core / Revit），移除界面、账号、计费、更新模块。
- 新增 `docs/bridge-protocol.md`、`docs/modeling-recipes.md`、`docs/migration.md`。
- 新增 CI（引擎构建 + 冒烟测试 + JSON 校验）与 Release 工作流。
- 新增 CODE_OF_CONDUCT、SUPPORT、ROADMAP、Issue/PR 模板。
- 移除仓库内的测试安装包（改由 Releases 提供），修正发现文件路径说明。
- 加载项显示名改为 `BIM-AI Studio Bridge`；程序集与命名空间保持不变。

## 2026-10-09 - Revit 2027 Hybrid Test Package

- Added `releases/2027-test/RevitAI-Hybrid-Setup-5.0.0.exe`.
- Added rebuilt Revit 2027 120-tool source set under `rebuilt-120-tools/`.
- Added hybrid worktree under `worktree/`, including the Revit AI WebView2 UI, PDF/CAD extensions, packaging script, and embedded Codex bridge.
- Codex bridge now starts automatically with the Revit AI UI and writes `%LOCALAPPDATA%\RevitAi\codex-bridge.json`.
- This release is a test build, not a formal production release.

> 该测试包的安装文件已从源码树移出，改由 Releases 提供；`rebuilt-120-tools/` 源码保留。

## 2.1.0 - 2026-09-20

- Expanded the typed bridge catalog from 47 to 48 tools.
- Added `set_level_line_pattern` for Level-category datum presentation.
- Added headless add-in startup for isolated Codex contract hosting.
- Added the stdio MCP server and Codex registration/invocation helpers.
- Documented the Agent workflow for discovery, preflight, dry-run, mutation, readback, and user boundaries.
- Added explicit open-source and repository secret boundaries.
- Clarified that local signing material, external binaries, and user data are not distributed.


## 1.4.0 - 2026-09-19

- Released the ReVitAI Bridge core tool and API layer for the Revit-AI-Bridge Agent workflow.
- Kept 47 native Revit 2027 tools with typed JSON contracts.
- Added current-user Named Pipe discovery and structured request handling.
- Added transaction, dry-run, batch, plan, and readback support.
- Unified the source, assembly, add-in, and tool catalog under the `ReVitAI.Bridge` namespace.
- Added clean source and binary release packaging for Revit 2027.
