using RevitAi.Engine.Abstractions;
using RevitAi.Engine.Abstractions.Adapters;
using RevitAi.Engine.Abstractions.Services;
using RevitAi.Engine.Core;

namespace ExtToolLib.Implementations;

/// <summary>
/// 批次「查询与交互」实现：元素查询/过滤、房间边界、轴网交点、偏移点、明细表字段与数据、
/// 碰撞检查、删除链接、项目单位、类别可见性。
/// </summary>
public static class QueryInteractionImplementations
{
    public static void RegisterAll()
    {
        ToolImplementationRegistry.Register("element_query", new ElementQueryImplementation());
        ToolImplementationRegistry.Register("filter_elements", new FilterElementsImplementation());
        ToolImplementationRegistry.Register("get_room_boundaries", new GetRoomBoundariesImplementation());
        ToolImplementationRegistry.Register("get_grid_intersection", new GetGridIntersectionImplementation());
        ToolImplementationRegistry.Register("get_offset_point", new GetOffsetPointImplementation());
        ToolImplementationRegistry.Register("get_schedule_fields", new GetScheduleFieldsImplementation());
        ToolImplementationRegistry.Register("read_schedule_data", new ReadScheduleDataImplementation());
        ToolImplementationRegistry.Register("check_collision", new CheckCollisionImplementation());
        ToolImplementationRegistry.Register("delete_link", new DeleteLinkImplementation());
        ToolImplementationRegistry.Register("set_project_units", new SetProjectUnitsImplementation());
        ToolImplementationRegistry.Register("set_category_visibility", new SetCategoryVisibilityImplementation());
    }
}

internal static class QueryArgs
{
    public static object RequireDocument(IRevitAdapter adapter, AIToolContext context)
        => context.Document ?? adapter.GetActiveDocument() ?? throw new InvalidOperationException("文档对象为空");

    public static List<int> ResolveIds(AIToolContext context, params string[] keys)
    {
        var all = keys.Length > 0 ? keys : new[] { "elementIds", "element_ids", "elementId", "element_id" };
        var ids = WriteArgs.Ids(context, all);

        if (ids.Count == 0)
        {
            var cacheId = WriteArgs.Text(context, "cacheId", "cache_id");
            if (!string.IsNullOrWhiteSpace(cacheId))
            {
                if (context.GetCachedData<List<Dictionary<string, object?>>>(cacheId) is { } cached)
                {
                    ids.AddRange(ToolArgumentReader.ExtractIds(cached));
                }
                else if (context.GetCachedData<List<int>>(cacheId) is { } cachedIds)
                {
                    ids.AddRange(cachedIds);
                }
            }
        }

        return ids.Distinct().ToList();
    }

    public static object Summarize(ElementSummary element) => new
    {
        elementId = element.ElementId,
        name = element.Name,
        category = element.CategoryName,
        typeId = element.TypeId,
        typeName = element.TypeName,
        levelId = element.LevelId,
        levelName = element.LevelName,
        location = element.X is null || element.Y is null || element.Z is null
            ? null
            : new { x = element.X, y = element.Y, z = element.Z, unit = "millimeters" },
        isElementType = element.IsElementType,
    };

    public static string StoreElements(AIToolContext context, string key, IReadOnlyList<ElementSummary> elements)
    {
        if (context.DataCache is null || elements.Count == 0)
        {
            return string.Empty;
        }

        var cached = elements.Select(element => new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["elementId"] = element.ElementId,
            ["id"] = element.ElementId,
            ["name"] = element.Name,
            ["category"] = element.CategoryName,
            ["typeId"] = element.TypeId,
            ["typeName"] = element.TypeName,
            ["levelId"] = element.LevelId,
            ["levelName"] = element.LevelName,
            ["x"] = element.X,
            ["y"] = element.Y,
            ["z"] = element.Z,
        }).ToList();
        return context.DataCache.Store(context.SessionId ?? "default", key, cached);
    }
}

