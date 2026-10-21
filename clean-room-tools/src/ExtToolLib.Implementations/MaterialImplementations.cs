using RevitAi.Engine.Abstractions;
using RevitAi.Engine.Abstractions.Adapters;
using RevitAi.Engine.Abstractions.Services;
using RevitAi.Engine.Core;

namespace ExtToolLib.Implementations;

/// <summary>
/// 批次 3 第二刀实现：材料查询/管理、复合层结构读写。
/// </summary>
public static class MaterialImplementations
{
    public static void RegisterAll()
    {
        ToolImplementationRegistry.Register("material_query", new MaterialQueryImplementation());
        ToolImplementationRegistry.Register("material_manager", new MaterialManagerImplementation());
        ToolImplementationRegistry.Register("get_compound_structure_layers", new GetCompoundStructureLayersImplementation());
        ToolImplementationRegistry.Register("add_compound_structure_layer", new AddCompoundStructureLayerImplementation());
        ToolImplementationRegistry.Register("delete_compound_structure_layer", new DeleteCompoundStructureLayerImplementation());
        ToolImplementationRegistry.Register("modify_compound_structure_layer", new ModifyCompoundStructureLayerImplementation());
    }
}

internal static class MaterialArgs
{
    public static object RequireDocument(IRevitAdapter adapter, AIToolContext context)
        => context.Document ?? adapter.GetActiveDocument() ?? throw new InvalidOperationException("文档对象为空");

    public static string? HexFromRgb(AIToolContext context)
    {
        if (!context.Parameters.ContainsKey("red") && !context.Parameters.ContainsKey("green") && !context.Parameters.ContainsKey("blue"))
        {
            return WriteArgs.Text(context, "hexColor", "hex_color", "color");
        }

        var red = Math.Clamp((int)WriteArgs.Number(context, 0, "red"), 0, 255);
        var green = Math.Clamp((int)WriteArgs.Number(context, 0, "green"), 0, 255);
        var blue = Math.Clamp((int)WriteArgs.Number(context, 0, "blue"), 0, 255);
        return $"#{red:X2}{green:X2}{blue:X2}";
    }

    public static int? OptionalInt(AIToolContext context, params string[] keys) => WriteArgs.OptionalInt(context, keys);

    public static List<int> ResolveElementIds(AIToolContext context)
    {
        var ids = WriteArgs.Ids(context, "elementIds", "element_ids", "elementId", "element_id");
        if (ids.Count > 0)
        {
            return ids;
        }

        var cacheId = WriteArgs.Text(context, "cacheId", "cache_id");
        if (string.IsNullOrWhiteSpace(cacheId))
        {
            return ids;
        }

        if (context.GetCachedData<List<Dictionary<string, object?>>>(cacheId) is { } cached)
        {
            ids.AddRange(ToolArgumentReader.ExtractIds(cached));
        }
        else if (context.GetCachedData<List<int>>(cacheId) is { } cachedIds)
        {
            ids.AddRange(cachedIds);
        }

        return ids.Distinct().ToList();
    }
}

// 结构分析对照：Revit\AS\Tools\Revit\Net8\AI\Tools\MaterialQueryTool.cs（材料管理）
internal sealed class MaterialQueryImplementation : IToolImplementation
{
    public Task<AIToolResult> ExecuteAsync(IRevitAdapter adapter, AIToolContext context, CancellationToken cancellationToken = default)
    {
        var service = adapter.MaterialService ?? throw new InvalidOperationException("无法获取 MaterialService");
        var document = MaterialArgs.RequireDocument(adapter, context);

        var operationText = WriteArgs.Text(context, "operation");
        if (string.IsNullOrWhiteSpace(operationText))
        {
            return Task.FromResult(AIToolResult.Fail("必须指定 operation 参数"));
        }

        var operation = operationText.Trim().ToLowerInvariant();
        var filter = WriteArgs.Text(context, "filter", "materialName", "filterText");
        var limit = (int)WriteArgs.Number(context, 200, "limit");

        switch (operation)
        {
            case "get_all":
            {
                var materials = service.Query(document, filter, limit);
                return Task.FromResult(CreateMaterialsResult(context, operation, filter, materials));
            }

            case "get_by_name":
            {
                var materialName = WriteArgs.Text(context, "materialName", "material_name");
                if (string.IsNullOrWhiteSpace(materialName))
                {
                    return Task.FromResult(AIToolResult.Fail("get_by_name 操作需要指定 materialName 参数"));
                }

                var material = service.Get(document, null, materialName);
                return Task.FromResult(material is null
                    ? AIToolResult.Fail($"找不到名称为 '{materialName}' 的材质")
                    : AIToolResult.Ok($"找到材质: {material.Name}", new
                    {
                        operation,
                        materialId = material.MaterialId,
                        materialName = material.Name,
                        materialClass = material.Class,
                        color = material.ColorHex,
                        transparency = material.Transparency,
                    }));
            }

            case "get_element_materials":
            {
                var ids = MaterialArgs.ResolveElementIds(context);
                if (ids.Count == 0)
                {
                    return Task.FromResult(AIToolResult.Fail(
                        "get_element_materials 操作需要提供 elementId、elementIds 或 cacheId 参数"));
                }

                var records = service.GetElementMaterials(document, ids);
                var cacheId = string.Empty;
                if (context.DataCache is not null && records.Count > 0)
                {
                    cacheId = context.DataCache.Store(
                        context.SessionId ?? "default",
                        "element_materials",
                        records);
                }

                var message = $"✅ 查询了 {records.Count(r => r.Found)} 个元素的材质"
                              + (cacheId.Length > 0 ? $"\n\n💡 在后续工具调用中使用 cacheId=\"{cacheId}\"" : string.Empty);
                var data = new
                {
                    operation,
                    elements = records,
                    count = records.Count,
                    cache_id = cacheId.Length == 0 ? null : cacheId,
                };
                return Task.FromResult(cacheId.Length == 0
                    ? AIToolResult.Ok(message, data)
                    : AIToolResult.OkWithCache(message, cacheId, data));
            }

            default:
                return Task.FromResult(AIToolResult.Fail("不支持的操作类型: " + operationText));
        }
    }

