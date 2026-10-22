# 工具编写指南（Tool Authoring Guide）

> 依据：本仓库的 `toolset/` 以及 `agent/revit-2027/` 中的桥接契约。
> 本文的模板来自 Apache-2.0 工具层，可直接作为新工具的实现基线。

---

## 0. 一句话结论

一个工具就是四件事：

```
一个带 [AITool] 的类  +  一段 JSON Schema  +  一次服务调用  +  一个由宿主统一管理的事务边界
```

工具本身**不创建事务、不碰 UI 线程、不解析自然语言**。它只负责把「已经验证过的参数」翻译成 Revit 数据库操作。

---

## 1. 工具集是怎么分层的

```
┌──────────────────────────────────────────────────────────────┐
│ 客户端（Codex 技能 / 内置 Agent / 任何调用方）                 │
│   invoke <tool> {args}                                       │
└───────────────────────────┬──────────────────────────────────┘
                            │ 命名管道（CurrentUserOnly）
┌───────────────────────────▼──────────────────────────────────┐
│ 插件壳：IExternalApplication                                  │
│   · 写 discovery 文件（pipeName / processId）                 │
│   · 起 PipeServer，串行处理请求                                │
│   · 建 Ribbon 按钮、加载 UI                                    │
└───────────────────────────┬──────────────────────────────────┘
                            │ ToolCallInfo(toolName, parameters)
┌───────────────────────────▼──────────────────────────────────┐
│ 调度层：AIToolInvocationHandler                               │
│   · 查注册表取工具                                             │
│   · 注入 DataCache / UnitService                              │
│   · 判断是否需要回 Revit 主线程                                │
└───────────────────────────┬──────────────────────────────────┘
                            │ Task + TaskCompletionSource
┌───────────────────────────▼──────────────────────────────────┐
│ 宿主层：ExternalEvent + IExternalEventHandler + Transaction    │
│   · 回主线程；Start/Commit/Rollback；Regenerate；压警告         │
└───────────────────────────┬──────────────────────────────────┘
                            │ AIToolContext（Document / Adapter / Transaction）
┌───────────────────────────▼──────────────────────────────────┐
│ 工具层：IAITool + [AITool]                                     │
│   · 读参数、做校验、调服务、组装结果                            │
└───────────────────────────┬──────────────────────────────────┘
                            │ IRevitAdapter.XxxService
┌───────────────────────────▼──────────────────────────────────┐
│ 服务层：Wall/Floor/Annotation/View/Parameter …                 │
│   · 唯一直接使用 Autodesk.Revit.DB API 的地方                  │
└──────────────────────────────────────────────────────────────┘
```

当前实现中的对应位置：

| 结论 | 代码位置 |
| --- | --- |
| 工具用特性声明元数据，用 JSON 字符串声明参数 | `toolset/src/RevitAi.Engine.Abstractions/IAITool.cs`、`AIToolAttribute.cs` |
| 注册表用反射扫描 + 构造函数按类型注入服务 | `toolset/src/RevitAi.Engine.Core/AIToolRegistry.cs` |
| 事务由宿主统一开启，工具成功才 Commit | `toolset/src/RevitAi.Engine.Revit/RevitToolHost.cs` |
| 绘图最终落到 `Wall.Create` / `Floor.Create` / `NewDetailCurve` / `TextNote.Create` / `NewDimension` | `toolset/src/RevitAi.Engine.Revit/Services/` |
| 对外暴露命名管道 + discovery 文件 | `src/ReVitAI.Bridge/BridgeServer.cs`、`src/RevitAi.McpServer/Program.cs` |

---

## 2. 工具契约

### 2.1 接口：五个成员

```csharp
public interface IAITool
{
    string Name { get; }              // 全局唯一，snake_case，如 create_straight_wall
    string Description { get; }       // 给模型看的说明，必须写清单位和前置条件
    string Category { get; }          // 中文分类，如 元素创建 / 注释与标记
    string ParametersSchema { get; }  // JSON Schema 字符串
    Task<AIToolResult> ExecuteAsync(AIToolContext context, CancellationToken ct = default);
}
```

### 2.2 特性：四个开关