// 结构分析对照：Revit\AS\Tools\Revit\Net8\AI\Tools\ElementQueryTool.cs（元素查询）
internal sealed class ElementQueryImplementation : IToolImplementation
{
    public Task<AIToolResult> ExecuteAsync(IRevitAdapter adapter, AIToolContext context, CancellationToken cancellationToken = default)
    {
        var service = adapter.AdvancedQueryService ?? throw new InvalidOperationException("无法获取 AdvancedQueryService");
        var document = QueryArgs.RequireDocument(adapter, context);

        var operationText = WriteArgs.Text(context, "operation");
        if (string.IsNullOrWhiteSpace(operationText))
        {
            return Task.FromResult(AIToolResult.Fail("必须指定 operation 参数"));
        }

        var operation = NormalizeOperation(operationText);
        IReadOnlyList<ElementSummary> elements;
        var requested = 0;
        string message;

        switch (operation)
        {
            case "by_id":
            {
                var ids = QueryArgs.ResolveIds(context, "elementId", "elementIds");
                if (ids.Count == 0 && WriteArgs.Text(context, "cacheId", "cache_id") is { } cachedId)
                {
                    ids = ResolveCachedIds(context, cachedId);
                }

                if (ids.Count == 0)
                {
                    return Task.FromResult(AIToolResult.Fail("by_id 操作需要提供 elementId 或 elementIds 参数"));
                }

                requested = ids.Count;
                elements = service.QueryElements(document, "by_id", ids, null, null, null, ids.Count);
                message = $"✅ 成功查询 {ids.Count} 个元素，找到 {elements.Count} 个";
                if (ids.Count > elements.Count)
                {
                    message += $"，{ids.Count - elements.Count} 个未找到";
                }

                break;
            }

            case "selected":
            {
                var selection = adapter.SelectionQueryService
                                ?? throw new InvalidOperationException("无法获取 SelectionService");
                var uiDocument = adapter.GetActiveUiDocument()
                                 ?? throw new InvalidOperationException("当前没有可用的 UIDocument");
                var ids = selection.GetSelectedElementIds(uiDocument);
                requested = ids.Count;
                elements = ids.Count == 0
                    ? Array.Empty<ElementSummary>()
                    : service.QueryElements(document, "by_id", ids, null, null, null, ids.Count);
                message = $"✅ 成功获取 {elements.Count} 个选中元素";
                break;
            }

            case "by_category":
            {
                var category = WriteArgs.Text(context, "categoryName", "category_name", "category");
                if (string.IsNullOrWhiteSpace(category))
                {
                    return Task.FromResult(AIToolResult.Fail("类别名称不能为空"));
                }

                elements = service.QueryElements(document, "by_category", Array.Empty<int>(), category, null, null, 10000);
                requested = elements.Count;
                message = $"✅ 找到 {elements.Count} 个 {category}";
                break;
            }

            case "by_family_type":
            {
                var typeId = WriteArgs.OptionalInt(context, "typeId", "type_id") ?? 0;
                if (typeId <= 0)
                {
                    return Task.FromResult(AIToolResult.Fail("typeId 必须大于 0"));
                }

                var familyService = adapter.FamilyQueryService
                                    ?? throw new InvalidOperationException("无法获取 FamilyService");
                var typeInfo = familyService.GetFamilyTypes(document, null, 10000)
                    .FirstOrDefault(type => type.TypeId == typeId);
                if (typeInfo is null)
                {
                    return Task.FromResult(AIToolResult.Fail($"找不到 ID 为 {typeId} 的族类型元素"));
                }

                var expectedTypeName = WriteArgs.Text(context, "typeName", "type_name");
                if (!string.IsNullOrWhiteSpace(expectedTypeName) &&
                    !string.Equals(expectedTypeName, typeInfo.TypeName, StringComparison.Ordinal))
                {
                    return Task.FromResult(AIToolResult.Fail(
                        $"类型名称不匹配：期望 '{expectedTypeName}'，实际 '{typeInfo.TypeName}'"));
                }

                elements = service.QueryElements(
                    document,
                    "by_type",
                    Array.Empty<int>(),
                    typeInfo.Category,
                    typeId,
                    typeInfo.TypeName,
                    10000);
                requested = elements.Count;
                message = $"✅ 找到 {elements.Count} 个使用类型 '{typeInfo.TypeName}' 的{typeInfo.Category}实例";
                break;
            }

            default:
                return Task.FromResult(AIToolResult.Fail("不支持的操作类型: " + operationText));
        }

        var cacheId = QueryArgs.StoreElements(context, $"element_query_{operation}", elements);
        if (!string.IsNullOrEmpty(cacheId))
        {
            message += $"\n\n💡 在后续工具调用中使用 cacheId=\"{cacheId}\" 参数来操作这些元素";
        }

        var data = new
        {
            operation,
            requested,
            found = elements.Count,
            count = elements.Count,
            elements = elements.Take(50).Select(QueryArgs.Summarize).ToArray(),
            cache_id = string.IsNullOrEmpty(cacheId) ? null : cacheId,
            cache_info = string.IsNullOrEmpty(cacheId)
                ? null
                : new { cache_id = cacheId, total_items = elements.Count, returned_items = Math.Min(50, elements.Count) },
        };

        return Task.FromResult(string.IsNullOrEmpty(cacheId)
            ? AIToolResult.Ok(message, data)
            : AIToolResult.OkWithCache(message, cacheId, data));
    }

