using RevitAi.Engine.Abstractions;
using RevitAi.Engine.Abstractions.Adapters;
using RevitAi.Engine.Abstractions.Services;
using RevitAi.Engine.Core;

namespace ExtToolLib.Implementations;

/// <summary>
/// 批次 2 剩余实现：过滤器创建、标记、明细表创建与 CSV 导出、视图导出（图片/DWG）。
/// 导出类工具按工具契约是"免事务"（tx=False），但仍需回主线程（RequiresActiveDocument=true）。
/// </summary>
public static class WriteImplementations2
{
    public static void RegisterAll()
    {
        ToolImplementationRegistry.Register("create_view_filter", new CreateViewFilterImplementation());
        ToolImplementationRegistry.Register("create_tag", new CreateTagImplementation());
        ToolImplementationRegistry.Register("create_schedule", new CreateScheduleImplementation());
        ToolImplementationRegistry.Register("export_view_images", new ExportViewImagesImplementation());
        ToolImplementationRegistry.Register("export_views_as_dwg", new ExportViewsAsDwgImplementation());
        ToolImplementationRegistry.Register("export_schedule_to_csv", new ExportScheduleToCsvImplementation());
    }
}

internal static class ExportArgs
{
    public static object RequireDocument(IRevitAdapter adapter, AIToolContext context)
        => context.Document ?? adapter.GetActiveDocument() ?? throw new InvalidOperationException("文档对象为空");

    /// <summary>viewIds 优先；其次 cacheId；都没有则返回空列表（由服务回退到活动视图）。</summary>
    public static List<int> ResolveViewIds(AIToolContext context)
    {
        var ids = WriteArgs.Ids(context, "viewIds", "view_ids");
        if (ids.Count > 0)
        {
            return ids;
        }

        var cacheId = WriteArgs.Text(context, "cacheId", "cache_id");
        if (string.IsNullOrWhiteSpace(cacheId))
        {
            return ids;
        }

        var cached = context.GetCachedData<List<Dictionary<string, object?>>>(cacheId);
        if (cached is not null)
        {
            ids.AddRange(ToolArgumentReader.ExtractIds(cached));
        }
        else
        {
            var cachedIds = context.GetCachedData<List<int>>(cacheId);
            if (cachedIds is not null)
            {
                ids.AddRange(cachedIds);
            }
        }

        return ids;
    }

    public static int? ParseParameterId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var id)
            ? id
            : null;
    }

    public static List<int> ReadParameterIds(AIToolContext context, params string[] keys)
    {
        var parameters = new Dictionary<string, object?>(context.Parameters, StringComparer.OrdinalIgnoreCase);
        var texts = keys
            .SelectMany(key => ToolArgumentReader.ReadStrings(parameters, key))
            .ToList();
        return texts
            .Select(ParseParameterId)
            .Where(id => id is not null)
            .Select(id => id!.Value)
            .Distinct()
            .ToList();
    }
}

internal sealed class CreateViewFilterImplementation : IToolImplementation
{
    public Task<AIToolResult> ExecuteAsync(IRevitAdapter adapter, AIToolContext context, CancellationToken cancellationToken = default)
    {
        var service = adapter.FilterCreationService ?? throw new InvalidOperationException("无法获取 FilterCreationService");
        var document = ExportArgs.RequireDocument(adapter, context);

        var filterName = WriteArgs.Text(context, "filterName", "filter_name")
                         ?? throw new InvalidOperationException("必须提供 filterName。");

        var categoryNames = ToolArgumentReader.ReadStrings(
            new Dictionary<string, object?>(context.Parameters, StringComparer.OrdinalIgnoreCase), "categoryNames");
        var categoryName = categoryNames.FirstOrDefault() ?? WriteArgs.Text(context, "categoryName", "category");
        if (string.IsNullOrWhiteSpace(categoryName))
        {
            return Task.FromResult(AIToolResult.Fail("必须提供 categoryNames（至少一个类别）。"));
        }

        var (parameterName, value, rule) = ReadRule(context);
        if (string.IsNullOrWhiteSpace(parameterName))
        {
            return Task.FromResult(AIToolResult.Fail("rules 里必须提供 parameterName（或直接用 parameterName/value 简写）。"));
        }

        var filterId = service.CreateFilter(document, filterName, categoryName!, parameterName!, value, rule);
        return Task.FromResult(AIToolResult.Ok(
            $"✅ 已创建过滤器 {filterId}：{filterName}（类别 {categoryName}，参数 {parameterName}，规则 {rule ?? "equals"}）",
            new { filterId, filterName, categoryName, parameterName, value, rule = rule ?? "equals" }));
    }