```csharp
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class AIToolAttribute : Attribute
{
    public string Name { get; }                       // 注册键
    public string Category { get; set; } = "通用";
    public string Description { get; set; } = "";
    public bool RequiresTransaction { get; set; }     // 需要事务 → 回主线程执行
    public bool RequiresModification { get; set; }    // 会改模型
    public bool RequiresActiveDocument { get; set; } = true;
}
```

判断规则很简单：

- 只读查询：`RequiresTransaction=false, RequiresModification=false` → 可以就地执行，不必回主线程。
- 创建/修改/删除：两个都设为 `true` → 必须回主线程，并且包在一个事务里。

漏设 `RequiresTransaction` 的后果不是报错，而是**在非主线程直接调 Revit API**，抛「API 只能在 Revit 线程调用」或被静默吞掉。这是最常见的翻车点。

### 2.3 Schema：只写真正需要的信息

`ParametersSchema` 是一个字符串形式的 JSON Schema。实际下发给模型时会被包进 `{"type":"function","function":{...}}`，所以只需要 `type/properties/required` 这三层。

```jsonc
{
  "type": "object",
  "properties": {
    "level_id": {
      "type": "integer",
      "description": "标高 ID。先用 get_all_levels 读取，不要猜"
    },
    "wall_type_id": {
      "type": "integer",
      "description": "墙类型 ID，必选。可用 get_family_types 获取"
    },
    "start_x": { "type": "number", "description": "起点 X（毫米）" }
  },
  "required": ["start_x", "start_y", "end_x", "end_y", "level_id", "wall_type_id"]
}
```

写 Schema 的三条硬规矩：

1. **单位写进 description**。同一套工具里毫米、米、英尺三种口径都存在，不写清楚模型一定会错。
2. **ID 类参数必须写明获取途径**（`get_all_levels` / `get_family_types`），并在运行时校验类型。
3. **批量参数和单参数并存时，明确优先级**。归档里的统一做法是：存在 `walls` / `floors` / `columns` 数组时忽略单条参数。

### 2.4 命名

| 项 | 规则 | 例子 |
| --- | --- | --- |
| 工具名 | 动词_名词，snake_case | `create_straight_wall`、`get_all_levels`、`set_parameter_values` |
| 分类 | 中文短词 | `元素创建`、`注释与标记`、`视图与显示` |
| 参数名 | snake_case；同时兼容 camelCase 读取 | `level_id` / `levelId` |

兼容读取很重要：工具层里 120 个工具同时接受两套写法，避免模型换个写法就失败。

---

## 3. 注册与注入

### 3.1 发现规则

启动后第一次调用时扫描程序集，满足全部条件才注册：

```
是 class  ∧  非 abstract  ∧  实现 IAITool  ∧  带 [AITool]
```

实现 `IAITool` 但没打特性 → 跳过并记日志。同名重复注册 → 后者覆盖前者。

### 3.2 注入方式：按类型 switch，不是 DI 容器

```csharp
private object? ResolveService(Type t)
{
    if (t == typeof(IRevitAdapter))     return _adapter;
    if (t == typeof(IAIToolDataCache))  return _dataCache;
    if (t == typeof(IUnitConverter))    return _unitConverter;

    var a = _adapter;
    if (a is null) return null;

    if (t == typeof(IElementService))      return a.ElementService;
    if (t == typeof(IAnnotationService))   return a.AnnotationService;
    if (t == typeof(IModificationService)) return a.ModificationService;
    // …按适配器属性逐个补
    return null;
}
```

构造工具时取**参数最少的可解析构造函数**；解析不出就跳过这个工具并记日志，不要让整个插件启动失败。

### 3.3 下发给模型的 tools 数组

```json
[
  {
    "type": "function",
    "function": {
      "name": "create_straight_wall",
      "description": "在指定标高处创建直墙。所有距离单位为毫米",
      "parameters": { "type": "object", "properties": {}, "required": [] }
    }
  }
]
```

工具清单**不要写进 system prompt**，每次调用前从注册表现生成，这样设备间工具数量不同也不会错位。

---

## 4. 线程与事务：这是整套架构的命门

### 4.1 为什么必须走 ExternalEvent

