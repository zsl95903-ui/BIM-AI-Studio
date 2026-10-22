# 2027 测试版本说明

本仓库中与 Revit 2027 混合测试包相关的内容均为测试版本，非正式发行版，仅供参考、研究与本地验证使用。

## 包含内容

- `releases/2027-test/RevitAI-Hybrid-Setup-5.0.0.exe`
- `toolset/`
- 应用层源码由 [`BIM-AI-Studio-App`](https://github.com/zsl95903-ui/BIM-AI-Studio-App)
  的 `host/hybrid-ui/` 维护

## 风险提示

- 请在测试模型或隔离环境中验证。
- 不要直接用于生产项目、正式交付或未经备份的模型。
- 安装前请关闭 Revit。
- 本仓库不包含本机用户配置、日志、会话记录、缓存、API Key 或机器指纹数据。

## 命名管道桥接

应用加载项启动时会启动命名管道宿主，并写入：

`%LOCALAPPDATA%\RevitAi\bridge.json`

外部客户端可通过该 discovery 文件连接并调用插件注册的 Revit 工具。
