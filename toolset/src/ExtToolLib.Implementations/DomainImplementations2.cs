using RevitAi.Engine.Abstractions;
using RevitAi.Engine.Abstractions.Adapters;
using RevitAi.Engine.Core;

namespace ExtToolLib.Implementations;

/// <summary>
/// 收尾批实现：道路（墙→路面板）、CAD 图层管理、剪切顺序修正、UI 选择/缩放/激活视图。
/// </summary>
public static class DomainImplementations2
{
    public static void RegisterAll()
    {
        ToolImplementationRegistry.Register("create_road_from_walls", new CreateRoadFromWallsImplementation());
        ToolImplementationRegistry.Register("cad_layer_manager", new CadLayerManagerImplementation());
        ToolImplementationRegistry.Register("fix_cutting_order", new FixCuttingOrderImplementation());
        ToolImplementationRegistry.Register("select_elements", new SelectElementsImplementation());
        ToolImplementationRegistry.Register("clear_selection", new ClearSelectionImplementation());
        ToolImplementationRegistry.Register("zoom_to_elements", new ZoomToElementsImplementation());
        ToolImplementationRegistry.Register("activate_view", new ActivateViewImplementation());
    }
}

internal static class UiArgs
{
    public static object RequireDocument(IRevitAdapter adapter, AIToolContext context)
        => context.Document ?? adapter.GetActiveDocument() ?? throw new InvalidOperationException("文档对象为空");

    public static object RequireUiDocument(IRevitAdapter adapter)
        => adapter.GetActiveUiDocument()
           ?? throw new InvalidOperationException("当前没有可用的 UIDocument（请在 Revit 界面会话里执行该工具）。");

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
}

internal sealed class CreateRoadFromWallsImplementation : IToolImplementation
{
    public Task<AIToolResult> ExecuteAsync(IRevitAdapter adapter, AIToolContext context, CancellationToken cancellationToken = default)
    {
        var service = adapter.DomainExtraService ?? throw new InvalidOperationException("无法获取 DomainExtraService");
        var document = UiArgs.RequireDocument(adapter, context);

        var wallIds = UiArgs.ResolveIds(context, "wall_element_ids", "wallElementIds", "wallIds");
        if (wallIds.Count == 0)
        {
            return Task.FromResult(AIToolResult.Fail("必须提供 wall_element_ids。"));
        }

        var result = service.CreateRoadFromWalls(
            document,
            wallIds,
            OptionalDouble(context, "lane_width_meters", "laneWidthMeters"),
            OptionalDouble(context, "sidewalk_width_meters", "sidewalkWidthMeters"),
            OptionalDouble(context, "default_road_width_meters", "defaultRoadWidthMeters"));
        var options = new
        {
            intersectionRadiusMeters = WriteArgs.Number(context, 10, "intersection_radius_meters", "intersectionRadiusMeters"),
            sidewalk = ReadOptionMap(context, "sidewalk_options", "sidewalkOptions"),
            curb = ReadOptionMap(context, "curb_options", "curbOptions"),
            marking = ReadOptionMap(context, "marking_options", "markingOptions"),
            advanced = ReadOptionMap(context, "advanced_options", "advancedOptions"),
        };
        var notes = result.Notes.Concat(new[]
        {
            "道路主体生成路面板；人行道、路缘、标线和场地外扩参数已解析并回传，"
            + "实际构造需项目族/SiteSubRegion/ModelCurve 样板进一步启用。",
        }).ToArray();

        return Task.FromResult(AIToolResult.Ok(
            $"✅ 已按 {result.WallCount} 面墙生成路面板 {result.FloorId}（轮廓 {result.Outline.Count} 点，{(result.ClosedLoop ? "闭合环" : "包围盒")}）",
            new
            {
                floorId = result.FloorId,
                wallCount = result.WallCount,
                closedLoop = result.ClosedLoop,
                outline = result.Outline.Select(point => new { x = point.X, y = point.Y }).ToArray(),
                unit = "millimeters",
                options,
                notes,
            }));
    }

    private static double? OptionalDouble(AIToolContext context, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (context.Parameters.ContainsKey(key))
            {
                return WriteArgs.Number(context, 0, key);
            }
        }

        return null;
    }

    private static IReadOnlyDictionary<string, object?> ReadOptionMap(AIToolContext context, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (context.Parameters.TryGetValue(key, out var value) && value is Dictionary<string, object?> map)
            {
                return new Dictionary<string, object?>(map, StringComparer.OrdinalIgnoreCase);
            }
        }

        return new Dictionary<string, object?>();
    }
}