    private static AIToolResult CreateMaterialsResult(
        AIToolContext context,
        string operation,
        string? filter,
        IReadOnlyList<MaterialInfo> materials)
    {
        var cacheId = string.Empty;
        if (context.DataCache is not null && materials.Count > 0)
        {
            cacheId = context.DataCache.Store(context.SessionId ?? "default", "materials", materials);
        }

        var message = $"✅ 找到 {materials.Count} 个材料"
                      + (string.IsNullOrWhiteSpace(filter) ? string.Empty : $"（过滤：{filter}）")
                      + (cacheId.Length > 0 ? $"\n\n💡 在后续工具调用中使用 cacheId=\"{cacheId}\" 参数来操作这些材料" : string.Empty);
        var data = new
        {
            operation,
            filter,
            count = materials.Count,
            materials = materials.Select(material => new
            {
                materialId = material.MaterialId,
                name = material.Name,
                @class = material.Class,
                color = material.ColorHex,
                transparency = material.Transparency,
            }).ToArray(),
            cache_id = cacheId.Length == 0 ? null : cacheId,
        };

        return cacheId.Length == 0
            ? AIToolResult.Ok(message, data)
            : AIToolResult.OkWithCache(message, cacheId, data);
    }
}

// 结构分析对照：Revit\AS\Tools\Revit\Net8\AI\Tools\MaterialManagerTool.cs（材料管理）
internal sealed class MaterialManagerImplementation : IToolImplementation
{
    public Task<AIToolResult> ExecuteAsync(IRevitAdapter adapter, AIToolContext context, CancellationToken cancellationToken = default)
    {
        var service = adapter.MaterialService ?? throw new InvalidOperationException("无法获取 MaterialService");
        var document = MaterialArgs.RequireDocument(adapter, context);

        var operationText = WriteArgs.Text(context, "operation");
        if (string.IsNullOrWhiteSpace(operationText))
        {
            return Task.FromResult(AIToolResult.Fail("必须指定 operation 参数"));
        }

        var operation = operationText.Trim().ToLowerInvariant();
        var name = WriteArgs.Text(context, "materialName", "material_name", "name");
        var materialId = MaterialArgs.OptionalInt(context, "materialId", "material_id");
        var colorHex = MaterialArgs.HexFromRgb(context);
        var transparency = MaterialArgs.OptionalInt(context, "transparency");
        var force = WriteArgs.Flag(context, false, "force");

        switch (operation)
        {
            case "create":
            {
                if (string.IsNullOrWhiteSpace(name))
                {
                    return Task.FromResult(AIToolResult.Fail("create 需要 materialName。"));
                }

                var material = service.Create(document, name!, colorHex, transparency, force);
                return Task.FromResult(AIToolResult.Ok(
                    $"✅ 材料 {material.Name} 已就绪（id={material.MaterialId}，颜色 {material.ColorHex}，透明度 {material.Transparency}）",
                    new { materialId = material.MaterialId, name = material.Name, color = material.ColorHex, transparency = material.Transparency }));
            }

            case "set_color":
            {
                if (string.IsNullOrWhiteSpace(name))
                {
                    return Task.FromResult(AIToolResult.Fail("set_color 操作需要指定 materialName 参数"));
                }

                var red = WriteArgs.Number(context, -1, "red");
                var green = WriteArgs.Number(context, -1, "green");
                var blue = WriteArgs.Number(context, -1, "blue");
                if (red is < 0 or > 255 || green is < 0 or > 255 || blue is < 0 or > 255)
                {
                    return Task.FromResult(AIToolResult.Fail("RGB值必须在 0-255 范围内"));
                }

                var material = service.Update(document, materialId, name, colorHex, null);
                return Task.FromResult(material is null
                    ? AIToolResult.Fail($"找不到名称为 '{name}' 的材质")
                    : AIToolResult.Ok(
                        $"成功将材质 '{material.Name}' 的颜色设置为 RGB({red:F0}, {green:F0}, {blue:F0})",
                        new { operation, materialId = material.MaterialId, materialName = material.Name, red = (int)red, green = (int)green, blue = (int)blue }));
            }

            case "set_transparency":
            {
                if (string.IsNullOrWhiteSpace(name))
                {
                    return Task.FromResult(AIToolResult.Fail("set_transparency 操作需要指定 materialName 参数"));
                }

                if (transparency is null or < 0 or > 100)
                {
                    return Task.FromResult(AIToolResult.Fail("透明度值必须在 0-100 范围内"));
                }

                var material = service.Update(document, materialId, name, null, transparency);
                return Task.FromResult(material is null
                    ? AIToolResult.Fail($"找不到名称为 '{name}' 的材质")
                    : AIToolResult.Ok(
                        $"成功将材质 '{material.Name}' 的透明度设置为 {material.Transparency}%",
                        new { operation, materialId = material.MaterialId, materialName = material.Name, transparency = material.Transparency }));
            }

            case "delete":
            {
                if (materialId is null or <= 0)
                {
                    return Task.FromResult(AIToolResult.Fail("delete 操作需要指定有效的 materialId 参数"));
                }

                var deleted = service.Delete(document, materialId, name, force);
                return Task.FromResult(deleted
                    ? AIToolResult.Ok($"成功删除材质: {name ?? materialId.Value.ToString()}", new { operation, materialId = materialId.Value, deleted = true, force })
                    : AIToolResult.Fail("未找到要删除的材料。"));
            }

            default:
                return Task.FromResult(AIToolResult.Fail(
                    "不支持的操作类型: " + operationText + "（支持 create / delete / set_color / set_transparency）"));
        }
    }
}