    private static string NormalizeOperation(string operation)
        => operation.Trim().ToLowerInvariant() switch
        {
            "by_id" or "byid" or "id" or "ids" => "by_id",
            "selected" or "selection" => "selected",
            "by_category" or "bycategory" or "category" => "by_category",
            "by_family_type" or "byfamilytype" or "by_type" or "bytype" or "type" => "by_family_type",
            _ => operation.Trim().ToLowerInvariant(),
        };

    private static List<int> ResolveCachedIds(AIToolContext context, string cacheId)
    {
        if (context.GetCachedData<List<Dictionary<string, object?>>>(cacheId) is { } cached)
        {
            return ToolArgumentReader.ExtractIds(cached);
        }

        return context.GetCachedData<List<int>>(cacheId) ?? new List<int>();
    }
}

// 结构分析对照：Revit\AS\Tools\Revit\Net8\AI\Tools\FilterElementsTool.cs（元素查询）
internal sealed class FilterElementsImplementation : IToolImplementation
{
    public Task<AIToolResult> ExecuteAsync(IRevitAdapter adapter, AIToolContext context, CancellationToken cancellationToken = default)
    {
        var service = adapter.AdvancedQueryService ?? throw new InvalidOperationException("无法获取 AdvancedQueryService");
        var document = QueryArgs.RequireDocument(adapter, context);

        var elements = service.FilterElements(
            document,
            QueryArgs.ResolveIds(context),
            WriteArgs.Text(context, "categoryName", "category_name", "category"),
            WriteArgs.Text(context, "typeName", "type_name"),
            WriteArgs.Text(context, "levelName", "level_name"),
            (int)WriteArgs.Number(context, 200, "limit"));

        var customFilter = WriteArgs.Text(context, "customFilter", "custom_filter");

        return Task.FromResult(AIToolResult.Ok(
            $"✅ 过滤后剩 {elements.Count} 个元素",
            new
            {
                count = elements.Count,
                elements = elements.Select(QueryArgs.Summarize).ToArray(),
                customFilter,
                note = string.IsNullOrWhiteSpace(customFilter) ? null : "customFilter 为第三方侧表达式，本实现只回显不解析（安全考虑）。",
            }));
    }
}