internal sealed class CadLayerManagerImplementation : IToolImplementation
{
    public Task<AIToolResult> ExecuteAsync(IRevitAdapter adapter, AIToolContext context, CancellationToken cancellationToken = default)
    {
        var service = adapter.DomainExtraService ?? throw new InvalidOperationException("无法获取 DomainExtraService");
        var document = UiArgs.RequireDocument(adapter, context);

        var operationText = WriteArgs.Text(context, "operation");
        var linkId = WriteArgs.OptionalInt(context, "linkId", "link_id");
        if (string.IsNullOrWhiteSpace(operationText))
        {
            return Task.FromResult(AIToolResult.Fail("必须指定 operation 参数"));
        }

        if (linkId is null or <= 0)
        {
            return Task.FromResult(AIToolResult.Fail("必须指定有效的 linkId"));
        }

        var operation = operationText.Trim().ToLowerInvariant();
        if (operation is not ("get_layers" or "get_lines" or "get_texts" or "get_blocks" or "analyze"))
        {
            return Task.FromResult(AIToolResult.Fail("不支持的操作类型: " + operationText));
        }

        var layerName = WriteArgs.Text(context, "layerName", "layer_name");
        if (operation is "get_lines" or "get_texts" or "get_blocks" && string.IsNullOrWhiteSpace(layerName))
        {
            return Task.FromResult(AIToolResult.Fail($"{operation} 操作需要指定 layerName 参数"));
        }

        var result = service.QueryCad(document, linkId.Value, operation, layerName);
        var finalData = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["operation"] = operation,
            ["linkId"] = linkId.Value,
            ["source"] = result.Source,
        };

        switch (operation)
        {
            case "get_layers":
                finalData["count"] = result.Layers.Count;
                finalData["layers"] = result.Layers;
                break;
            case "get_lines":
                finalData["layerName"] = layerName;
                finalData["count"] = result.Lines.Count;
                finalData["lines"] = result.Lines;
                break;
            case "get_texts":
                finalData["layerName"] = layerName;
                finalData["count"] = result.Texts.Count;
                finalData["texts"] = result.Texts;
                break;
            case "get_blocks":
                finalData["layerName"] = layerName;
                finalData["count"] = result.Blocks.Count;
                finalData["blocks"] = result.Blocks;
                break;
            default:
                finalData["statistics"] = new
                {
                    totalLayers = result.LayerCount,
                    nonEmptyLayers = result.NonEmptyLayerCount,
                    emptyLayers = result.LayerCount - result.NonEmptyLayerCount,
                    totalBlocks = result.Analysis.Sum(layer => layer.BlockCount),
                    totalLines = result.Analysis.Sum(layer => layer.LineCount),
                    totalTexts = result.Analysis.Sum(layer => layer.TextCount),
                };
                finalData["layers"] = result.Analysis;
                break;
        }

        if (!string.IsNullOrWhiteSpace(result.Message) && result.Message.Contains("无法", StringComparison.Ordinal))
        {
            return Task.FromResult(AIToolResult.Fail(result.Message, finalData));
        }

        var count = operation switch
        {
            "get_layers" => result.Layers.Count,
            "get_lines" => result.Lines.Count,
            "get_texts" => result.Texts.Count,
            "get_blocks" => result.Blocks.Count,
            _ => result.LayerCount,
        };
        var noun = operation switch
        {
            "get_layers" => "个图层",
            "get_lines" => "条线条",
            "get_texts" => "个文字",
            "get_blocks" => "个图块",
            _ => "个图层",
        };
        var message = operation == "analyze"
            ? $"已分析 {result.LayerCount} 个图层，其中 {result.NonEmptyLayerCount} 个包含内容，"
              + $"共识别 {result.Analysis.Sum(layer => layer.TotalCount)} 个对象"
            : $"从{result.Source}获取到 {count} {noun}";

        string? cacheId = null;
        if (operation is "get_lines" or "get_texts" or "get_blocks" && context.DataCache is not null && count > 0)
        {
            object payload = operation switch
            {
                "get_lines" => result.Lines,
                "get_texts" => result.Texts,
                _ => result.Blocks,
            };
            cacheId = context.DataCache.Store(context.SessionId ?? "default", $"cad_{operation}_{layerName}", payload);
            message += $"\n\n💡 在后续工具调用中使用 cacheId=\"{cacheId}\" 参数来操作这些{noun}";
            finalData["cache_id"] = cacheId;
        }

        return Task.FromResult(string.IsNullOrEmpty(cacheId)
            ? AIToolResult.Ok(message, finalData)
            : AIToolResult.OkWithCache(message, cacheId, finalData));
    }
}