Revit API 只能在 Revit 自己的线程上调用。`ConfigureAwait(false)` 之后恢复的线程是线程池线程，直接调 `Wall.Create` 必炸。正确做法：

```
调用线程                      Revit 主线程
   │                              │
   ├─ TaskCompletionSource ───────┤
   ├─ ExternalEvent.Raise() ─────▶│ Execute(UIApplication)
   │                              ├─ Transaction.Start()
   │                              ├─ action(context)   ← 工具在这里跑
   │                              ├─ Regenerate + Commit / Rollback
   │◀──── TaskSource.SetResult ───┤
   ▼
 await 拿到 AIToolResult
```

### 4.2 事务边界的四条纪律

1. **只在宿主开事务**。服务层和工具层里写 `new Transaction(...)` 是最常见的架构破坏——一旦工具被批量调用，会出现嵌套事务或跨工具半提交。
2. **成功才 Commit**，失败或异常一律 `RollBack()`。
3. **Commit 前 `Document.Regenerate()`**，否则读回几何可能拿到旧值。
4. **回滚失败不能覆盖原始异常**。

### 4.3 推荐的宿主实现

```csharp
public void Execute(UIApplication app)
{
    while (_queue.TryDequeue(out var req))
    {
        var doc = app.ActiveUIDocument?.Document ?? req.Context.Document as Document;
        if (doc is null) { req.Completion.TrySetResult(AIToolResult.Fail("没有活动文档。")); continue; }

        req.Context.Document = doc;
        Transaction? tx = null;

        try
        {
            if (req.RequiresTransaction)
            {
                tx = new Transaction(doc, "AI Tool Execution");
                var opt = tx.GetFailureHandlingOptions();
                opt.SetFailuresPreprocessor(new WarningSuppressor());
                tx.SetFailureHandlingOptions(opt);
                tx.Start();
                req.Context.Transaction = tx;
            }

            var result = req.Action(req.Context).GetAwaiter().GetResult();

            if (tx is not null)
            {
                if (result.Success)
                {
                    doc.Regenerate();
                    var st = tx.Commit();
                    if (st != TransactionStatus.Committed)
                        result = AIToolResult.Fail($"事务未能提交（{st}）：{result.Message}");
                }
                else tx.RollBack();
            }

            req.Completion.TrySetResult(result);
        }
        catch (Exception ex)
        {
            TryRollback(tx);
            req.Completion.TrySetResult(AIToolResult.Fail($"执行工具时发生异常：{ex.GetBaseException().Message}"));
        }
        finally { req.Context.Transaction = null; }
    }
}
```

### 4.4 为什么要压警告

批量建模会大量触发「图元已连接但未相交」「实例原点不在主体面上」这类警告。每条都弹窗会把 Agent 循环卡死。归档里的做法是在事务的 `FailuresPreprocessor` 里**只删警告、不动错误**：

```csharp
public FailureProcessingResult PreprocessFailures(FailuresAccessor acc)
{
    foreach (var f in acc.GetFailureMessages())
        if (f.GetSeverity() == FailureSeverity.Warning) acc.DeleteWarning(f);
    return FailureProcessingResult.Continue;   // 错误照常抛出
}
```

---

## 5. 绘制类工具怎么实现

### 5.1 先记住单位口径

Revit 内部长度单位是**英尺**。对外暴露的口径并不统一，必须逐工具确认：

| 场景 | 对外单位 | 转换 | 依据 |
| --- | --- | --- | --- |
| 墙/柱/梁/门窗坐标、墙高 | 毫米 | `mm / 304.8` | `CreateStraightWallTool` |
| `create_level.elevation` | 米 | `m * 1000 / 304.8` | 工具目录 schema |
| `get_all_levels.elevation` | 米 | `ft * 304.8 / 1000` | 技能参考文档 |
| `get_element_location` | 毫米 | `ft * 304.8` | 技能参考文档 |
| 详图线 / 文字位置 | 毫米 | 显式 `ToFeet()` | `DrawingCommands.cs` |
| `create_grid` | 项目长度单位 | 直接传入 | 工具目录 schema（唯一没写毫米的） |

统一封装，不要在各工具里散写魔法数：