    private static (string? ParameterName, string? Value, string? Rule) ReadRule(AIToolContext context)
    {
        // 简写：parameterName + value + rule
        var shorthandName = WriteArgs.Text(context, "parameterName", "parameter_name");
        if (!string.IsNullOrWhiteSpace(shorthandName))
        {
            return (shorthandName, WriteArgs.Text(context, "value"), WriteArgs.Text(context, "rule", "ruleType"));
        }

        // 标准：rules: [{ parameterName, ruleType, value }]
        if (context.Parameters.TryGetValue("rules", out var rules) && rules is List<object?> list && list.Count > 0)
        {
            if (list[0] is Dictionary<string, object?> rule)
            {
                string? Get(params string[] keys)
                {
                    foreach (var key in keys)
                    {
                        if (rule.TryGetValue(key, out var v) && v is not null && !string.IsNullOrWhiteSpace(v.ToString()))
                        {
                            return v.ToString();
                        }
                    }

                    return null;
                }

                return (Get("parameterName", "parameter", "parameter_name"), Get("value"), Get("ruleType", "rule", "comparison"));
            }
        }

        return (null, null, null);
    }
}

internal sealed class CreateTagImplementation : IToolImplementation
{
    public Task<AIToolResult> ExecuteAsync(IRevitAdapter adapter, AIToolContext context, CancellationToken cancellationToken = default)
    {
        var service = adapter.TagCreationService ?? throw new InvalidOperationException("无法获取 TagCreationService");
        var document = ExportArgs.RequireDocument(adapter, context);

        var elementId = WriteArgs.OptionalInt(context, "elementId", "element_id");
        var batchMode = WriteArgs.Flag(context, false, "batchMode", "batch_mode")
                        || context.Parameters.ContainsKey("categoryNames")
                        || elementId is null;
        if (batchMode)
        {
            return Task.FromResult(CreateBatchTags(adapter, context, document));
        }

        if (elementId is null || elementId <= 0)
        {
            return Task.FromResult(AIToolResult.Fail("单个模式必须提供 elementId 参数"));
        }

        if (!context.Parameters.ContainsKey("position"))
        {
            return Task.FromResult(AIToolResult.Fail("单个模式必须提供 position 参数（包含 x/y 坐标）"));
        }

        var position = WriteArgs.Point(context, (0, 0, 0), "position", "location");
        var tagId = service.CreateTag(
            document,
            elementId.Value,
            WriteArgs.OptionalInt(context, "tagTypeId", "tag_type_id"),
            WriteArgs.OptionalInt(context, "viewId", "view_id"),
            position.X,
            position.Y,
            WriteArgs.Text(context, "orientation"),
            WriteArgs.Flag(context, false, "addLeader", "add_leader"));

        return Task.FromResult(AIToolResult.Ok(
            $"✅ 已为元素 {elementId} 创建标记 {tagId}",
            new { tagId, elementId, position = new { x = position.X, y = position.Y, unit = "millimeters" } }));
    }