// 结构分析对照：Revit\AS\Tools\Revit\Net8\AI\Tools\GetRoomBoundariesTool.cs（空间分析）
internal sealed class GetRoomBoundariesImplementation : IToolImplementation
{
    public Task<AIToolResult> ExecuteAsync(IRevitAdapter adapter, AIToolContext context, CancellationToken cancellationToken = default)
    {
        var service = adapter.AdvancedQueryService ?? throw new InvalidOperationException("无法获取 AdvancedQueryService");
        var document = QueryArgs.RequireDocument(adapter, context);

        var roomId = WriteArgs.OptionalInt(context, "roomId", "room_id");
        if (roomId is null)
        {
            return Task.FromResult(AIToolResult.Fail("必须提供 roomId。"));
        }

        var loops = service.GetRoomBoundaries(document, roomId.Value);
        var total = Math.Round(loops.Sum(loop => loop.LengthMm), 2);

        return Task.FromResult(AIToolResult.Ok(
            $"✅ 房间 {roomId} 共 {loops.Count} 条边界环，总周长 {total} mm",
            new
            {
                roomId = roomId.Value,
                loopCount = loops.Count,
                totalLengthMM = total,
                loops = loops.Select(loop => new
                {
                    index = loop.Index,
                    isOuter = loop.IsOuter,
                    lengthMM = loop.LengthMm,
                    pointCount = loop.Points.Count,
                    points = loop.Points.Select(point => new { x = point.X, y = point.Y }).ToArray(),
                }).ToArray(),
            }));
    }
}

// 结构分析对照：Revit\AS\Tools\Revit\Net8\AI\Tools\GetGridIntersectionTool.cs（空间分析）
internal sealed class GetGridIntersectionImplementation : IToolImplementation
{
    public Task<AIToolResult> ExecuteAsync(IRevitAdapter adapter, AIToolContext context, CancellationToken cancellationToken = default)
    {
        var service = adapter.AdvancedQueryService ?? throw new InvalidOperationException("无法获取 AdvancedQueryService");
        var document = QueryArgs.RequireDocument(adapter, context);

        var grid1 = WriteArgs.OptionalInt(context, "grid1_id", "grid1Id");
        var grid2 = WriteArgs.OptionalInt(context, "grid2_id", "grid2Id");
        if (grid1 is null || grid2 is null)
        {
            return Task.FromResult(AIToolResult.Fail("必须提供 grid1_id 与 grid2_id。"));
        }

        var result = service.GetGridIntersection(document, grid1.Value, grid2.Value);
        return Task.FromResult(result.Exists
            ? AIToolResult.Ok($"✅ 交点 ({result.X}, {result.Y}) mm", new { exists = true, x = result.X, y = result.Y, z = result.Z, unit = "millimeters" })
            : AIToolResult.Fail($"没有唯一交点：{result.Message}", new { exists = false }));
    }
}

// 结构分析对照：Revit\AS\Tools\Revit\Net8\AI\Tools\GetOffsetPointTool.cs（几何计算）
internal sealed class GetOffsetPointImplementation : IToolImplementation
{
    public Task<AIToolResult> ExecuteAsync(IRevitAdapter adapter, AIToolContext context, CancellationToken cancellationToken = default)
    {
        var basePoint = WriteArgs.Point(context, (0, 0, 0), "base_point", "basePoint", "point");
        var distance = WriteArgs.Number(context, double.NaN, "distance");
        if (double.IsNaN(distance))
        {
            return Task.FromResult(AIToolResult.Fail("必须提供 distance（毫米）。"));
        }

        var angle = context.Parameters.ContainsKey("angle")
            ? (double?)WriteArgs.Number(context, 0, "angle")
            : null;

        var directionText = WriteArgs.Text(context, "direction") ?? "east";
        var (dx, dy) = ResolveDirection(directionText, angle);
        if (dx == 0 && dy == 0 && angle is null)
        {
            return Task.FromResult(AIToolResult.Fail(
                "direction 只支持 north/south/east/west/ne/nw/se/sw（或提供 angle 角度）。"));
        }

        var x = basePoint.X + distance * dx;
        var y = basePoint.Y + distance * dy;

        return Task.FromResult(AIToolResult.Ok(
            $"✅ 偏移点 ({Math.Round(x, 2)}, {Math.Round(y, 2)}, {basePoint.Z}) mm",
            new
            {
                x = Math.Round(x, 2),
                y = Math.Round(y, 2),
                z = basePoint.Z,
                unit = "millimeters",
                basePoint = new { x = basePoint.X, y = basePoint.Y, z = basePoint.Z },
                direction = directionText,
                angle,
                distance,
            }));
    }