```csharp
public static class Units
{
    public const double MmPerFoot = 304.8;
    public static double Mm(double mm) => mm / MmPerFoot;
    public static double Ft2Mm(double ft) => ft * MmPerFoot;
    public static double M(double m)   => m * 1000.0 / MmPerFoot;
    public static double Ft2M(double ft) => ft * MmPerFoot / 1000.0;
}
```

### 5.2 永远先解析 ID，不要用默认值兜底

```csharp
// 反例：拿第一个墙类型顶上，模型里就多了一堵用户没要求的墙
var wallType = new FilteredElementCollector(doc).OfClass(typeof(WallType)).FirstElement();

// 正例：按 ID 精确取，取不到就失败并说明怎么取
var type = doc.GetElement(new ElementId(wallTypeId)) as WallType
           ?? throw new InvalidOperationException(
              $"找不到墙类型 {wallTypeId}，请先调用 get_family_types 获取有效 ID。");
```

同一规则适用于：`Level`、`WallType`、`FloorType`、`FamilySymbol`、`TextNoteType`、`DimensionType`、`ViewFamilyType`。

归档里 `ElementService.CreateStraightWall` 带了一个「取不到就用第一个墙类型」的兜底，那是给手工单测用的。**新工具不要复制这个兜底**，它会掩盖参数错误。

### 5.3 第一类：模型元素（墙 / 板 / 柱 / 梁 / 门窗）

#### 墙

```csharp
[AITool("create_straight_wall", Category = "元素创建",
        Description = "在指定标高处创建直墙。所有距离单位为毫米",
        RequiresTransaction = true, RequiresModification = true)]
public sealed class CreateStraightWallTool : IAITool
{
    private readonly IElementService _elements;
    public CreateStraightWallTool(IElementService elements) => _elements = elements;

    public string Name => "create_straight_wall";
    public string Category => "元素创建";
    public string Description => "在指定标高处创建直墙。所有距离单位为毫米";
    public string ParametersSchema => /* 见 2.3 */;

    public Task<AIToolResult> ExecuteAsync(AIToolContext ctx, CancellationToken ct = default)
    {
        try
        {
            var doc = ctx.Document ?? throw new InvalidOperationException("文档对象为空");

            var startX = Units.Mm(ctx.GetParameter<double>("start_x"));
            var startY = Units.Mm(ctx.GetParameter<double>("start_y"));
            var endX   = Units.Mm(ctx.GetParameter<double>("end_x"));
            var endY   = Units.Mm(ctx.GetParameter<double>("end_y"));
            var height = Units.Mm(ctx.GetParameter("height", 3000.0));
            var levelId = ctx.GetParameter<int>("level_id");
            var typeId  = ctx.GetParameter<int?>("wall_type_id");

            var wall = _elements.CreateStraightWall(doc, startX, startY, endX, endY,
                                                    levelId, height, typeId);
            if (wall is null)
                return Task.FromResult(AIToolResult.Fail(
                    $"创建墙体失败（起点 {startX:F1},{startY:F1} → 终点 {endX:F1},{endY:F1}，标高 {levelId}）"));

            var id = ((Element)wall).Id.Value;
            return Task.FromResult(AIToolResult.Ok($"成功创建墙体，ID: {id}",
                new { wall_id = id, level_id = levelId, height_mm = height * Units.MmPerFoot }));
        }
        catch (Exception ex)
        {
            return Task.FromResult(AIToolResult.Fail($"创建墙体失败: {ex.Message}"));
        }
    }
}
```

服务层落地写法（注意 `height` 已经是英尺）：

```csharp
public object? CreateStraightWall(Document doc, double sx, double sy, double ex, double ey,
                                  int levelId, double height, int? wallTypeId)
{
    var level = doc.GetElement(new ElementId(levelId)) as Level
                ?? throw new InvalidOperationException($"找不到标高 {levelId}");
    var type = doc.GetElement(new ElementId(wallTypeId!.Value)) as WallType
               ?? throw new InvalidOperationException($"找不到墙类型 {wallTypeId}");

    var p0 = new XYZ(sx, sy, level.Elevation);
    var p1 = new XYZ(ex, ey, level.Elevation);
    if (p0.DistanceTo(p1) < 1e-6) throw new ArgumentException("墙起点与终点重合");

    return Wall.Create(doc, Line.CreateBound(p0, p1),
                       type.Id, level.Id,
                       height, 0.0, false, false);
}
```