internal sealed class FixCuttingOrderImplementation : IToolImplementation
{
    public Task<AIToolResult> ExecuteAsync(IRevitAdapter adapter, AIToolContext context, CancellationToken cancellationToken = default)
    {
        var service = adapter.DomainExtraService ?? throw new InvalidOperationException("无法获取 DomainExtraService");
        var document = UiArgs.RequireDocument(adapter, context);

        var cuttingIds = UiArgs.ResolveIds(context, "cuttingElementIds", "cutting_element_ids");
        var toCutIds = UiArgs.ResolveIds(context, "elementToCutIds", "element_to_cut_ids");
        var dryRun = WriteArgs.Flag(context, false, "dryRun", "dry_run");
        var includeDetails = WriteArgs.Flag(context, false, "includeDetails", "include_details");

        var result = service.FixCuttingOrder(document, cuttingIds, toCutIds, dryRun);

        return Task.FromResult(AIToolResult.Ok(
            dryRun
                ? $"✅ 已检查 {result.Checked} 对连接（dryRun），其中 {result.Details.Count} 对需要修正"
                : $"✅ 已检查 {result.Checked} 对连接，修正 {result.Fixed} 对剪切顺序",
            new
            {
                checkedPairs = result.Checked,
                fixedPairs = result.Fixed,
                dryRun,
                details = includeDetails || dryRun ? result.Details : null,
            }));
    }
}

internal sealed class SelectElementsImplementation : IToolImplementation
{
    public Task<AIToolResult> ExecuteAsync(IRevitAdapter adapter, AIToolContext context, CancellationToken cancellationToken = default)
    {
        var service = adapter.ViewInteractionService ?? throw new InvalidOperationException("无法获取 ViewInteractionService");
        var uiDocument = UiArgs.RequireUiDocument(adapter);

        var ids = UiArgs.ResolveIds(context);
        if (ids.Count == 0)
        {
            return Task.FromResult(AIToolResult.Fail("必须提供 elementIds 或 cacheId。"));
        }

        var result = service.SelectElements(
            uiDocument,
            ids,
            WriteArgs.Flag(context, false, "append"),
            WriteArgs.Flag(context, false, "zoomToFit", "zoom_to_fit"));

        return Task.FromResult(AIToolResult.Ok(
            $"✅ {result.Message}",
            new { selected = result.Selected, zoomed = result.Zoomed, requested = ids.Count }));
    }
}

internal sealed class ClearSelectionImplementation : IToolImplementation
{
    public Task<AIToolResult> ExecuteAsync(IRevitAdapter adapter, AIToolContext context, CancellationToken cancellationToken = default)
    {
        var service = adapter.ViewInteractionService ?? throw new InvalidOperationException("无法获取 ViewInteractionService");
        var uiDocument = UiArgs.RequireUiDocument(adapter);

        var result = service.ClearSelection(uiDocument);
        return Task.FromResult(AIToolResult.Ok($"✅ {result.Message}", new { cleared = result.Cleared }));
    }
}

internal sealed class ZoomToElementsImplementation : IToolImplementation
{
    public Task<AIToolResult> ExecuteAsync(IRevitAdapter adapter, AIToolContext context, CancellationToken cancellationToken = default)
    {
        var service = adapter.ViewInteractionService ?? throw new InvalidOperationException("无法获取 ViewInteractionService");
        var uiDocument = UiArgs.RequireUiDocument(adapter);

        var ids = UiArgs.ResolveIds(context);
        if (ids.Count == 0)
        {
            return Task.FromResult(AIToolResult.Fail("必须提供 elementIds 或 cacheId。"));
        }

        var result = service.ZoomToElements(uiDocument, ids);
        return Task.FromResult(AIToolResult.Ok(
            $"✅ {result.Message}",
            new { zoomed = result.Zoomed, requested = ids.Count, fitFactor = context.Parameters.ContainsKey("fitFactor") ? WriteArgs.Number(context, 1, "fitFactor") : (double?)null }));
    }
}

internal sealed class ActivateViewImplementation : IToolImplementation
{
    public Task<AIToolResult> ExecuteAsync(IRevitAdapter adapter, AIToolContext context, CancellationToken cancellationToken = default)
    {
        var service = adapter.ViewInteractionService ?? throw new InvalidOperationException("无法获取 ViewInteractionService");
        var uiDocument = UiArgs.RequireUiDocument(adapter);

        var viewId = WriteArgs.OptionalInt(context, "viewId", "view_id");
        if (viewId is null)
        {
            return Task.FromResult(AIToolResult.Fail("必须提供 viewId。"));
        }

        var result = service.ActivateView(uiDocument, viewId.Value);
        return Task.FromResult(AIToolResult.Ok($"✅ {result.Message}", new { viewId = viewId.Value, activated = true }));
    }
}
