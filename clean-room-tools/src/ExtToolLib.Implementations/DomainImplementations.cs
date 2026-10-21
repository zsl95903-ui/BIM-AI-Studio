using RevitAi.Engine.Abstractions;
using RevitAi.Engine.Abstractions.Adapters;
using RevitAi.Engine.Abstractions.Services;
using RevitAi.Engine.Core;

namespace ExtToolLib.Implementations;

/// <summary>
/// 批次 5 第一批实现：地形、保温、MEP 系统。
/// </summary>
public static class DomainImplementations
{
    public static void RegisterAll()
    {
        ToolImplementationRegistry.Register("create_topography_surface", new CreateTopographySurfaceImplementation());
        ToolImplementationRegistry.Register("create_topography_from_floors", new CreateTopographyFromFloorsImplementation());
        ToolImplementationRegistry.Register("insulation_manager", new InsulationManagerImplementation());
        ToolImplementationRegistry.Register("insulation_query", new InsulationQueryImplementation());
        ToolImplementationRegistry.Register("mep_system_manager", new MepSystemManagerImplementation());
        ToolImplementationRegistry.Register("mep_system_query", new MepSystemQueryImplementation());
    }
}

internal static class DomainArgs
{
    public static object RequireDocument(IRevitAdapter adapter, AIToolContext context)
        => context.Document ?? adapter.GetActiveDocument() ?? throw new InvalidOperationException("文档对象为空");