#### 楼板（闭合轮廓）

```csharp
var loop = new CurveLoop();
for (int i = 0; i < pts.Count; i++)
{
    var a = pts[i];
    var b = pts[(i + 1) % pts.Count];       // 自动闭合
    if (a.DistanceTo(b) < 1e-6) continue;   // 跳过重合点
    loop.Append(Line.CreateBound(a, b));
}
if (loop.IsOpen()) throw new ArgumentException("轮廓未闭合");

var floor = Floor.Create(doc, new List<CurveLoop> { loop }, floorTypeId, levelId,
                         false, null, 0.0);
```

校验顺序（按失败成本从低到高）：

1. 点数 ≥ 3；
2. 相邻点不重合；
3. 首尾自动接上，`IsOpen() == false`；
4. 面积不为 0（极小面积会被 Revit 拒绝）；
5. 自交检测（复杂轮廓必做，否则 `Floor.Create` 返回 null 或直接抛异常）。

失败信息要带上点数、面积、第一个重合点索引。只回一句「创建失败」等于没写。

#### 柱 / 梁 / 独立族实例

```csharp
var symbol = doc.GetElement(new ElementId(typeId)) as FamilySymbol
             ?? throw new InvalidOperationException("族类型 ID 无效");
if (!symbol.IsActive) symbol.Activate();      // 不激活会静默创建失败

var inst = doc.Create.NewFamilyInstance(
    new XYZ(x, y, level.Elevation), symbol, level, StructuralType.NonStructural);
```

#### 门窗（有主体的族实例）

```csharp
var host = doc.GetElement(new ElementId(wallId)) as Wall
           ?? throw new InvalidOperationException($"找不到墙 {wallId}");

var inst = doc.Create.NewFamilyInstance(
    new XYZ(x, y, 0), symbol, host, StructuralType.NonStructural);
```

门窗必须依附于墙，坐标落在墙中心线上；创建后要读回 `Host` 或 `Location` 确认确实挂上了。

### 5.4 第二类：2D 详图（详图线 / 文字）

这类工具**必须有视图**，且视图要是可绘制的（平面/详图/剖面，不能是图纸视图）。

```csharp
var view = doc.GetElement(new ElementId(viewId)) as View
           ?? throw new InvalidOperationException("视图 ID 无效");

var curve = Line.CreateBound(new XYZ(x1, y1, 0), new XYZ(x2, y2, 0));
var detail = doc.Create.NewDetailCurve(view, curve);   // 返回 DetailCurve

// 颜色 / 线型 / 线宽靠 OverrideGraphicSettings，不是建模属性
var ogs = new OverrideGraphicSettings();
ogs.SetProjectionLineColor(new Color(255, 0, 0));
ogs.SetProjectionLinePatternId(patternId);
ogs.SetProjectionLineWeight(3);
view.SetElementOverrides(detail.Id, ogs);
```

文字：

```csharp
var note = TextNote.Create(doc, view.Id, new XYZ(x, y, 0), "轴网图 红色虚线",
                           new TextNoteOptions(textTypeId));
```

注意点：

- `NewDetailCurve` 只接受**平面曲线**，两端 `Z` 必须一致；带斜率的线会被拒绝。
- 线型 `LinePatternElement` 要先 `GetOrCreate`，不要假设文档里已有。
- 详图元素属于视图，删除视图就一起消失；跨视图复制用 `ElementTransformUtils.CopyElements`。
- 坐标按毫米→英尺换算，和模型工具保持一致。

### 5.5 第三类：标注（尺寸 / 标记）

尺寸标注依赖的是**几何引用**而不是元素 ID：

```csharp
var refs = new ReferenceArray();
foreach (var id in elementIds)
{
    var elem = doc.GetElement(new ElementId(id));
    var reference = new Reference(elem);       // 或取具体 face / edge 的引用
    refs.Append(reference);
}

var dim = doc.Create.NewDimension(view, Line.CreateBound(p0, p1), refs);
```

标记：