    private static (double Dx, double Dy) ResolveDirection(string direction, double? angle)
    {
        if (angle is not null)
        {
            var radians = angle.Value * Math.PI / 180.0;
            return (Math.Cos(radians), Math.Sin(radians));
        }

        return direction.Trim().ToLowerInvariant() switch
        {
            "north" or "n" or "北" => (0, 1),
            "south" or "s" or "南" => (0, -1),
            "east" or "e" or "东" => (1, 0),
            "west" or "w" or "西" => (-1, 0),
            "ne" or "东北" => (Math.Sqrt(0.5), Math.Sqrt(0.5)),
            "nw" or "西北" => (-Math.Sqrt(0.5), Math.Sqrt(0.5)),
            "se" or "东南" => (Math.Sqrt(0.5), -Math.Sqrt(0.5)),
            "sw" or "西南" => (-Math.Sqrt(0.5), -Math.Sqrt(0.5)),
            _ => (0, 0),
        };
    }
}

// 结构分析对照：Revit\AS\Tools\Revit\Net8\AI\Tools\GetScheduleFieldsTool.cs（明细表）
internal sealed class GetScheduleFieldsImplementation : IToolImplementation
{
    public Task<AIToolResult> ExecuteAsync(IRevitAdapter adapter, AIToolContext context, CancellationToken cancellationToken = default)
    {
        var service = adapter.AdvancedQueryService ?? throw new InvalidOperationException("无法获取 AdvancedQueryService");
        var document = QueryArgs.RequireDocument(adapter, context);

        var scheduleId = WriteArgs.OptionalInt(context, "scheduleId", "schedule_id");
        var scheduleName = WriteArgs.Text(context, "scheduleName", "schedule_name");
        if (scheduleId is null && string.IsNullOrWhiteSpace(scheduleName))
        {
            return Task.FromResult(AIToolResult.Fail("必须提供 scheduleId 或 scheduleName。"));
        }

        var fields = service.GetScheduleFields(document, scheduleId, scheduleName);
        return Task.FromResult(AIToolResult.Ok(
            $"✅ 明细表共 {fields.Count} 个字段",
            new
            {
                count = fields.Count,
                fields = fields.Select(field => new
                {
                    index = field.Index,
                    name = field.Name,
                    fieldType = field.FieldType,
                    isHidden = field.IsHidden,
                    heading = field.Heading,
                }).ToArray(),
            }));
    }
}

// 结构分析对照：Revit\AS\Tools\Revit\Net8\AI\Tools\ReadScheduleDataTool.cs（明细表）
internal sealed class ReadScheduleDataImplementation : IToolImplementation
{
    public Task<AIToolResult> ExecuteAsync(IRevitAdapter adapter, AIToolContext context, CancellationToken cancellationToken = default)
    {
        var service = adapter.AdvancedQueryService ?? throw new InvalidOperationException("无法获取 AdvancedQueryService");
        var document = QueryArgs.RequireDocument(adapter, context);

        var scheduleId = WriteArgs.OptionalInt(context, "scheduleId", "schedule_id");
        var scheduleName = WriteArgs.Text(context, "scheduleName", "schedule_name");
        if (scheduleId is null && string.IsNullOrWhiteSpace(scheduleName))
        {
            return Task.FromResult(AIToolResult.Fail("必须提供 scheduleId 或 scheduleName。"));
        }

        var data = service.ReadScheduleData(
            document,
            scheduleId,
            scheduleName,
            (int)WriteArgs.Number(context, 0, "startRow", "start_row"),
            (int)WriteArgs.Number(context, 200, "maxRows", "max_rows"),
            WriteArgs.Flag(context, true, "includeHeader", "include_header"));

        return Task.FromResult(AIToolResult.Ok(
            $"✅ 明细表 {data.ScheduleName} 共 {data.TotalRows} 行，本次返回 {data.ReturnedRows} 行",
            new
            {
                scheduleName = data.ScheduleName,
                totalRows = data.TotalRows,
                returnedRows = data.ReturnedRows,
                headers = data.Headers,
                rows = data.Rows,
            }));
    }
}

