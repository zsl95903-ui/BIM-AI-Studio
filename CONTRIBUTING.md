# 参与贡献

## 开始之前

- 较大的架构改动先开 Issue 讨论。
- 第一次提交前读一遍 [docs/tool-authoring-guide.md](docs/tool-authoring-guide.md)。

## 代码约定

1. Revit API 调用只允许出现在服务层与工具执行层，不要散落在 UI 或 Agent 循环里。
2. 每个新工具必须有：`[AITool]` 元数据、JSON Schema、失败原因、读回校验。
3. 写操作必须由宿主统一开事务，工具内部不得 `new Transaction(...)`。
4. 单位在入口统一换算，禁止在业务代码里散写 `304.8`。
5. 工具清单以运行时 `tools list` 为准，不要在文档里写死数量。

## 禁止提交

- Autodesk Revit API 二进制（`RevitAPI.dll` / `RevitAPIUI.dll`）；
- 证书、PFX、DPAPI 备份、私钥；
- 用户模型、日志、会话、个人设置；
- 第三方安装包、原始程序集、处理中间产物；
- 本机绝对路径。

## 提交前

```powershell
git status
git diff --cached
dotnet build src/ReVitAI.Bridge/ReVitAI.Bridge.csproj -c Release
```

CI 会额外做：JSON 校验、绝对路径扫描、引擎冒烟测试。

## PR

按 `.github/PULL_REQUEST_TEMPLATE.md` 填写，说明**在哪个 Revit 版本、哪个模型上验证、读回了什么**。