```csharp
var tag = IndependentTag.Create(doc, view.Id, new Reference(elem), false,
                                TagMode.TM_ADDBY_CATEGORY, TagOrientation.Horizontal, xyz);
```

难点在**参考面选择**：同一堵墙在不同视图里有多个可标注的几何引用，选错不会报错，只会把尺寸标到别处。归档里的做法是先按元素包围盒算出候选标注线，再逐条尝试，失败换下一个候选。

### 5.6 批量与单个参数并存

统一模式：

```csharp
if (ctx.HasParameter("walls")) { /* 读数组，逐条创建，收集成功/失败 */ }
else { /* 读单条参数 */ }
```

批量返回值必须能回答「建了几条、哪条失败、为什么」：

```json
{ "requested": 12, "created": 11, "failed": 1,
  "ids": [ ... ], "failures": [ { "index": 7, "reason": "起点与终点重合" } ] }
```

一条失败不要中断整批——除非原因是全局性的（文档只读、事务已回滚）。

### 5.7 创建完成后必须读回

```csharp
var created = doc.GetElement(new ElementId(id));
if (created is null)
    return AIToolResult.Fail($"创建返回了 ID {id}，但读回不到该元素");

var loc = (created.Location as LocationCurve)?.Curve;
// 比对起点/终点/标高/类型，和请求值一致才算真正成功
```

**不要用「API 没抛异常」当作成功判据。** Revit 里大量失败是返回 `null` 或静默修正参数。

---

## 6. 服务层怎么写

### 6.1 为什么工具不直接写 Revit API

如果每个工具都直接 `new FilteredElementCollector(...)`：

- 单位换算、ID 校验、异常文案各写一遍，很快不一致；
- 无法在不启动 Revit 的情况下做冒烟测试；
- 换 Revit 版本时改动面是 120 个工具，而不是 1 个服务。

归档里的做法是：**工具只认服务接口，服务才认 `Autodesk.Revit.DB`**。适配器把服务挂成只读属性（`ElementService`、`AnnotationService`、`ViewService`…），注册表按构造参数类型注入。

### 6.2 服务接口粒度

按业务对象切，不按 API 类切：

| 服务 | 职责 | 典型方法 |
| --- | --- | --- |
| `ILevelService` | 标高读写 | `GetAllLevels` / `CreateLevel` / `SetElevation` |
| `IElementService` | 元素创建 | `CreateStraightWall` / `CreateFloorByProfile` / `CreateDoorInWall` |
| `IAnnotationService` | 文字/标记/尺寸 | `CreateTextNote` / `CreateTag` / `CreateDimensionByElements` |
| `IModificationService` | 移动/复制/删除/改类型 | `MoveElements` / `CopyElement` / `DeleteElements` |
| `IViewService` | 视图创建与显示 | `CreateFloorPlan` / `ActivateView` / `SetDetailLevel` |
| `IUnitConverter` | 单位换算 | `MmToFeet` / `FeetToMm` / `MetersToFeet` |

### 6.3 服务层禁止事项

- 自己 `new Transaction(...)`；
- 自己弹 `TaskDialog` / `MessageBox`；
- 吞掉异常只 `return null`（要返回带原因的结果，或抛出由工具层统一转成 `AIToolResult.Fail`）；
- 依赖 UI 线程上的 `UIDocument` 做纯数据操作。

---

## 7. 返回值契约

```csharp
public sealed class AIToolResult
{
    public bool Success { get; set; }
    public string? Message { get; set; }   // 成功时给人看的一句话
    public object? Data { get; set; }      // 机器可读：ID、数量、失败明细
    public string? Error { get; set; }     // 失败原因，必须可定位

    public static AIToolResult Ok(string? message = null, object? data = null);
    public static AIToolResult Fail(string error);
}
```

三条实践：

1. **`Message` 给人和模型看，`Data` 给程序看**，不要把结构化信息塞进字符串。
2. 失败信息要包含：工具名、关键参数、为什么失败、下一步怎么做。
3. 查询类工具超过 200 条时用「摘要 + 缓存 ID」：前 200 条直接返回，其余存缓存，模型需要时按 `cache_id` 再取。归档里的 `OkWithSmartSummary` 就是这套机制。

