using RevitAi.Engine.Abstractions;
using RevitAi.Engine.Abstractions.Adapters;
using RevitAi.Engine.Core;

namespace ExtToolLib.Implementations;

/// <summary>
/// 批次 3 第四刀实现：共享参数文件管理、项目参数（绑定）管理。
/// </summary>
public static class ProjectParameterImplementations
{
    public static void RegisterAll()
    {
        ToolImplementationRegistry.Register("manage_shared_parameters", new ManageSharedParametersImplementation());
        ToolImplementationRegistry.Register("manage_project_parameters", new ManageProjectParametersImplementation());
    }
}

internal static class ProjectParameterArgs
{
    public static object RequireDocument(IRevitAdapter adapter, AIToolContext context)
        => context.Document ?? adapter.GetActiveDocument() ?? throw new InvalidOperationException("文档对象为空");

    public static List<string> Strings(AIToolContext context, params string[] keys)
    {
        var result = new List<string>();
        var parameters = new Dictionary<string, object?>(context.Parameters, StringComparer.OrdinalIgnoreCase);
        foreach (var key in keys)
        {
            result.AddRange(ToolArgumentReader.ReadStrings(parameters, key));
        }

        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}

// 结构分析对照：Revit\AS\Tools\Revit\Net8\AI\Tools\ManageSharedParametersTool.cs（参数管理）
internal sealed class ManageSharedParametersImplementation : IToolImplementation
{
    public Task<AIToolResult> ExecuteAsync(IRevitAdapter adapter, AIToolContext context, CancellationToken cancellationToken = default)
    {
        var service = adapter.SharedParameterService ?? throw new InvalidOperationException("无法获取 SharedParameterService");
        var document = ProjectParameterArgs.RequireDocument(adapter, context);

        var operationText = WriteArgs.Text(context, "operation");
        if (string.IsNullOrWhiteSpace(operationText))
        {
            return Task.FromResult(AIToolResult.Fail("operation 参数不能为空"));
        }

        var operation = operationText.Trim().ToLowerInvariant();
        var parameterName = WriteArgs.Text(context, "parameterName", "parameter_name", "name");
        var parameterType = WriteArgs.Text(context, "parameterType", "parameter_type");
        var groupName = WriteArgs.Text(context, "groupName", "group_name", "group");
        var categoryNames = ProjectParameterArgs.Strings(context, "categoryNames", "category_names");
        var instanceParameter = WriteArgs.Flag(context, true, "instanceParameter", "instance_parameter");

        switch (operation)
        {
            case "list":
            {
                var groups = service.ListSharedParameterGroups(document);
                var total = groups.Sum(group => group.DefinitionCount);
                return Task.FromResult(AIToolResult.Ok(
                    $"✅ 共享参数共 {groups.Count} 组 / {total} 个定义",
                    new
                    {
                        groups = groups.Select(group => new
                        {
                            name = group.Name,
                            definitionCount = group.DefinitionCount,
                            parameterNames = group.ParameterNames,
                        }).ToArray(),
                        groupCount = groups.Count,
                        definitionCount = total,
                    }));
            }

            case "create":
            {
                if (string.IsNullOrWhiteSpace(parameterName))
                {
                    return Task.FromResult(AIToolResult.Fail("create 需要 parameterName。"));
                }

                if (string.IsNullOrWhiteSpace(parameterType))
                {
                    return Task.FromResult(AIToolResult.Fail("parameterType 参数不能为空"));
                }

                if (categoryNames.Count == 0)
                {
                    return Task.FromResult(AIToolResult.Fail("categoryNames 参数不能为空"));
                }

                var created = service.CreateSharedParameter(document, parameterName!, parameterType, groupName ?? "自定义参数");
                var bound = service.CreateProjectParameter(
                    document,
                    created,
                    created,
                    instanceParameter ? "instance" : "type",
                    categoryNames,
                    instanceParameter);
                return Task.FromResult(AIToolResult.Ok(
                    $"✅ 成功创建共享参数 '{created}'，绑定到 {bound} 个类别",
                    new
                    {
                        parameterName = created,
                        parameterType,
                        groupName = groupName ?? "自定义参数",
                        categoryCount = bound,
                        categoryNames,
                        instanceParameter,
                        note = "定义写入共享参数文件；还需用 manage_project_parameters create 绑定到类别后才生效。",
                    }));
            }

            case "delete":
            {
                // Revit API 没有"删除共享参数定义"的接口（DefinitionGroup/Definitions 只提供 Create）。
                return Task.FromResult(AIToolResult.Fail(
                    "Revit API 不支持从共享参数文件中删除定义（Definitions 只有 Create，没有 Remove）；"
                    + "请手工编辑共享参数 txt 文件，或改用 manage_project_parameters delete 解除绑定。"));
            }

            case "unbind":
            {
                if (string.IsNullOrWhiteSpace(parameterName))
                {
                    return Task.FromResult(AIToolResult.Fail("parameterName 参数不能为空"));
                }

                if (categoryNames.Count > 0)
                {
                    var remaining = service.UpdateProjectParameterCategories(document, parameterName!, categoryNames, add: false);
                    return Task.FromResult(AIToolResult.Ok(
                        $"✅ 成功从 {categoryNames.Count} 个类别移除参数 '{parameterName}' 的绑定；剩余 {remaining} 个类别",
                        new { parameterName, removedCategories = categoryNames, remainingCategoryCount = remaining }));
                }

                var unbound = service.DeleteProjectParameter(document, parameterName!);
                return Task.FromResult(unbound
                    ? AIToolResult.Ok($"✅ 已解除共享参数 '{parameterName}' 的全部项目绑定", new { parameterName, unbound = true })
                    : AIToolResult.Fail($"参数 '{parameterName}' 未绑定到当前项目"));
            }

            case "check":
            {
                if (string.IsNullOrWhiteSpace(parameterName))
                {
                    return Task.FromResult(AIToolResult.Fail("parameterName 参数不能为空"));
                }

                var groups = service.ListSharedParameterGroups(document);
                var defined = groups.Any(group => group.ParameterNames.Contains(parameterName!, StringComparer.OrdinalIgnoreCase));
                var usage = service.CountSharedParameterUsage(document, parameterName!);
                return Task.FromResult(AIToolResult.Ok(
                    $"📊 参数 '{parameterName}' 使用数量: {usage} 个元素",
                    new { parameterName, defined, usageCount = usage, unused = usage == 0 }));
            }

            default:
                return Task.FromResult(AIToolResult.Fail(
                    "不支持的操作类型: " + operationText + "（支持 create / list / delete / unbind / check）"));
        }
    }
}

// 结构分析对照：Revit\AS\Tools\Revit\Net8\AI\Tools\ManageProjectParametersTool.cs（参数管理）
internal sealed class ManageProjectParametersImplementation : IToolImplementation
{
    public Task<AIToolResult> ExecuteAsync(IRevitAdapter adapter, AIToolContext context, CancellationToken cancellationToken = default)
    {
        var service = adapter.SharedParameterService ?? throw new InvalidOperationException("无法获取 SharedParameterService");
        var document = ProjectParameterArgs.RequireDocument(adapter, context);

        var operationText = WriteArgs.Text(context, "operation");
        if (string.IsNullOrWhiteSpace(operationText))
        {
            return Task.FromResult(AIToolResult.Fail("operation 参数不能为空"));
        }

        var operation = operationText.Trim().ToLowerInvariant();
        var parameterName = WriteArgs.Text(context, "parameterName", "parameter_name", "name");
        var sharedParameterName = WriteArgs.Text(context, "sharedParameterName", "shared_parameter_name");
        var bindingType = WriteArgs.Text(context, "bindingType", "binding_type");
        var instanceParameter = WriteArgs.Flag(context, false, "instanceParameter", "instance_parameter");

        var categories = ProjectParameterArgs.Strings(context, "categoryNames", "category_names");
        var categoriesToAdd = ProjectParameterArgs.Strings(context, "categoriesToAdd", "categories_to_add");
        var categoriesToRemove = ProjectParameterArgs.Strings(context, "categoriesToRemove", "categories_to_remove");

        switch (operation)
        {
            case "list":
            {
                var parameters = service.ListProjectParameters(document);
                return Task.FromResult(AIToolResult.Ok(
                    $"✅ 项目参数共 {parameters.Count} 个",
                    new
                    {
                        parameters = parameters.Select(parameter => new
                        {
                            name = parameter.Name,
                            bindingType = parameter.BindingType,
                            isShared = parameter.IsShared,
                            categoryNames = parameter.CategoryNames,
                        }).ToArray(),
                        count = parameters.Count,
                    }));
            }

            case "create_binding":
            {
                if (string.IsNullOrWhiteSpace(sharedParameterName))
                {
                    return Task.FromResult(AIToolResult.Fail("sharedParameterName 参数不能为空"));
                }

                if (categories.Count == 0)
                {
                    return Task.FromResult(AIToolResult.Fail("categoryNames 参数不能为空"));
                }

                if (string.IsNullOrWhiteSpace(bindingType))
                {
                    return Task.FromResult(AIToolResult.Fail("bindingType 参数不能为空"));
                }

                var categoryCount = service.CreateProjectParameter(
                    document,
                    sharedParameterName!,
                    sharedParameterName,
                    bindingType,
                    categories,
                    instanceParameter);

                return Task.FromResult(AIToolResult.Ok(
                    $"✅ 项目参数 {sharedParameterName} 已绑定到 {categoryCount} 个类别（{(instanceParameter || string.Equals(bindingType, "instance", StringComparison.OrdinalIgnoreCase) ? "实例" : "类型")}参数）",
                    new
                    {
                        parameterName = sharedParameterName,
                        sharedParameterName = sharedParameterName ?? parameterName,
                        bindingType = instanceParameter ? "instance" : bindingType ?? "type",
                        categoryCount,
                        parameterGroup = WriteArgs.Text(context, "parameterGroup", "parameter_group"),
                        instanceParameter,
                    }));
            }

            case "delete":
            {
                if (string.IsNullOrWhiteSpace(parameterName))
                {
                    return Task.FromResult(AIToolResult.Fail("delete 需要 parameterName。"));
                }

                var deleted = service.DeleteProjectParameter(document, parameterName!);
                return Task.FromResult(deleted
                    ? AIToolResult.Ok($"✅ 已解除项目参数 {parameterName} 的绑定", new { parameterName, deleted = true })
                    : AIToolResult.Fail($"未找到项目参数 {parameterName}。"));
            }

            case "add_categories":
            case "remove_categories":
            case "update_binding":
            {
                if (string.IsNullOrWhiteSpace(parameterName))
                {
                    return Task.FromResult(AIToolResult.Fail($"{operation} 需要 parameterName。"));
                }

                if (categoriesToAdd.Count == 0 && categoriesToRemove.Count == 0)
                {
                    return Task.FromResult(AIToolResult.Fail("categoriesToAdd 或 categoriesToRemove 至少需要提供一个"));
                }

                var categoryCount = service.ListProjectParameters(document)
                    .FirstOrDefault(parameter => string.Equals(parameter.Name, parameterName, StringComparison.OrdinalIgnoreCase))
                    ?.CategoryNames.Count ?? 0;
                if (categoriesToAdd.Count > 0)
                {
                    categoryCount = service.UpdateProjectParameterCategories(document, parameterName!, categoriesToAdd, add: true);
                }

                if (categoriesToRemove.Count > 0)
                {
                    categoryCount = service.UpdateProjectParameterCategories(document, parameterName!, categoriesToRemove, add: false);
                }

                return Task.FromResult(AIToolResult.Ok(
                    $"✅ 项目参数 {parameterName} 现在绑定 {categoryCount} 个类别",
                    new { parameterName, categoryCount, added = categoriesToAdd, removed = categoriesToRemove, operation }));
            }

            case "list_parameter_groups":
            {
                var groups = service.ListSharedParameterGroups(document);
                var builtInGroups = new[]
                {
                    "数据", "文字", "约束", "尺寸标注", "标识数据", "图形", "结构", "机械", "电气", "管道",
                };
                var names = builtInGroups
                    .Concat(groups.Select(group => group.Name))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                return Task.FromResult(AIToolResult.Ok(
                    $"找到 {names.Count} 个可用的参数组",
                    new { groups = names, count = names.Count }));
            }

            default:
                return Task.FromResult(AIToolResult.Fail(
                    "不支持的操作类型: " + operationText
                    + "（支持 list / delete / create_binding / update_binding / list_parameter_groups）"));
        }
    }
}