// 结构分析对照：Revit\AS\Tools\Revit\Net8\AI\Tools\CheckCollisionTool.cs（碰撞检查）
internal sealed class CheckCollisionImplementation : IToolImplementation
{
    public Task<AIToolResult> ExecuteAsync(IRevitAdapter adapter, AIToolContext context, CancellationToken cancellationToken = default)
    {
        var service = adapter.AdvancedQueryService ?? throw new InvalidOperationException("无法获取 AdvancedQueryService");
        var document = QueryArgs.RequireDocument(adapter, context);

        if (!context.Parameters.ContainsKey("source"))
        {
            return Task.FromResult(AIToolResult.Fail("必须提供 source（元素 ID、ID 数组、缓存 ID 或类别名称）。"));
        }

        var sourceResolution = ResolveSelector(context, service, document, "source");
        if (!string.IsNullOrWhiteSpace(sourceResolution.Error))
        {
            return Task.FromResult(AIToolResult.Fail("无法解析源元素：" + sourceResolution.Error));
        }

        var sources = sourceResolution.Ids;
        if (sources.Count == 0)
        {
            return Task.FromResult(AIToolResult.Fail(
                "无法解析源元素：请提供有效的元素 ID、ID 数组、缓存 ID 或类别名称"));
        }

        List<int> targets;
        if (!context.Parameters.ContainsKey("target"))
        {
            targets = sources.ToList();
        }
        else
        {
            var targetResolution = ResolveSelector(context, service, document, "target");
            if (!string.IsNullOrWhiteSpace(targetResolution.Error))
            {
                return Task.FromResult(AIToolResult.Fail("无法解析目标元素：" + targetResolution.Error));
            }

            targets = targetResolution.Ids;
        }

        var sourceLink = WriteArgs.OptionalInt(context, "sourceLinkInstanceId", "source_link_instance_id");
        var targetLink = WriteArgs.OptionalInt(context, "targetLinkInstanceId", "target_link_instance_id");

        var pairs = service.CheckCollision(document, sources, targets, sourceLink, targetLink);
        var collisions = pairs.Where(pair => pair.Intersects).ToList();
        var checkedPairs = Math.Max(pairs.Count, sources.Count * Math.Max(1, targets.Count));
        var rate = checkedPairs == 0 ? 0 : Math.Round(collisions.Count * 100.0 / checkedPairs, 1);
        var message = $"碰撞检查完成：共检查 {checkedPairs} 对元素，发现 {collisions.Count} 对存在碰撞（碰撞率 {rate}%）";

        var resultItems = collisions.Select(pair => new
        {
            sourceId = pair.SourceId,
            targetId = pair.TargetId,
            sourceDocument = pair.SourceDocumentIndex == 0 ? "主模型" : "链接模型",
            targetDocument = pair.TargetDocumentIndex == 0 ? "主模型" : "链接模型",
            sourceLinkInstanceId = pair.SourceLinkInstanceId,
            targetLinkInstanceId = pair.TargetLinkInstanceId,
            sourceCategory = pair.SourceCategory,
            targetCategory = pair.TargetCategory,
            sourceTypeName = pair.SourceTypeName,
            targetTypeName = pair.TargetTypeName,
            collisionPoints = pair.CollisionPoints,
        }).ToList();

        string? itemsCache = null;
        string? pairsCache = null;
        string? elementIdsCache = null;
        if (context.DataCache is not null)
        {
            var session = context.SessionId ?? "default";
            itemsCache = context.DataCache.Store(session, "collision_items", resultItems);
            pairsCache = context.DataCache.Store(
                session,
                "collision_pairs",
                collisions.Select(pair => new[] { pair.SourceId, pair.TargetId }).ToList());
            elementIdsCache = context.DataCache.Store(
                session,
                "collision_element_ids",
                collisions.SelectMany(pair => new[] { pair.SourceId, pair.TargetId }).Distinct().ToList());
            message += $"\n\n💡 碰撞数据已分别缓存：\n- 着色使用：elements={elementIdsCache}"
                       + $"\n- 创建视图使用：pairs={pairsCache}"
                       + $"\n- 完整详情：items={itemsCache}";
        }

        var data = new
        {
            sourceCount = sources.Count,
            targetCount = targets.Count,
            checkedPairs,
            collisionCount = collisions.Count,
            collisionRate = rate,
            collisions = resultItems.Take(50).ToArray(),
            sourceLinkInstanceId = sourceLink,
            targetLinkInstanceId = targetLink,
            cache_info = itemsCache is null
                ? null
                : new
                {
                    items = itemsCache,
                    pairs = pairsCache,
                    element_ids = elementIdsCache,
                    total_items = collisions.Count,
                    returned_items = Math.Min(50, collisions.Count),
                    truncated = collisions.Count > 50,
                },
        };

        return Task.FromResult(itemsCache is null
            ? AIToolResult.Ok(message, data)
            : AIToolResult.OkWithCache(message, itemsCache, data));
    }