---

## 8. 桥接层：外部怎么调这些工具

### 8.1 发现文件

插件启动时写 `%LOCALAPPDATA%\RevitAi\bridge.json`（以及按进程号区分的副本）：

```json
{ "pipeName": "RevitAi.54228.80907c5cc67f4929bc489b2c803a0d52",
  "processId": 54228,
  "startedAt": "2026-10-16T11:41:58Z" }
```

调用方读文件 → 检查 `processId` 是否还活着 → 连管道。进程不在了就是陈旧文件，直接拒绝，不要乱连。

### 8.2 协议：一行 JSON 进，一行 JSON 出

```jsonc
// 请求
{ "type": "invoke", "requestId": "abc123",
  "tool": "create_straight_wall",
  "arguments": { "start_x": 0, "start_y": 0, "end_x": 6000, "end_y": 0,
                 "level_id": 311, "height": 3000, "wall_type_id": 398 } }

// 响应
{ "type": "invoke", "requestId": "abc123", "success": true,
  "payload": { "tool": "create_straight_wall", "success": true,
               "message": "成功创建墙体，ID: 339672",
               "data": { "wall_id": 339672 },
               "elapsedMs": 42.7 },
  "durationMs": 43.1 }
```

支持的四类请求：

| type | 作用 | 备注 |
| --- | --- | --- |
| `ping` | 探活，返回进程号与工具数 | 客户端启动时先打一次 |
| `tools.list` | 返回全部工具元数据 + schema | 模型每次会话读一次 |
| `invoke` | 执行单个工具 | 一次只做一件事 |
| `error` | 失败响应 | 带 `errorCode` / `errorMessage` |

### 8.3 串行化

一个插件进程只有一个 ExternalEvent 槽位。桥接层必须**串行处理**请求，并拒绝并发调用（归档里返回 `revit_tool_busy`）。不要试图用多个 ExternalEvent 并行改模型，Revit 会直接拒绝或产生不可预期的合并结果。

### 8.4 常见错误码

| 码 | 含义 | 处理 |
| --- | --- | --- |
| `revit_context_not_ready` | 插件还没初始化 | 先在 Revit 里打开一次 AI 控制台 |
| `revit_tool_busy` | 上一个调用还没结束 | 等待后重试，不要并发 |
| 工具执行失败（错误码前缀随构建版本变化，以 live 桥接返回为准） | 工具内部抛出异常 | 读 `payload.errorMessage`，不要盲目重试写操作 |
| `bridge_error` | 协议/解析错误 | 检查 JSON 结构 |

---

## 9. 120 个工具是批量生产的

归档里的工具集不是一个个手写的，而是 **schema → 工具类** 生成：

```
工具目录（name / category / description / schema / mutating / requiresTransaction）
        │
        ├─ ToolSetGenerator ──▶ ExtToolLib.Tools.*   （只是元数据外壳）
        │
        └─ 实现注册表 ToolImplementationRegistry
                 │
                 └─ 每个工具名对应一个 IToolImplementation
```

生成出来的类只负责：声明 `Name/Category/Description/ParametersSchema`，然后在 `ExecuteAsync` 里查实现注册表并调用。真正的逻辑集中在实现文件里（`ModelingImplementations.cs`、`ReadOnlyImplementations.cs` 等按批次分组）。

好处：

- 增删工具只改目录，不用改 120 个文件；
- 工具名与既有目录保持 1:1，方便对照验收；
- 生成代码可以整块重跑，不会覆盖手写实现。

**生成文件不要手改**，改了下次生成会被覆盖。要改逻辑就改实现类。

---

## 10. 常见坑

这些是工具开发中真实踩过的，按出现频率排序：

