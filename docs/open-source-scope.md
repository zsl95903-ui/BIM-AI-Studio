# 开源范围

## 包含

| 目录 | 内容 |
| --- | --- |
| `src/ReVitAI.Bridge` | 类型化桥接、调度器、Schema、事务与 48 个稳定工具 |
| `src/RevitAi.McpServer` | stdio MCP 转发器 |
| `clean-room-tools` | 自研引擎：契约、注册表、宿主、服务层、冒烟测试 |
| `clean-room-tools` | **120 工具层**：契约、生成器、工具实现、服务、测试和打包脚本 |
| `agent/revit-2027` | Codex 技能包 |
| `tools` / `build` / `installer` | 客户端脚本、构建、打包、安装 |
| `docs` | 架构、协议、工具编写指南、兼容性、安全 |

## 不包含

- 代码签名密钥、PFX/P12、DPAPI 备份、证书导出；
- API Key、模型令牌、对话记录、会话存储、用户设置；
- 本机绝对路径与本地发现文件；
- Autodesk Revit API 二进制（`RevitAPI.dll` / `RevitAPIUI.dll`）；
- 第三方安装包、原始程序集、处理中间产物；
- 界面框架、账号体系、计费与额度、自动更新等业务模块；
- 本地构建产物、模型文件与用户数据。

## 边界说明

`clean-room-tools` 保存完整的 120 工具实现：`contracts/tool-contracts.json` 是契约事实，工具类由生成器产出，具体逻辑位于 `ExtToolLib.Implementations`、`RevitAi.Engine.Revit` 与服务层。界面、登录、购买、设备限制、更新等目录不在开源范围内。

该目录采用 Apache-2.0。仓库不包含旧版反编译源码、原始程序集或安装包。

## 提交规则

PR 不得包含签名材料、密钥、第三方二进制、第三方安装包、界面与计费模块、本机用户路径或模型数据。提交前运行 `git status` 并检查 `git diff --cached`。