    public static List<int> ResolveIds(AIToolContext context, params string[] keys)
    {
        var all = keys.Length > 0 ? keys : new[] { "element_ids", "elementIds", "element_id", "elementId" };
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

    /// <summary>支持 points: [{x,y,z}] 与 points: [[x,y,z]]。</summary>
    public static List<Point3> ReadPoints3(AIToolContext context, string key)
    {
        var points = new List<Point3>();
        if (!context.Parameters.TryGetValue(key, out var value) || value is not List<object?> list)
        {
            return points;
        }

        foreach (var entry in list)
        {
            switch (entry)
            {
                case Dictionary<string, object?> map:
                    points.Add(new Point3(
                        ModelingArgs.Number(map, "x"),
                        ModelingArgs.Number(map, "y"),
                        ModelingArgs.Number(map, "z")));
                    break;
                case List<object?> triple when triple.Count >= 3:
                    points.Add(new Point3(
                        ToolArgumentReader.ToDouble(triple[0]),
                        ToolArgumentReader.ToDouble(triple[1]),
                        ToolArgumentReader.ToDouble(triple[2])));
                    break;
            }
        }

        return points;
    }
}

// 结构分析对照：Revit\AS\Tools\Revit\Net8\AI\Tools\CreateTopographySurfaceTool.cs（地形）
internal sealed class CreateTopographySurfaceImplementation : IToolImplementation
{
    public Task<AIToolResult> ExecuteAsync(IRevitAdapter adapter, AIToolContext context, CancellationToken cancellationToken = default)
    {
        var service = adapter.TopographyService ?? throw new InvalidOperationException("无法获取 TopographyService");
        var document = DomainArgs.RequireDocument(adapter, context);

        var points = DomainArgs.ReadPoints3(context, "points");
        if (points.Count < 3)
        {
            return Task.FromResult(AIToolResult.Fail("必须提供 points（至少 3 个点，格式 [{x,y,z}] 或 [[x,y,z]]，单位毫米）。"));
        }

        var topographyId = service.CreateTopography(document, points);
        var minZ = points.Min(point => point.Z);
        var maxZ = points.Max(point => point.Z);

        return Task.FromResult(AIToolResult.Ok(
            $"✅ 已创建地形 {topographyId}（{points.Count} 个点，高程 {minZ}–{maxZ} mm）",
            new { topographyId, pointCount = points.Count, minElevationMM = minZ, maxElevationMM = maxZ }));
    }
}

// 结构分析对照：Revit\AS\Tools\Revit\Net8\AI\Tools\CreateTopographyFromFloorsTool.cs（地形）
internal sealed class CreateTopographyFromFloorsImplementation : IToolImplementation
{
    public Task<AIToolResult> ExecuteAsync(IRevitAdapter adapter, AIToolContext context, CancellationToken cancellationToken = default)
    {
        var service = adapter.TopographyService ?? throw new InvalidOperationException("无法获取 TopographyService");
        var document = DomainArgs.RequireDocument(adapter, context);

        var topographyId = WriteArgs.OptionalInt(context, "topography_element_id", "topographyElementId");
        if (topographyId is null)
        {
            return Task.FromResult(AIToolResult.Fail("必须提供 topography_element_id（目标地形元素）。"));
        }

        var floorIds = DomainArgs.ResolveIds(context, "floor_element_ids", "floorElementIds", "floor_element_id", "floorElementId");
        if (floorIds.Count == 0)
        {
            var cacheId = WriteArgs.Text(context, "floor_cache_id", "floorCacheId");
            if (!string.IsNullOrWhiteSpace(cacheId) &&
                context.GetCachedData<List<Dictionary<string, object?>>>(cacheId) is { } cached)
            {
                floorIds.AddRange(ToolArgumentReader.ExtractIds(cached));
            }
        }

        if (floorIds.Count == 0)
        {
            return Task.FromResult(AIToolResult.Fail("必须提供 floor_element_id/floor_element_ids 或 floor_cache_id。"));
        }

        var confirmed = WriteArgs.Flag(context, false, "confirmed");
        if (!confirmed)
        {
            return Task.FromResult(AIToolResult.Fail(
                $"该操作会向地形 {topographyId} 追加 {floorIds.Count} 个楼板的轮廓点（可能改变既有地形形状）；"
                + "请在确认后带上 confirmed=true 重新调用。"));
        }

        var info = service.AddPointsFromFloors(document, topographyId.Value, floorIds);
        return Task.FromResult(AIToolResult.Ok(
            $"✅ 地形 {info.ElementId} 现有 {info.PointCount} 个点（本次新增 {info.AddedPoints} 个），高程 {info.MinElevationMm}–{info.MaxElevationMm} mm",
            new
            {
                topographyId = info.ElementId,
                pointCount = info.PointCount,
                addedPoints = info.AddedPoints,
                minElevationMM = info.MinElevationMm,
                maxElevationMM = info.MaxElevationMm,
                floorElementIds = floorIds,
            }));
    }
}

// 结构分析对照：Revit\AS\Tools\Revit\Net8\AI\Tools\InsulationManagerTool.cs（保温）
internal sealed class InsulationManagerImplementation : IToolImplementation
{
    public Task<AIToolResult> ExecuteAsync(IRevitAdapter adapter, AIToolContext context, CancellationToken cancellationToken = default)
    {
        var service = adapter.InsulationService ?? throw new InvalidOperationException("无法获取 InsulationService");
        var document = DomainArgs.RequireDocument(adapter, context);

        var operationText = WriteArgs.Text(context, "operation");
        if (string.IsNullOrWhiteSpace(operationText))
        {
            return Task.FromResult(AIToolResult.Fail("必须指定 operation 参数"));
        }

        var operation = NormalizeInsulationOperation(operationText);
        if (operation is null)
        {
            return Task.FromResult(AIToolResult.Fail(
                "不支持的操作类型: " + operationText
                + "（支持 add_to_elements / remove_from_elements / modify_thickness / modify_material / add_to_system）"));
        }

        var ids = DomainArgs.ResolveIds(context);
        var systemTypeId = WriteArgs.OptionalInt(context, "system_type_id", "systemTypeId");
        var thickness = context.Parameters.ContainsKey("thickness")
            ? (double?)WriteArgs.Number(context, 0, "thickness")
            : null;
        var material = WriteArgs.Text(context, "material", "materialName");
        var overrideExisting = WriteArgs.Flag(context, true, "override_existing", "overrideExisting");

        if (operation == "add_to_system")
        {
            if (systemTypeId is null or <= 0)
            {
                return Task.FromResult(AIToolResult.Fail("必须指定有效的系统类型 ID (system_type_id)"));
            }
        }
        else if (ids.Count == 0)
        {
            return Task.FromResult(AIToolResult.Fail(
                operation == "add_to_elements"
                    ? "必须指定要添加保温的元素 ID 列表 (element_ids)"
                    : "必须指定要修改/删除保温的元素 ID 列表 (element_ids)"));
        }

        if (operation is "add_to_elements" or "modify_thickness" or "add_to_system" &&
            (thickness is null or <= 0))
        {
            return Task.FromResult(AIToolResult.Fail("必须指定有效的保温厚度 (thickness)"));
        }

        if (operation == "modify_material" && string.IsNullOrWhiteSpace(material))
        {
            return Task.FromResult(AIToolResult.Fail("必须指定保温材质名称 (material)"));
        }

        var result = service.ManageInsulation(
            document,
            operation,
            ids,
            thickness,
            material,
            overrideExisting,
            systemTypeId);

        var changed = result.SuccessCount + result.UpdatedCount;
        var prefix = operation switch
        {
            "add_to_elements" => $"成功为 {changed} 个元素添加保温",
            "remove_from_elements" => $"成功删除 {result.SuccessCount} 个元素的保温",
            "modify_thickness" => $"成功修改 {result.UpdatedCount} 个元素的保温厚度为 {thickness} mm",
            "modify_material" => $"成功修改 {result.UpdatedCount} 个元素的保温材质为 '{material}'",
            "add_to_system" => $"成功为系统 '{result.ElementKind}' 添加保温",
            _ => $"保温操作 {operation} 完成",
        };

        var details = new List<string>();
        if (result.UpdatedCount > 0 && operation == "add_to_elements")
        {
            details.Add($"修改 {result.UpdatedCount} 个现有保温");
        }

        if (result.SkippedCount > 0)
        {
            details.Add($"跳过 {result.SkippedCount} 个");
        }

        if (result.FailedCount > 0)
        {
            details.Add($"失败 {result.FailedCount} 个");
        }

        var message = prefix + (details.Count > 0 ? "，" + string.Join("，", details) : string.Empty);
        var data = new
        {
            operation,
            requested = result.Requested,
            affected = changed,
            success = result.SuccessCount,
            updated = result.UpdatedCount,
            skipped = result.SkippedCount,
            failed = result.FailedCount,
            element_ids = result.AffectedElementIds,
            skipped_element_ids = result.SkippedElementIds,
            errors = result.Errors.Count == 0 ? null : result.Errors,
            thicknessMM = result.ThicknessMm,
            material = result.MaterialName,
            override_existing = overrideExisting,
            system_type_id = result.SystemTypeId,
            element_type = result.ElementKind,
        };

        return Task.FromResult(result.FailedCount > 0 && changed == 0
            ? AIToolResult.Fail(message, data)
            : AIToolResult.Ok(message, data));
    }