| 坑 | 现象 | 对策 |
| --- | --- | --- |
| 事务没开就写模型 | 抛 "Cannot modify document outside transaction" | 写操作一律 `RequiresTransaction=true` |
| 单位混用 | 墙画到 30 米外，或只有 30 毫米长 | 入参统一毫米，服务入口一次换算，读回时再换回来 |
| 拿第一个类型兜底 | 模型里出现用户没要求的类型 | 精确按 ID 取，取不到就报错 |
| 未激活 FamilySymbol | `NewFamilyInstance` 返回 null | 创建前 `if (!symbol.IsActive) symbol.Activate();` |
| 轮廓未闭合/有重合点 | `Floor.Create` 返回 null | 创建前做 5 步几何校验 |
| 事务内没刷新 | 读回几何是旧值 | `Commit` 前 `Regenerate()` |
| 注释坐标用错视图 | 文字跑到别处或直接异常 | 严格校验 `viewId` 是可绘制视图 |
| 批量中一条失败全批回滚 | 用户看到「全部失败」 | 逐条 try/catch，返回成功数与失败明细 |
| 并发调用 | 随机异常、半提交 | 桥接层串行化，忙时返回 `revit_tool_busy` |

---

## 11. 新工具落地检查清单

写完后逐条打勾，缺一条都先别提交：

- [ ] 工具名是 `snake_case`，与既有目录不重复
- [ ] `[AITool]` 的 `RequiresTransaction` / `RequiresModification` 设置正确
- [ ] `ParametersSchema` 有 `type/properties/required`，单位写在 description 里
- [ ] ID 类参数有获取途径说明，运行时做了类型校验
- [ ] 工具类不直接引用 `Autodesk.Revit.DB`（只用服务接口与 `AIToolContext`）
- [ ] 服务层没有自己开事务、没有弹窗
- [ ] 单位在入口统一换算，没有散落的 `304.8`
- [ ] 批量模式有 `requested / created / failed / ids / failures`
- [ ] 失败信息包含关键参数与原因
- [ ] 创建后做了读回校验，返回真实 ID
- [ ] `tools.list` 的条数比上一版 +1，schema 与代码一致
- [ ] 用一个最小文档跑一次创建 + 读回 + 撤销（不保存）

---

## 12. 归档参考索引

以下路径均相对于本仓库根目录：

| 内容 | 路径 |
| --- | --- |
| 工具接口与特性 | `toolset/src/RevitAi.Engine.Abstractions/` |
| 工具上下文/结果 | 同上：`AIToolContext.cs`、`AIToolResult.cs`、`IAIToolRegistry.cs` |
| 反射注册表 | `toolset/src/RevitAi.Engine.Core/AIToolRegistry.cs` |
| 事务宿主 | `toolset/src/RevitAi.Engine.Revit/RevitToolHost.cs` |
| 调度与结果判定 | `toolset/src/RevitAi.Engine.Core/AIToolInvocationHandler.cs` |
| 墙/板/门窗实现 | `toolset/src/RevitAi.Engine.Revit/Services/ElementAndModificationServices.cs` |
| 文字/标记/尺寸实现 | `toolset/src/RevitAi.Engine.Revit/Services/WriteServices.cs` |
| 120 工具批量实现 | `toolset/src/ExtToolLib.Implementations/` |
| 生成器 | `toolset/tools/ToolSetGenerator/Program.cs` |
| 桥接协议 | `docs/bridge-protocol.md` |
| 工具目录快照 | `agent/revit-2027/references/tool-catalog.json`（以运行时 `tools list` 为准） |

---

## 附录：最小闭环

```powershell
# 1) 探活
.\agent\revit-2027\scripts\revit-ai.ps1 ping

# 2) 读上下文的 ID
.\agent\revit-2027\scripts\revit-ai.ps1 invoke get_all_levels '{}'
.\agent\revit-2027\scripts\revit-ai.ps1 invoke get_family_types '{"familyName":"墙"}'

# 3) 看目标工具的真实 schema（不要凭记忆写参数）
.\agent\revit-2027\scripts\revit-ai.ps1 schema create_straight_wall

# 4) 执行
.\agent\revit-2027\scripts\revit-ai.ps1 invoke create_straight_wall '{"start_x":0,"start_y":0,"end_x":6000,"end_y":0,"level_id":311,"height":3000,"wall_type_id":398}'

# 5) 读回
.\agent\revit-2027\scripts\revit-ai.ps1 invoke get_element_location '{"element_id":339672}'
.\agent\revit-2027\scripts\revit-ai.ps1 invoke get_element_geometry '{"elementId":339672}'
```

读回的值与请求值一致（允许浮点误差），才算这个工具写完。