    private static (List<int> Ids, string? Error) ResolveSelector(
        AIToolContext context,
        IAdvancedQueryService service,
        object document,
        string key)
    {
        if (!context.Parameters.TryGetValue(key, out var value) || value is null)
        {
            return (new List<int>(), null);
        }

        try
        {
            switch (value)
            {
                case long or int or double or decimal:
                    return (new List<int> { (int)ToolArgumentReader.ToDouble(value) }, null);

                case string text:
                    return ResolveSelectorText(context, service, document, text);

                case List<object?> list:
                {
                    var ids = new List<int>();
                    foreach (var item in list)
                    {
                        if (item is string itemText)
                        {
                            var resolved = ResolveSelectorText(context, service, document, itemText);
                            if (!string.IsNullOrWhiteSpace(resolved.Error))
                            {
                                return resolved;
                            }

                            ids.AddRange(resolved.Ids);
                        }
                        else if (item is not null)
                        {
                            ids.Add((int)ToolArgumentReader.ToDouble(item));
                        }
                    }

                    return (ids.Distinct().ToList(), null);
                }

                default:
                    return (new List<int>(), $"不支持的 selector 类型 {value.GetType().Name}");
            }
        }
        catch (Exception ex)
        {
            return (new List<int>(), ex.GetBaseException().Message);
        }
    }

    private static (List<int> Ids, string? Error) ResolveSelectorText(
        AIToolContext context,
        IAdvancedQueryService service,
        object document,
        string text)
    {
        var value = text.Trim();
        if (value.StartsWith("cache_", StringComparison.OrdinalIgnoreCase))
        {
            if (context.GetCachedData<List<Dictionary<string, object?>>>(value) is { } cached)
            {
                return (ToolArgumentReader.ExtractIds(cached), null);
            }

            if (context.GetCachedData<List<int>>(value) is { } cachedIds)
            {
                return (cachedIds.ToList(), null);
            }

            return (new List<int>(), $"缓存 ID '{value}' 无效或已过期");
        }

        if (int.TryParse(value, out var id))
        {
            return (new List<int> { id }, null);
        }

        var elements = service.QueryElements(document, "by_category", Array.Empty<int>(), value, null, null, 10000);
        return (elements.Select(element => element.ElementId).Distinct().ToList(), null);
    }
}

