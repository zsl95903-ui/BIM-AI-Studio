# 工具层源码

本目录只保留工具相关项目：

| 项目 | 职责 |
| --- | --- |
| `RevitAi.Abstractions` | 工具契约、服务接口、数据模型 |
| `RevitAi.Core` | 工具注册表、调用调度、AI 工具 |
| `RevitAi.Revit` | 120 个工具类、服务实现、外部事件宿主 |

界面、账号、计费、额度、更新等模块不在本目录内。

## 说明

- 这是**参考源码集**，不是独立可构建的完整产品：构建需要本机 Revit API 与相应依赖库。
- 工具的实现范式见 [docs/tool-authoring-guide.md](../docs/tool-authoring-guide.md)。
- 新增工具时，只需按该指南添加工具类与服务方法，无需修改调度层。