// 结构分析对照：Revit\AS\Tools\Revit\Net8\AI\Tools\GetCompoundStructureLayersTool.cs（复合层结构）
internal sealed class GetCompoundStructureLayersImplementation : IToolImplementation
{
    public Task<AIToolResult> ExecuteAsync(IRevitAdapter adapter, AIToolContext context, CancellationToken cancellationToken = default)
    {
        var service = adapter.CompoundStructureService ?? throw new InvalidOperationException("无法获取 CompoundStructureService");
        var document = MaterialArgs.RequireDocument(adapter, context);

        var typeId = MaterialArgs.OptionalInt(context, "elementTypeId", "element_type_id");
        var typeName = WriteArgs.Text(context, "elementTypeName", "element_type_name", "typeName");
        if (typeId is null && string.IsNullOrWhiteSpace(typeName))
        {
            return Task.FromResult(AIToolResult.Fail("必须提供 elementTypeId 或 elementTypeName。"));
        }

        var layers = service.GetLayers(document, typeId, typeName);
        var totalMm = Math.Round(layers.Sum(layer => layer.WidthMm), 2);

        return Task.FromResult(AIToolResult.Ok(
            $"✅ 共 {layers.Count} 层，总厚度 {totalMm} mm",
            new
            {
                layers = layers.Select(layer => new
                {
                    index = layer.Index,
                    function = layer.Function,
                    thicknessMM = layer.WidthMm,
                    materialId = layer.MaterialId,
                    materialName = layer.MaterialName,
                    isStructural = layer.IsStructuralMaterial,
                }).ToArray(),
                count = layers.Count,
                totalThicknessMM = totalMm,
            }));
    }
}