// 结构分析对照：Revit\AS\Tools\Revit\Net8\AI\Tools\DeleteLinkTool.cs（链接管理）
internal sealed class DeleteLinkImplementation : IToolImplementation
{
    public Task<AIToolResult> ExecuteAsync(IRevitAdapter adapter, AIToolContext context, CancellationToken cancellationToken = default)
    {
        var service = adapter.AdvancedQueryService ?? throw new InvalidOperationException("无法获取 AdvancedQueryService");
        var document = QueryArgs.RequireDocument(adapter, context);

        var linkId = WriteArgs.OptionalInt(context, "linkId", "link_id");
        if (linkId is null)
        {
            return Task.FromResult(AIToolResult.Fail("必须提供 linkId。"));
        }

        var deleted = service.DeleteLink(document, linkId.Value);
        return Task.FromResult(deleted
            ? AIToolResult.Ok($"✅ 已删除链接 {linkId}", new { linkId = linkId.Value, deleted = true })
            : AIToolResult.Fail($"链接 {linkId} 未被删除。", new { linkId = linkId.Value, deleted = false }));
    }
}

// 结构分析对照：Revit\AS\Tools\Revit\Net8\AI\Tools\SetProjectUnitsTool.cs（项目设置）
internal sealed class SetProjectUnitsImplementation : IToolImplementation
{
    public Task<AIToolResult> ExecuteAsync(IRevitAdapter adapter, AIToolContext context, CancellationToken cancellationToken = default)
    {
        var service = adapter.ProjectSettingsService ?? throw new InvalidOperationException("无法获取 ProjectSettingsService");
        var document = QueryArgs.RequireDocument(adapter, context);

        var length = WriteArgs.Text(context, "length");
        var area = WriteArgs.Text(context, "area");
        var volume = WriteArgs.Text(context, "volume");
        var angle = WriteArgs.Text(context, "angle");
        var slope = WriteArgs.Text(context, "slope");

        if (length is null && area is null && volume is null && angle is null && slope is null)
        {
            return Task.FromResult(AIToolResult.Fail(
                "必须至少提供 length/area/volume/angle/slope 之一（如 length=mm、area=m2、angle=deg）。"));
        }

        var changed = service.SetProjectUnits(document, length, area, volume, angle, slope);
        return Task.FromResult(AIToolResult.Ok(
            $"✅ 已更新 {changed} 项项目单位",
            new { changed, length, area, volume, angle, slope }));
    }
}

// 结构分析对照：Revit\AS\Tools\Revit\Net8\AI\Tools\SetCategoryVisibilityTool.cs（视图管理）
internal sealed class SetCategoryVisibilityImplementation : IToolImplementation
{
    public Task<AIToolResult> ExecuteAsync(IRevitAdapter adapter, AIToolContext context, CancellationToken cancellationToken = default)
    {
        var service = adapter.ProjectSettingsService ?? throw new InvalidOperationException("无法获取 ProjectSettingsService");
        var document = QueryArgs.RequireDocument(adapter, context);

        var viewId = WriteArgs.OptionalInt(context, "view_id", "viewId");
        var categoryName = WriteArgs.Text(context, "category_name", "categoryName", "category");
        if (viewId is null || string.IsNullOrWhiteSpace(categoryName))
        {
            return Task.FromResult(AIToolResult.Fail("必须提供 view_id 与 category_name。"));
        }

        var visible = WriteArgs.Flag(context, true, "visible");
        var changed = service.SetCategoryVisibility(document, viewId.Value, categoryName!, visible);

        return Task.FromResult(AIToolResult.Ok(
            $"✅ 视图 {viewId} 的类别「{categoryName}」已{(visible ? "显示" : "隐藏")}",
            new { viewId = viewId.Value, categoryName, visible, changed }));
    }
}