    private static string? NormalizeInsulationOperation(string operation)
        => operation.Trim().ToLowerInvariant() switch
        {
            "add_to_elements" or "add" or "apply" => "add_to_elements",
            "remove_from_elements" or "remove" or "delete" => "remove_from_elements",
            "modify_thickness" or "update_thickness" => "modify_thickness",
            "modify_material" or "update_material" => "modify_material",
            "add_to_system" => "add_to_system",
            _ => null,
        };
}

// 结构分析对照：Revit\AS\Tools\Revit\Net8\AI\Tools\InsulationQueryTool.cs（保温）
internal sealed class InsulationQueryImplementation : IToolImplementation
{
    public Task<AIToolResult> ExecuteAsync(IRevitAdapter adapter, AIToolContext context, CancellationToken cancellationToken = default)
    {
        var service = adapter.InsulationService ?? throw new InvalidOperationException("无法获取 InsulationService");
        var document = DomainArgs.RequireDocument(adapter, context);

        var operationText = WriteArgs.Text(context, "operation");
        if (string.IsNullOrWhiteSpace(operationText))
        {
            return Task.FromResult(AIToolResult.Fail("必须指定 operation 参数"));
        }

        var operation = operationText.Trim().ToLowerInvariant();
        if (operation is not ("list_systems" or "query_elements" or "query_types" or "query_size_ranges" or "query_uninsulated"))
        {
            return Task.FromResult(AIToolResult.Fail("不支持的操作类型: " + operationText));
        }

        var target = (WriteArgs.Text(context, "target") ?? "all").Trim().ToLowerInvariant();
        if (target is not ("pipe" or "duct" or "all"))
        {
            return Task.FromResult(AIToolResult.Fail("target 只支持 pipe / duct / all"));
        }

        var limit = (int)WriteArgs.Number(context, 200, "limit");
        var systemTypeId = WriteArgs.OptionalInt(context, "system_type_id", "systemTypeId");
        IReadOnlyList<InsulationInfo> infos = Array.Empty<InsulationInfo>();
        MepSystemQueryResult? systems = null;
        IReadOnlyList<string>? types = null;

        switch (operation)
        {
            case "query_elements":
            {
                var ids = DomainArgs.ResolveIds(context);
                if (ids.Count == 0)
                {
                    return Task.FromResult(AIToolResult.Fail(
                        "query_elements 操作需要提供 element_ids、elementIds 或 cacheId"));
                }

                infos = service.QueryMepInsulation(document, ids, target, false, limit);
                break;
            }

            case "query_types":
                types = service.ListInsulationTypes(document, target);
                break;

            case "list_systems":
            case "query_uninsulated":
            {
                var mep = adapter.MepSystemService
                          ?? throw new InvalidOperationException("无法获取 MepSystemService");
                systems = mep.QueryMepSystems(
                    document,
                    "insulation_status",
                    target,
                    systemTypeId,
                    operation == "query_uninsulated",
                    false,
                    false,
                    false,
                    limit);
                break;
            }

            case "query_size_ranges":
            {
                if (target == "all")
                {
                    return Task.FromResult(AIToolResult.Fail(
                        "query_size_ranges 操作必须指定 target 为 pipe 或 duct"));
                }

                if (systemTypeId is null)
                {
                    return Task.FromResult(AIToolResult.Fail(
                        "query_size_ranges 操作需要指定 system_type_id 参数"));
                }

                var mep = adapter.MepSystemService
                          ?? throw new InvalidOperationException("无法获取 MepSystemService");
                systems = mep.QueryMepSystems(
                    document,
                    "size_distribution",
                    target,
                    systemTypeId,
                    false,
                    true,
                    false,
                    false,
                    limit);
                break;
            }
        }

        if (systems is not null)
        {
            var message = operation switch
            {
                "query_uninsulated" =>
                    $"成功获取 {systems.TotalCount} 个系统的保温状态，其中 {systems.TotalCount} 个系统存在未保温成员",
                "query_size_ranges" =>
                    $"成功获取{(target == "pipe" ? "水管" : "风管")}系统的尺寸分布",
                _ =>
                    $"成功获取 {systems.TotalCount} 个系统的保温状态",
            };

            return Task.FromResult(AIToolResult.Ok(message, new
            {
                operation,
                target,
                systemTypeId,
                systemCount = systems.TotalCount,
                systems = systems.Systems,
                sizeDistribution = systems.SizeDistribution,
            }));
        }

        if (types is not null)
        {
            return Task.FromResult(AIToolResult.Ok(
                $"✅ 找到 {types.Count} 个保温类型",
                new { operation, target, count = types.Count, types }));
        }

        var insulated = infos.Count(info => info.HasInsulation);
        var totalThickness = Math.Round(infos.Where(info => info.HasInsulation).Sum(info => info.ThicknessMm), 2);
        return Task.FromResult(AIToolResult.Ok(
            $"✅ 保温查询：{infos.Count} 个元素，其中已保温 {insulated} 个，总厚度 {totalThickness} mm",
            new
            {
                operation,
                target,
                recordCount = infos.Count,
                insulatedCount = insulated,
                totalThicknessMM = totalThickness,
                records = infos.Select(info => new
                {
                    elementId = info.ElementId,
                    elementName = info.ElementName,
                    elementType = info.TypeName,
                    hasInsulation = info.HasInsulation,
                    thicknessMM = info.ThicknessMm,
                    insulationType = info.MaterialName,
                }).ToArray(),
            }));
    }
}

// 结构分析对照：Revit\AS\Tools\Revit\Net8\AI\Tools\MepSystemManagerTool.cs（管网）
internal sealed class MepSystemManagerImplementation : IToolImplementation
{
    public Task<AIToolResult> ExecuteAsync(IRevitAdapter adapter, AIToolContext context, CancellationToken cancellationToken = default)
    {
        var service = adapter.MepSystemService ?? throw new InvalidOperationException("无法获取 MepSystemService");
        var document = DomainArgs.RequireDocument(adapter, context);

        var operationText = WriteArgs.Text(context, "operation");
        if (string.IsNullOrWhiteSpace(operationText))
        {
            return Task.FromResult(AIToolResult.Fail("必须指定 operation 参数"));
        }

        var operation = operationText.Trim().ToLowerInvariant();
        var systemTypeId = WriteArgs.OptionalInt(context, "system_type_id", "systemTypeId");
        var sourceSystemTypeId = WriteArgs.OptionalInt(context, "source_system_type_id", "sourceSystemTypeId");
        var newName = WriteArgs.Text(context, "new_system_name", "new_name", "name");
        var force = WriteArgs.Flag(context, false, "force");

        switch (operation)
        {
            case "duplicate":
            {
                if (sourceSystemTypeId is null or <= 0)
                {
                    return Task.FromResult(AIToolResult.Fail("必须指定有效的 source_system_type_id"));
                }

                if (string.IsNullOrWhiteSpace(newName))
                {
                    return Task.FromResult(AIToolResult.Fail("必须指定新系统名称 (new_system_name)"));
                }

                var duplicate = service.DuplicateSystem(document, sourceSystemTypeId.Value, newName!);
                return Task.FromResult(duplicate is null
                    ? AIToolResult.Fail("复制系统类型失败")
                    : AIToolResult.Ok(
                        $"成功复制系统类型到 '{duplicate.Name}'（ID: {duplicate.SystemId}）",
                        new { operation, sourceSystemTypeId = sourceSystemTypeId.Value, systemTypeId = duplicate.SystemId, newName = duplicate.Name }));
            }

            case "create":
            {
                if (systemTypeId is null)
                {
                    return Task.FromResult(AIToolResult.Fail("create 需要 system_type_id。"));
                }

                var system = service.CreateSystem(document, systemTypeId.Value, newName);
                return Task.FromResult(system is null
                    ? AIToolResult.Fail("创建系统失败。")
                    : AIToolResult.Ok(
                        $"✅ 已创建系统 {system.SystemId}（{system.Name}，类型 {system.SystemTypeName}）",
                        new { systemId = system.SystemId, name = system.Name, systemTypeName = system.SystemTypeName, kind = system.Kind }));
            }

            case "rename":
            {
                if (systemTypeId is null)
                {
                    return Task.FromResult(AIToolResult.Fail("rename 需要 system_type_id 作为目标系统 id（或改用 mep_system_query 查回 id）。"));
                }

                if (string.IsNullOrWhiteSpace(newName))
                {
                    return Task.FromResult(AIToolResult.Fail("rename 需要 new_name。"));
                }

                var changed = service.RenameSystem(document, systemTypeId.Value, newName!);
                return Task.FromResult(AIToolResult.Ok($"成功将系统类型 {systemTypeId} 重命名为 {newName}", new { operation, systemId = systemTypeId.Value, newName, changed }));
            }

            case "delete":
            case "remove":
            {
                if (systemTypeId is null)
                {
                    return Task.FromResult(AIToolResult.Fail("delete 需要 system_type_id 作为目标系统 id。"));
                }

                var deleted = service.DeleteSystem(document, systemTypeId.Value, force);
                return Task.FromResult(deleted
                    ? AIToolResult.Ok($"成功删除系统类型 '{systemTypeId}'", new { operation, systemId = systemTypeId.Value, deleted = true, force })
                    : AIToolResult.Fail($"系统类型 {systemTypeId} 未被删除。"));
            }

            default:
                return Task.FromResult(AIToolResult.Fail(
                    "不支持的操作类型: " + operationText + "（支持 duplicate / rename / delete）"));
        }
    }
}

// 结构分析对照：Revit\AS\Tools\Revit\Net8\AI\Tools\MepSystemQueryTool.cs（管网）
internal sealed class MepSystemQueryImplementation : IToolImplementation
{
    public Task<AIToolResult> ExecuteAsync(IRevitAdapter adapter, AIToolContext context, CancellationToken cancellationToken = default)
    {
        var service = adapter.MepSystemService ?? throw new InvalidOperationException("无法获取 MepSystemService");
        var document = DomainArgs.RequireDocument(adapter, context);

        var operationText = WriteArgs.Text(context, "operation");
        if (string.IsNullOrWhiteSpace(operationText))
        {
            return Task.FromResult(AIToolResult.Fail("必须指定 operation 参数"));
        }

        var operation = operationText.Trim().ToLowerInvariant();
        var supported = operation is
            "list_systems" or "system_statistics" or "category_breakdown" or "size_distribution"
            or "insulation_status" or "system_details" or "check_usage" or "get_properties" or "get_elements";
        if (!supported)
        {
            return Task.FromResult(AIToolResult.Fail("不支持的操作类型: " + operationText));
        }

        var target = (WriteArgs.Text(context, "target") ?? "all").Trim().ToLowerInvariant();
        if (target is not ("pipe" or "duct" or "all"))
        {
            return Task.FromResult(AIToolResult.Fail("target 只支持 pipe / duct / all"));
        }

        if (operation is "category_breakdown" or "size_distribution" or "get_elements" && target == "all")
        {
            return Task.FromResult(AIToolResult.Fail(
                $"{operation} 操作必须指定 target 为 pipe 或 duct（不支持 all）"));
        }

        var systemTypeId = WriteArgs.OptionalInt(context, "system_type_id", "systemTypeId");
        if (operation is "category_breakdown" or "size_distribution" or "system_details"
            or "get_properties" or "get_elements" && systemTypeId is null)
        {
            return Task.FromResult(AIToolResult.Fail($"{operation} 操作需要指定 system_type_id 参数"));
        }

        var onlyUninsulated = WriteArgs.Flag(context, false, "only_uninsulated", "onlyUninsulated");
        var includeSizes = WriteArgs.Flag(context, false, "include_size_distribution", "includeSizeDistribution");
        var includeBreakdown = WriteArgs.Flag(context, false, "include_category_breakdown", "includeCategoryBreakdown");
        var includeFittings = WriteArgs.Flag(context, true, "include_fittings", "includeFittings");

        var result = service.QueryMepSystems(
            document,
            operation,
            target,
            systemTypeId,
            onlyUninsulated,
            includeSizes,
            includeBreakdown,
            includeFittings,
            (int)WriteArgs.Number(context, operation == "get_elements" ? 100 : 200, "limit"));

        string message;
        switch (operation)
        {
            case "list_systems":
                message = $"找到 {result.TotalCount} 个系统类型（水管系统: {result.Systems.Count(system => system.ElementKind == "水管")}，风管系统: {result.Systems.Count(system => system.ElementKind == "风管")}）";
                break;
            case "system_statistics":
                message = $"成功获取 {result.Systems.Count} 个系统的统计信息";
                break;
            case "category_breakdown":
                message = "成功获取" + (target == "pipe" ? "水管" : "风管") + "系统的类别分布";
                break;
            case "size_distribution":
                message = "成功获取" + (target == "pipe" ? "水管" : "风管") + "系统的尺寸分布";
                break;
            case "insulation_status":
                message = $"成功获取 {result.TotalCount} 个系统的保温状态，其中 {result.UnusedCount} 个系统未保温";
                break;
            case "system_details":
                message = $"成功获取系统 '{result.Systems.FirstOrDefault()?.Name ?? systemTypeId?.ToString()}' 的详细信息";
                break;
            case "check_usage":
                message = $"成功检查 {result.TotalCount} 个系统的使用情况，其中 {result.UnusedCount} 个未使用";
                break;
            case "get_properties":
                message = $"成功获取系统 '{result.Systems.FirstOrDefault()?.Name ?? systemTypeId?.ToString()}' 的属性";
                break;
            case "get_elements":
                message = $"成功获取系统 '{result.Systems.FirstOrDefault()?.Name ?? systemTypeId?.ToString()}' 的 {result.ElementIds.Count} 个元素";
                break;
            default:
                message = "MEP 系统查询完成";
                break;
        }

        string? cacheId = null;
        if (operation == "get_elements" && context.DataCache is not null && result.ElementIds.Count > 0)
        {
            cacheId = context.DataCache.Store(
                context.SessionId ?? "default",
                "mep_system_elements",
                result.ElementIds.Select(id => new Dictionary<string, object?> { ["elementId"] = id }).ToList());
            message += $"\n\n💡 在后续工具调用中使用 cacheId=\"{cacheId}\" 参数来操作这些元素";
        }

        var data = new
        {
            operation,
            target,
            system_count = result.TotalCount,
            in_use_count = result.InUseCount,
            unused_count = result.UnusedCount,
            systems = result.Systems.Select(system => new
            {
                system_type_id = system.SystemTypeId,
                name = system.Name,
                classification = system.Classification,
                element_kind = system.ElementKind,
                element_count = system.ElementCount,
                insulated_count = system.InsulatedCount,
                uninsulated_count = system.UninsulatedCount,
                is_in_use = system.IsInUse,
                can_be_deleted = system.CanBeDeleted,
                can_be_renamed = system.CanBeRenamed,
                can_be_copied = system.CanBeCopied,
                coverage_rate = system.CoverageRate,
            }).ToArray(),
            element_ids = result.ElementIds,
            breakdown = result.Breakdown,
            size_distribution = result.SizeDistribution,
            properties = result.Properties,
            include_fittings = includeFittings,
            cache_id = cacheId,
            message = result.Message,
        };

        return Task.FromResult(string.IsNullOrEmpty(cacheId)
            ? AIToolResult.Ok(message, data)
            : AIToolResult.OkWithCache(message, cacheId, data));
    }
}