// 结构分析对照：Revit\AS\Tools\Revit\Net8\AI\Tools\AddCompoundStructureLayerTool.cs
internal sealed class AddCompoundStructureLayerImplementation : IToolImplementation
{
    public Task<AIToolResult> ExecuteAsync(IRevitAdapter adapter, AIToolContext context, CancellationToken cancellationToken = default)
    {
        var service = adapter.CompoundStructureService ?? throw new InvalidOperationException("无法获取 CompoundStructureService");
        var document = MaterialArgs.RequireDocument(adapter, context);

        var typeId = MaterialArgs.OptionalInt(context, "elementTypeId", "element_type_id");
        var typeName = WriteArgs.Text(context, "elementTypeName", "element_type_name", "typeName");
        if (typeId is null && string.IsNullOrWhiteSpace(typeName))
        {
            return Task.FromResult(AIToolResult.Fail("必须提供 elementTypeId 或 elementTypeName。"));
        }

        var thickness = WriteArgs.Number(context, 0, "thicknessMM", "thickness_mm", "thickness");
        var function = WriteArgs.Text(context, "function") ?? "Finish1";
        var materialId = MaterialArgs.OptionalInt(context, "materialId", "material_id");
        var materialName = WriteArgs.Text(context, "materialName", "material_name");
        var insertIndex = MaterialArgs.OptionalInt(context, "insertIndex", "insert_index");

        var layerCount = service.AddLayer(document, typeId, typeName, thickness, function, materialId, materialName, insertIndex);
        return Task.FromResult(AIToolResult.Ok(
            $"✅ 已插入复合层（{function}，{thickness} mm），当前共 {layerCount} 层",
            new { layerCount, function, thicknessMM = thickness, insertIndex }));
    }
}

// 结构分析对照：Revit\AS\Tools\Revit\Net8\AI\Tools\DeleteCompoundStructureLayerTool.cs
internal sealed class DeleteCompoundStructureLayerImplementation : IToolImplementation
{
    public Task<AIToolResult> ExecuteAsync(IRevitAdapter adapter, AIToolContext context, CancellationToken cancellationToken = default)
    {
        var service = adapter.CompoundStructureService ?? throw new InvalidOperationException("无法获取 CompoundStructureService");
        var document = MaterialArgs.RequireDocument(adapter, context);

        var typeId = MaterialArgs.OptionalInt(context, "elementTypeId", "element_type_id");
        var typeName = WriteArgs.Text(context, "elementTypeName", "element_type_name", "typeName");
        var layerIndex = MaterialArgs.OptionalInt(context, "layerIndex", "layer_index");
        if (layerIndex is null)
        {
            return Task.FromResult(AIToolResult.Fail("必须提供 layerIndex。"));
        }

        var deleted = service.DeleteLayer(document, typeId, typeName, layerIndex.Value);
        return Task.FromResult(deleted
            ? AIToolResult.Ok($"✅ 已删除第 {layerIndex} 层", new { layerIndex, deleted = true })
            : AIToolResult.Fail($"未能删除第 {layerIndex} 层。"));
    }
}

// 结构分析对照：Revit\AS\Tools\Revit\Net8\AI\Tools\ModifyCompoundStructureLayerTool.cs
internal sealed class ModifyCompoundStructureLayerImplementation : IToolImplementation
{
    public Task<AIToolResult> ExecuteAsync(IRevitAdapter adapter, AIToolContext context, CancellationToken cancellationToken = default)
    {
        var service = adapter.CompoundStructureService ?? throw new InvalidOperationException("无法获取 CompoundStructureService");
        var document = MaterialArgs.RequireDocument(adapter, context);

        var typeId = MaterialArgs.OptionalInt(context, "elementTypeId", "element_type_id");
        var typeName = WriteArgs.Text(context, "elementTypeName", "element_type_name", "typeName");
        var layerIndex = MaterialArgs.OptionalInt(context, "layerIndex", "layer_index");
        if (layerIndex is null)
        {
            return Task.FromResult(AIToolResult.Fail("必须提供 layerIndex。"));
        }

        double? thickness = null;
        if (context.Parameters.ContainsKey("thicknessMM") || context.Parameters.ContainsKey("thickness"))
        {
            thickness = WriteArgs.Number(context, 0, "thicknessMM", "thickness_mm", "thickness");
        }

        var function = WriteArgs.Text(context, "function");
        var materialId = MaterialArgs.OptionalInt(context, "materialId", "material_id");
        var materialName = WriteArgs.Text(context, "materialName", "material_name");

        var modified = service.ModifyLayer(document, typeId, typeName, layerIndex.Value, thickness, function, materialId, materialName);
        return Task.FromResult(modified
            ? AIToolResult.Ok(
                $"✅ 已修改第 {layerIndex} 层" + (thickness is not null ? $"（厚度 {thickness} mm）" : string.Empty),
                new { layerIndex, thicknessMM = thickness, function, materialId, materialName })
            : AIToolResult.Fail($"未能修改第 {layerIndex} 层。"));
    }
}