    private static AIToolResult CreateBatchTags(IRevitAdapter adapter, AIToolContext context, object document)
    {
        var tagService = adapter.TagCreationService
                         ?? throw new InvalidOperationException("无法获取 TagCreationService");
        var queryService = adapter.AdvancedQueryService
                           ?? throw new InvalidOperationException("无法获取 AdvancedQueryService");
        var viewService = adapter.ViewService
                          ?? throw new InvalidOperationException("无法获取 ViewService");
        var tagQueryService = adapter.TagService
                              ?? throw new InvalidOperationException("无法获取 TagService");

        var viewId = WriteArgs.OptionalInt(context, "viewId", "view_id");
        var viewName = WriteArgs.Text(context, "viewName", "view_name");
        if (viewId is null && !string.IsNullOrWhiteSpace(viewName))
        {
            viewId = viewService.GetAllViews(document)
                .FirstOrDefault(view => string.Equals(view.Name, viewName, StringComparison.OrdinalIgnoreCase))
                ?.Id;
        }

        viewId ??= viewService.GetActiveView(document)?.Id;
        if (viewId is null)
        {
            return AIToolResult.Fail("无法确定批量标记的目标视图，请提供 viewId 或 viewName。");
        }

        var categoryNames = ToolArgumentReader.ReadStrings(
            new Dictionary<string, object?>(context.Parameters, StringComparer.OrdinalIgnoreCase),
            "categoryNames");
        categoryNames = categoryNames
            .SelectMany(name => name.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var candidates = new List<ElementSummary>();
        if (categoryNames.Count == 0)
        {
            candidates.AddRange(queryService.QueryElements(document, "all", Array.Empty<int>(), null, null, null, 5000));
        }
        else
        {
            foreach (var categoryName in categoryNames)
            {
                candidates.AddRange(queryService.QueryElements(
                    document,
                    "by_category",
                    Array.Empty<int>(),
                    categoryName,
                    null,
                    null,
                    5000));
            }
        }

        candidates = candidates.GroupBy(element => element.ElementId).Select(group => group.First()).ToList();
        var includeTagged = WriteArgs.Flag(context, false, "includeTagged", "include_tagged");
        var taggedIds = includeTagged
            ? new HashSet<int>()
            : tagQueryService.GetAllTags(document)
                .Where(tag => tag.TaggedElementId is not null)
                .Select(tag => tag.TaggedElementId!.Value)
                .ToHashSet();
        var targets = candidates.Where(element => !taggedIds.Contains(element.ElementId)).ToList();
        var created = new List<object>();
        var failures = new List<string>();

        foreach (var target in targets)
        {
            try
            {
                var tagId = tagService.CreateTag(
                    document,
                    target.ElementId,
                    WriteArgs.OptionalInt(context, "tagTypeId", "tag_type_id"),
                    viewId,
                    target.X ?? 0,
                    target.Y ?? 0,
                    null,
                    WriteArgs.Flag(context, false, "addLeader", "add_leader"));
                created.Add(new { tagId, target.ElementId, target.CategoryName });
            }
            catch (Exception ex)
            {
                failures.Add($"{target.ElementId}: {ex.GetBaseException().Message}");
            }
        }

        return AIToolResult.Ok(
            $"批量标记完成：成功 {created.Count}/{candidates.Count}，跳过已标记 {candidates.Count - targets.Count}，失败 {failures.Count}",
            new
            {
                mode = "batch",
                viewId,
                categories = categoryNames,
                includeTagged,
                candidateCount = candidates.Count,
                createdCount = created.Count,
                skippedCount = candidates.Count - targets.Count,
                failedCount = failures.Count,
                created,
                failures = failures.Count == 0 ? null : failures,
            });
    }
}

internal sealed class CreateScheduleImplementation : IToolImplementation
{
    public Task<AIToolResult> ExecuteAsync(IRevitAdapter adapter, AIToolContext context, CancellationToken cancellationToken = default)
    {
        var service = adapter.ScheduleService ?? throw new InvalidOperationException("无法获取 ScheduleService");
        var document = ExportArgs.RequireDocument(adapter, context);

        var category = WriteArgs.Text(context, "category", "categoryName")
                       ?? throw new InvalidOperationException("必须提供 category（类别名）。");
        var name = WriteArgs.Text(context, "scheduleName", "name");

        var fields = ToolArgumentReader.ReadStrings(
            new Dictionary<string, object?>(context.Parameters, StringComparer.OrdinalIgnoreCase), "fields");
        var fieldParameterIds = ExportArgs.ReadParameterIds(context, "fieldUniqueIds", "field_unique_ids");
        if (fields.Count == 0 &&
            (context.Parameters.ContainsKey("fieldUniqueIds") || context.Parameters.ContainsKey("field_unique_ids")) &&
            fieldParameterIds.Count == 0)
        {
            return Task.FromResult(AIToolResult.Fail(
                "fieldUniqueIds 必须是 get_schedule_fields 返回的数字 parameterId/uniqueId 字符串"));
        }

        var sortBy = ExportArgs.ParseParameterId(WriteArgs.Text(context, "sortByUniqueId", "sort_by_unique_id"));
        var filterField = ExportArgs.ParseParameterId(WriteArgs.Text(context, "filterFieldUniqueId", "filter_field_unique_id"));
        var groupBy = ExportArgs.ParseParameterId(WriteArgs.Text(context, "groupByUniqueId", "group_by_unique_id"));
        var sortOrder = WriteArgs.Text(context, "sortOrder", "sort_order") ?? "ascending";
        var filterType = WriteArgs.Text(context, "filterType", "filter_type") ?? "contains";
        var filterValue = WriteArgs.Text(context, "filterValue", "filter_value");

        var scheduleId = service.CreateSchedule(
            document,
            category,
            name,
            fields,
            fieldParameterIds,
            sortBy,
            sortOrder,
            filterField,
            filterType,
            filterValue,
            groupBy);
        return Task.FromResult(AIToolResult.Ok(
            $"✅ 已创建明细表 {scheduleId}" + (fields.Count > 0 ? $"（{fields.Count} 个字段）" : string.Empty),
            new
            {
                scheduleId,
                category,
                name,
                fields,
                fieldParameterIds,
                fieldCount = fields.Count + fieldParameterIds.Count,
                sortByUniqueId = sortBy?.ToString(),
                sortOrder,
                filterFieldUniqueId = filterField?.ToString(),
                filterType,
                filterValue,
                groupByUniqueId = groupBy?.ToString(),
            }));
    }
}

internal sealed class ExportViewImagesImplementation : IToolImplementation
{
    public Task<AIToolResult> ExecuteAsync(IRevitAdapter adapter, AIToolContext context, CancellationToken cancellationToken = default)
    {
        var service = adapter.ExportService ?? throw new InvalidOperationException("无法获取 ExportService");
        var document = ExportArgs.RequireDocument(adapter, context);

        var dataSource = WriteArgs.Text(context, "dataSource", "data_source");
        if (string.IsNullOrWhiteSpace(dataSource))
        {
            return Task.FromResult(AIToolResult.Fail("必须提供 dataSource（active / all / ids / cache）。"));
        }

        var viewIds = ExportArgs.ResolveViewIds(context);
        var directory = WriteArgs.Text(context, "exportDirectory", "export_directory", "directory");
        var pixelSize = (int)WriteArgs.Number(context, 2048, "width", "pixelSize");
        var quality = WriteArgs.Text(context, "quality");
        var fileNamePattern = WriteArgs.Text(context, "fileNamePattern", "file_name_pattern");

        var result = service.ExportViewImages(document, viewIds, directory, pixelSize, quality, fileNamePattern);
        return Task.FromResult(AIToolResult.Ok(
            $"✅ 已导出 {result.ItemCount} 张视图图片 → {result.OutputPath}",
            new
            {
                outputPath = result.OutputPath,
                count = result.ItemCount,
                viewIds,
                width = pixelSize,
                quality,
                fileNamePattern,
                imageFormat = WriteArgs.Text(context, "imageFormat") ?? "png",
            }));
    }
}

internal sealed class ExportViewsAsDwgImplementation : IToolImplementation
{
    public Task<AIToolResult> ExecuteAsync(IRevitAdapter adapter, AIToolContext context, CancellationToken cancellationToken = default)
    {
        var service = adapter.ExportService ?? throw new InvalidOperationException("无法获取 ExportService");
        var document = ExportArgs.RequireDocument(adapter, context);

        var dataSource = WriteArgs.Text(context, "dataSource", "data_source");
        if (string.IsNullOrWhiteSpace(dataSource))
        {
            return Task.FromResult(AIToolResult.Fail("必须提供 dataSource（active / all / ids / cache）。"));
        }

        var viewIds = ExportArgs.ResolveViewIds(context);
        var directory = WriteArgs.Text(context, "exportDirectory", "export_directory", "directory");
        var settingName = WriteArgs.Text(context, "exportSettingName", "export_setting_name");
        var exportAs3D = WriteArgs.Flag(context, false, "exportAs3D", "export_as_3d");
        var sharedLevels = WriteArgs.Flag(context, false, "sharedLevels", "shared_levels");

        var result = service.ExportViewsAsDwg(document, viewIds, directory, settingName, exportAs3D, sharedLevels);
        return Task.FromResult(AIToolResult.Ok(
            $"✅ 已导出 {result.ItemCount} 个视图为 DWG → {result.OutputPath}",
            new
            {
                outputPath = result.OutputPath,
                count = result.ItemCount,
                viewIds,
                exportSettingName = settingName ?? "ACAD2018",
                exportAsDxf = WriteArgs.Flag(context, false, "exportAsDXF", "export_as_dxf"),
                exportAs3D,
                sharedLevels,
            }));
    }
}

internal sealed class ExportScheduleToCsvImplementation : IToolImplementation
{
    public Task<AIToolResult> ExecuteAsync(IRevitAdapter adapter, AIToolContext context, CancellationToken cancellationToken = default)
    {
        var service = adapter.ScheduleService ?? throw new InvalidOperationException("无法获取 ScheduleService");
        var document = ExportArgs.RequireDocument(adapter, context);

        var scheduleId = WriteArgs.OptionalInt(context, "scheduleId", "schedule_id");
        var scheduleName = WriteArgs.Text(context, "scheduleName", "schedule_name");
        if (scheduleId is null && string.IsNullOrWhiteSpace(scheduleName))
        {
            return Task.FromResult(AIToolResult.Fail("必须提供 scheduleId 或 scheduleName。"));
        }

        var filePath = WriteArgs.Text(context, "filePath", "file_path");
        var result = service.ExportScheduleToCsv(document, scheduleId, scheduleName, filePath);

        return Task.FromResult(AIToolResult.Ok(
            $"✅ 明细表已导出 {result.ItemCount} 行 → {result.OutputPath}",
            new { outputPath = result.OutputPath, rows = result.ItemCount, includeHeader = WriteArgs.Flag(context, true, "includeHeader") }));
    }
}
