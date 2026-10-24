# 从 Revit-AI-Bridge 迁移

## 名称变化

| 项 | 旧 | 新 |
| --- | --- | --- |
| 项目名 | ReVitAI Bridge / Revit-AI-Bridge | **BIM-AI Studio** |
| 仓库 | `zsl95903-ui/Revit-AI-Bridge` | `zsl95903-ui/BIM-AI-Studio` |
| Revit 加载项显示名 | `ReVitAI Bridge` | `BIM-AI Studio Bridge` |
| 程序集 | `ReVitAI.Bridge.dll` | 保持不变 |
| 命名空间 | `ReVitAI.Bridge` | 保持不变 |

程序集与命名空间暂不改名，保证现有构建脚本、`.addin` 清单与已安装版本可以直接升级。命名空间迁移放到下一个大版本，届时另发迁移说明。

## 路径变化

| 用途 | 旧路径（≤2.1.0 文档） | 实际/新路径 |
| --- | --- | --- |
| 桥接发现文件 | `%LOCALAPPDATA%\ReVitAI\revitai-bridge.json` | `%LOCALAPPDATA%\RevitAi\bridge.json` |
| 安装目录 | `%LOCALAPPDATA%\Programs\RevitAi` | 不变 |
| 运行日志 | — | `%LOCALAPPDATA%\RevitAi\logs\` |

如果脚本写死了单个发现文件路径，请改为依次检查
`RevitAi\bridge.json` 和 `ReVitAI\revitai-bridge.json`。

## 升级步骤

1. 关闭 Revit。
2. 在「控制面板 → 应用」卸载已有安装。
3. 删除残留的 `%LOCALAPPDATA%\ReVitAI\`（如果存在）。
4. 安装 BIM-AI Studio。
5. 启动 Revit，确认加载项列表里出现 **BIM-AI Studio Bridge**。
6. 运行 `agent/revit-2027/scripts/revit-smoke.ps1` 验证连通。

## 版本号说明

- 产品版本：**1.0.0**（新项目线的起点）。
- 子组件 `ReVitAI.Bridge`：**2.1.0**（沿用组件自身的版本号）。
- 旧的 `5.0.0` 测试包不再作为源码仓库存放物，改由 Releases 提供。

## 常见问题

**加载项里还是旧名字？**
两个 `.addin` 同时存在时，Revit 会都加载。检查 `%APPDATA%\Autodesk\Revit\Addins\2027\`，删除旧的 `ReVitAI.Bridge.addin`。

**桥接文件不存在？**
确保 Revit 已打开一个工程，并且加载项已加载。桥接在加载项启动时写发现文件，关闭时删除。

**工具数量对不上？**
协议 3.0 中 App 与独立 Bridge 共用 145 项运行时目录。120 是主契约总数，48 是历史独立 Bridge 数量。升级后重新读取 Schema；写请求必须绑定 expectedDocumentGuid。

### 视图查询实机兼容修复

get_all_views 默认只返回契约覆盖的普通用户视图，排除模板、内部浏览器和明细表；中文 viewType 参数映射到 Revit 原生枚举名称。输出保留原生 viewType，并补充中文 type；get_active_view 补充旧版视图标识字段别名。无 Revit 回归增加中文参数与排除范围检查；Revit 2027 只读实测已确认视图集合与旧插件一致。
