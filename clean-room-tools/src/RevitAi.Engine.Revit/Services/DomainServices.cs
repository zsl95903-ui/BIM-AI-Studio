using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using RevitAi.Engine.Abstractions.Services;

namespace RevitAi.Engine.Revit.Services;

/// <summary>
/// 地形服务：按点建地形、把楼板轮廓点并入已有地形。
/// API 事实：TopographySurface.Create(doc, IList&lt;XYZ&gt;)；TopographySurface.AddPoints(IList&lt;XYZ&gt;)。
/// </summary>
internal sealed class TopographyService : ITopographyService
{
    public int CreateTopography(object document, IReadOnlyList<Point3> points)
    {
        var doc = ModelingService.RequireDocument(document);
        if (points.Count < 3)
        {
            throw new InvalidOperationException("创建地形至少需要 3 个点。");
        }

        var xyz = points
            .Select(point => new XYZ(ModelingService.Mm(point.X), ModelingService.Mm(point.Y), ModelingService.Mm(point.Z)))
            .ToList();

        if (xyz.Distinct().Count() < 3)
        {
            throw new InvalidOperationException("地形点必须互不重合（至少 3 个不同点）。");
        }

        var surface = TopographySurface.Create(doc, xyz);
        return (int)surface.Id.Value;
    }

    public TopographyInfo AddPointsFromFloors(object document, int topographyId, IReadOnlyList<int> floorIds)
    {
        var doc = ModelingService.RequireDocument(document);
        var surface = doc.GetElement(new ElementId(topographyId)) as TopographySurface
                      ?? throw new InvalidOperationException($"元素 {topographyId} 不是地形表面。");

        if (floorIds.Count == 0)
        {
            throw new InvalidOperationException("必须提供 floor_element_ids/floor_element_id。");
        }

        var points = new List<XYZ>();
        foreach (var floorId in floorIds)
        {
            if (doc.GetElement(new ElementId(floorId)) is not Floor floor)
            {
                continue;
            }

            points.AddRange(FloorOutlinePoints(doc, floor));
        }

        if (points.Count == 0)
        {
            throw new InvalidOperationException("没有从楼板取到任何轮廓点。");
        }

        var added = 0;
        foreach (var point in points)
        {
            try
            {
                surface.AddPoints(new List<XYZ> { point });
                added++;
            }
            catch (Exception)
            {
                // 落在既有边界外/重复的点会被拒绝，跳过。
            }
        }

        var all = surface.GetPoints();
        var min = all.Count > 0 ? all.Min(item => item.Z) : 0;
        var max = all.Count > 0 ? all.Max(item => item.Z) : 0;

        return new TopographyInfo(
            (int)surface.Id.Value,
            all.Count,
            added,
            Math.Round(min * 304.8, 2),
            Math.Round(max * 304.8, 2));
    }

    private static List<XYZ> FloorOutlinePoints(Document document, Floor floor)
    {
        // 优先用楼板的几何边界（顶面轮廓），取不到时退化到包围盒四角。
        // 注意：C# 不允许在 try 块里 yield，所以先收集成列表再返回。
        var points = new List<XYZ>();
        try
        {
            var options = new Options { ComputeReferences = false, DetailLevel = ViewDetailLevel.Medium };
            if (floor.get_Geometry(options) is { } geometry)
            {
                foreach (var item in geometry)
                {
                    if (item is not Solid solid)
                    {
                        continue;
                    }

                    foreach (Face face in solid.Faces)
                    {
                        if (face is not PlanarFace planar)
                        {
                            continue;
                        }

                        var loop = planar.GetEdgesAsCurveLoops().FirstOrDefault();
                        if (loop is null)
                        {
                            continue;
                        }

                        foreach (var curve in loop)
                        {
                            points.AddRange(curve.Tessellate());
                        }

                        return points;
                    }
                }
            }
        }
        catch (Exception)
        {
            // 退化路径。
        }

        if (points.Count > 0)
        {
            return points;
        }

        if (floor.get_BoundingBox(null) is { } box)
        {
            points.Add(box.Min);
            points.Add(new XYZ(box.Max.X, box.Min.Y, box.Min.Z));
            points.Add(box.Max);
            points.Add(new XYZ(box.Min.X, box.Max.Y, box.Min.Z));
        }

        return points;
    }
}

/// <summary>
/// 保温服务：管理 Pipe/Duct 的 PipeInsulation/DuctInsulation 子元素。
/// 查询仍保留复合层视图用于 insulation_query；manager 工具对齐第三方 MEP 保温语义。
/// </summary>
internal sealed class InsulationService : IInsulationService
{
    private const double MillimetersPerFoot = 304.8;

    public IReadOnlyList<InsulationInfo> QueryInsulation(
        object document,
        IReadOnlyList<int> elementIds,
        bool onlyUninsulated,
        int limit)
    {
        var doc = ModelingService.RequireDocument(document);
        var safeLimit = limit <= 0 ? 200 : limit;
        var result = new List<InsulationInfo>();

        var hosts = elementIds.Count > 0
            ? elementIds
                .Select(id => doc.GetElement(new ElementId(id)))
                .Where(element => element is not null)
                .Select(element =>
                {
                    var typeId = element!.GetTypeId();
                    return typeId is not null && typeId != ElementId.InvalidElementId
                        ? doc.GetElement(typeId) as HostObjAttributes ?? element as HostObjAttributes
                        : element as HostObjAttributes;
                })
                .Where(host => host is not null)
                .Select(host => host!)
                .Distinct()
            : new FilteredElementCollector(doc).OfClass(typeof(HostObjAttributes)).Cast<HostObjAttributes>();

        foreach (var host in hosts)
        {
            if (result.Count >= safeLimit)
            {
                break;
            }

            var structure = host.GetCompoundStructure();
            if (structure is null)
            {
                continue;
            }

            var layers = structure.GetLayers();
            for (var index = 0; index < layers.Count; index++)
            {
                var layer = layers[index];
                var isInsulation = layer.Function == MaterialFunctionAssignment.Insulation;
                var materialName = layer.MaterialId == ElementId.InvalidElementId
                    ? string.Empty
                    : doc.GetElement(layer.MaterialId)?.Name ?? string.Empty;

                if (isInsulation || !onlyUninsulated)
                {
                    result.Add(new InsulationInfo(
                        (int)host.Id.Value,
                        host.Name,
                        host.Name,
                        isInsulation,
                        Math.Round(layer.Width * MillimetersPerFoot, 2),
                        materialName,
                        index));
                }
            }
        }

        return result;
    }

    public InsulationMutationResult ManageInsulation(
        object document,
        string operation,
        IReadOnlyList<int> elementIds,
        double? thicknessMm,
        string? materialName,
        bool overrideExisting,
        int? systemTypeId)
    {
        var doc = ModelingService.RequireDocument(document);
        var normalized = (operation ?? "add_to_elements").Trim().ToLowerInvariant();
        var hostIds = new List<int>(elementIds);
        var elementKind = string.Empty;

        if (normalized == "add_to_system")
        {
            if (systemTypeId is null)
            {
                throw new InvalidOperationException("add_to_system 需要 system_type_id。");
            }

            var systemType = doc.GetElement(new ElementId(systemTypeId.Value));
            if (systemType is PipingSystemType)
            {
                elementKind = "水管";
                hostIds.AddRange(new FilteredElementCollector(doc)
                    .OfClass(typeof(Pipe))
                    .Cast<Pipe>()
                    .Where(pipe => pipe.MEPSystem?.GetTypeId() == systemType.Id)
                    .Select(pipe => (int)pipe.Id.Value));
            }
            else if (systemType is MechanicalSystemType)
            {
                elementKind = "风管";
                hostIds.AddRange(new FilteredElementCollector(doc)
                    .OfClass(typeof(Duct))
                    .Cast<Duct>()
                    .Where(duct => duct.MEPSystem?.GetTypeId() == systemType.Id)
                    .Select(duct => (int)duct.Id.Value));
            }
            else
            {
                throw new InvalidOperationException($"元素 {systemTypeId} 不是管道/风管系统类型。");
            }

            elementKind = systemType.Name;
        }

        hostIds = hostIds.Distinct().ToList();
        var requested = hostIds.Count;
        var success = 0;
        var updated = 0;
        var skipped = new List<int>();
        var failed = new List<int>();
        var affected = new List<int>();
        var errors = new List<string>();
        var thicknessFeet = thicknessMm is > 0 ? thicknessMm.Value / MillimetersPerFoot : 0;

        foreach (var elementId in hostIds)
        {
            var host = doc.GetElement(new ElementId(elementId));
            if (host is not Pipe && host is not Duct)
            {
                skipped.Add(elementId);
                continue;
            }

            try
            {
                var existing = FindInsulation(doc, elementId);
                switch (normalized)
                {
                    case "remove_from_elements":
                        if (existing is null)
                        {
                            skipped.Add(elementId);
                            break;
                        }

                        doc.Delete(existing.Id);
                        success++;
                        affected.Add(elementId);
                        break;

                    case "modify_thickness":
                        if (existing is not InsulationLiningBase insulationThickness || thicknessFeet <= 0)
                        {
                            skipped.Add(elementId);
                            break;
                        }

                        insulationThickness.Thickness = thicknessFeet;
                        updated++;
                        affected.Add(elementId);
                        break;

                    case "modify_material":
                        if (existing is null || string.IsNullOrWhiteSpace(materialName))
                        {
                            skipped.Add(elementId);
                            break;
                        }

                        var replacementType = ResolveInsulationType(
                            doc,
                            host is Pipe ? typeof(PipeInsulationType) : typeof(DuctInsulationType),
                            materialName);
                        existing.ChangeTypeId(replacementType.Id);
                        updated++;
                        affected.Add(elementId);
                        break;

                    case "add_to_elements":
                    case "add_to_system":
                        if (existing is not null)
                        {
                            if (!overrideExisting)
                            {
                                skipped.Add(elementId);
                                break;
                            }

                            if (existing is InsulationLiningBase insulationUpdate && thicknessFeet > 0)
                            {
                                insulationUpdate.Thickness = thicknessFeet;
                            }

                            if (!string.IsNullOrWhiteSpace(materialName))
                            {
                                var replacement = ResolveInsulationType(
                                    doc,
                                    host is Pipe ? typeof(PipeInsulationType) : typeof(DuctInsulationType),
                                    materialName);
                                existing.ChangeTypeId(replacement.Id);
                            }

                            updated++;
                            affected.Add(elementId);
                            break;
                        }

                        if (thicknessFeet <= 0)
                        {
                            skipped.Add(elementId);
                            break;
                        }

                        var insulationType = ResolveInsulationType(
                            doc,
                            host is Pipe ? typeof(PipeInsulationType) : typeof(DuctInsulationType),
                            materialName);
                        if (host is Pipe pipe)
                        {
                            PipeInsulation.Create(doc, pipe.Id, insulationType.Id, thicknessFeet);
                        }
                        else if (host is Duct duct)
                        {
                            DuctInsulation.Create(doc, duct.Id, insulationType.Id, thicknessFeet);
                        }

                        success++;
                        affected.Add(elementId);
                        break;

                    default:
                        throw new InvalidOperationException($"不支持的保温操作 {operation}。");
                }
            }
            catch (Exception ex)
            {
                failed.Add(elementId);
                errors.Add($"{elementId}: {ex.GetBaseException().Message}");
            }
        }

        return new InsulationMutationResult(
            normalized,
            requested,
            success,
            updated,
            skipped.Count,
            failed.Count,
            affected,
            skipped,
            errors,
            thicknessMm,
            materialName,
            systemTypeId,
            elementKind);
    }

    public IReadOnlyList<InsulationInfo> QueryMepInsulation(
        object document,
        IReadOnlyList<int> elementIds,
        string target,
        bool onlyUninsulated,
        int limit)
    {
        var doc = ModelingService.RequireDocument(document);
        var safeLimit = limit <= 0 ? 200 : limit;
        var normalizedTarget = (target ?? "all").Trim().ToLowerInvariant();
        var hosts = new List<Element>();

        if (elementIds.Count > 0)
        {
            hosts.AddRange(elementIds
                .Select(id => doc.GetElement(new ElementId(id)))
                .Where(element => element is Pipe or Duct)
                .Select(element => element!));
        }
        else
        {
            if (normalizedTarget is "pipe" or "all")
            {
                hosts.AddRange(new FilteredElementCollector(doc).OfClass(typeof(Pipe)).Cast<Pipe>().Take(safeLimit));
            }

            if (normalizedTarget is "duct" or "all")
            {
                hosts.AddRange(new FilteredElementCollector(doc).OfClass(typeof(Duct)).Cast<Duct>().Take(safeLimit));
            }
        }

        var result = new List<InsulationInfo>();
        foreach (var host in hosts.DistinctBy(host => host.Id).Take(safeLimit))
        {
            var insulation = FindInsulation(doc, (int)host.Id.Value);
            var hasInsulation = insulation is not null;
            if (onlyUninsulated && hasInsulation)
            {
                continue;
            }

            var thickness = insulation is InsulationLiningBase lining
                ? Math.Round(lining.Thickness * MillimetersPerFoot, 2)
                : 0;
            var typeName = string.Empty;
            if (insulation?.GetTypeId() is { } insulationTypeId && insulationTypeId != ElementId.InvalidElementId)
            {
                typeName = doc.GetElement(insulationTypeId)?.Name ?? string.Empty;
            }

            result.Add(new InsulationInfo(
                (int)host.Id.Value,
                host.Name,
                host.GetType().Name,
                hasInsulation,
                thickness,
                typeName,
                0));
        }

        return result;
    }

    public IReadOnlyList<string> ListInsulationTypes(object document, string target)
    {
        var doc = ModelingService.RequireDocument(document);
        var normalizedTarget = (target ?? "all").Trim().ToLowerInvariant();
        var names = new List<string>();
        if (normalizedTarget is "pipe" or "all")
        {
            names.AddRange(new FilteredElementCollector(doc)
                .OfClass(typeof(PipeInsulationType))
                .Cast<ElementType>()
                .Select(type => type.Name));
        }

        if (normalizedTarget is "duct" or "all")
        {
            names.AddRange(new FilteredElementCollector(doc)
                .OfClass(typeof(DuctInsulationType))
                .Cast<ElementType>()
                .Select(type => type.Name));
        }

        return names.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static Element? FindInsulation(Document document, int hostElementId)
    {
        var pipeInsulation = new FilteredElementCollector(document)
            .OfClass(typeof(PipeInsulation))
            .Cast<PipeInsulation>()
            .FirstOrDefault(item => item.HostElementId == new ElementId(hostElementId));
        if (pipeInsulation is not null)
        {
            return pipeInsulation;
        }

        return new FilteredElementCollector(document)
            .OfClass(typeof(DuctInsulation))
            .Cast<DuctInsulation>()
            .FirstOrDefault(item => item.HostElementId == new ElementId(hostElementId));
    }

    private static ElementType ResolveInsulationType(Document document, Type insulationTypeClass, string? requestedName)
    {
        var types = new FilteredElementCollector(document)
            .OfClass(insulationTypeClass)
            .Cast<ElementType>()
            .ToList();
        if (types.Count == 0)
        {
            throw new InvalidOperationException($"未找到 {insulationTypeClass.Name}，请先创建保温类型。");
        }

        if (string.IsNullOrWhiteSpace(requestedName))
        {
            return types[0];
        }

        var match = types.FirstOrDefault(type =>
            string.Equals(type.Name, requestedName, StringComparison.OrdinalIgnoreCase)
            || type.Name.Contains(requestedName, StringComparison.OrdinalIgnoreCase));
        if (match is not null)
        {
            return match;
        }

        return types[0].Duplicate(requestedName)
               ?? throw new InvalidOperationException($"创建保温类型 '{requestedName}' 失败。");
    }
}

/// <summary>
/// MEP 系统服务：查询（管道/风管系统 + 成员 + 管径分布）与创建/改名/删除。
/// API 事实：PipingSystem.Create(doc, systemTypeId[, name])；MechanicalSystem.Create(...)；
/// MEPSystem.Elements(ElementSet)/Name；PipingSystem.SystemType。
/// API 校正：27.3 没有 RBS_ADDITIONAL_INSULATION_THICKNESS 内置参数，
/// 因此"是否保温"改为检查成员依赖元素里是否存在 Insulation 类元件。
/// </summary>
internal sealed class MepSystemService : IMepSystemService
{
    public MepSystemQueryResult QueryMepSystems(
        object document,
        string operation,
        string target,
        int? systemTypeId,
        bool onlyUninsulated,
        bool includeSizeDistribution,
        bool includeCategoryBreakdown,
        bool includeFittings,
        int limit)
    {
        var doc = ModelingService.RequireDocument(document);
        var safeLimit = limit <= 0 ? 200 : limit;
        var normalizedTarget = (target ?? "all").Trim().ToLowerInvariant();
        var normalizedOperation = (operation ?? "list_systems").Trim().ToLowerInvariant();
        var systems = new List<MepSystemTypeInfo>();

        if (normalizedTarget is "pipe" or "all")
        {
            foreach (var type in new FilteredElementCollector(doc).OfClass(typeof(PipingSystemType)).Cast<PipingSystemType>())
            {
                if (systemTypeId is not null && type.Id.Value != systemTypeId.Value)
                {
                    continue;
                }

                systems.Add(DescribeType(doc, type, GetPipeElements(doc, type.Id), "水管"));
            }
        }

        if (normalizedTarget is "duct" or "all")
        {
            foreach (var type in new FilteredElementCollector(doc).OfClass(typeof(MechanicalSystemType)).Cast<MechanicalSystemType>())
            {
                if (systemTypeId is not null && type.Id.Value != systemTypeId.Value)
                {
                    continue;
                }

                systems.Add(DescribeType(doc, type, GetDuctElements(doc, type.Id), "风管"));
            }
        }

        if (onlyUninsulated)
        {
            systems = systems.Where(system => system.UninsulatedCount > 0).ToList();
        }

        var totalCount = systems.Count;
        var inUseCount = systems.Count(system => system.IsInUse);
        var unusedCount = systems.Count(system => !system.IsInUse);
        var selectedSystems = systems
            .OrderByDescending(system => system.ElementCount)
            .ThenBy(system => system.Name, StringComparer.OrdinalIgnoreCase)
            .Take(safeLimit)
            .ToList();

        var resultElements = new List<Element>();
        var breakdown = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var sizes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        IReadOnlyDictionary<string, object?>? properties = null;
        string? message = null;

        if (systemTypeId is not null)
        {
            var systemType = doc.GetElement(new ElementId(systemTypeId.Value)) as MEPSystemType
                             ?? throw new InvalidOperationException($"找不到系统类型 ID: {systemTypeId}");

            resultElements.AddRange(systemType is PipingSystemType
                ? GetPipeElements(doc, systemType.Id)
                : GetDuctElements(doc, systemType.Id));

            if (includeFittings)
            {
                resultElements.AddRange(GetFittingElements(doc, systemType.Id));
            }

            resultElements = resultElements
                .GroupBy(element => element.Id)
                .Select(group => group.First())
                .Take(safeLimit)
                .ToList();

            if (includeCategoryBreakdown || normalizedOperation == "category_breakdown")
            {
                breakdown = resultElements
                    .GroupBy(element => element.Category?.Name ?? element.GetType().Name)
                    .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
            }

            if (includeSizeDistribution || normalizedOperation == "size_distribution")
            {
                CollectSizeDistribution(resultElements, sizes);
            }

            if (normalizedOperation == "get_properties")
            {
                properties = ReadProperties(systemType);
            }

            if (normalizedOperation == "system_details")
            {
                message = $"系统 {systemType.Name}: {resultElements.Count} 个元素";
            }
        }

        return new MepSystemQueryResult(
            normalizedOperation,
            normalizedTarget,
            selectedSystems,
            resultElements.Select(element => (int)element.Id.Value).Distinct().ToList(),
            breakdown,
            sizes,
            properties,
            totalCount,
            inUseCount,
            unusedCount,
            message);
    }

    private static MepSystemTypeInfo DescribeType(
        Document document,
        MEPSystemType type,
        IReadOnlyList<Element> elements,
        string elementKind)
    {
        var insulated = elements.Count(element => HasInsulationElement(document, element));
        var count = elements.Count;
        var coverage = count == 0 ? 0 : Math.Round(insulated * 100.0 / count, 1);

        return new MepSystemTypeInfo(
            (int)type.Id.Value,
            type.Name,
            type.SystemClassification.ToString(),
            elementKind,
            count,
            insulated,
            count - insulated,
            count > 0,
            type.CanBeDeleted,
            type.CanBeRenamed,
            type.CanBeCopied,
            coverage);
    }

    private static List<Element> GetPipeElements(Document document, ElementId systemTypeId)
        => new FilteredElementCollector(document)
            .OfClass(typeof(Pipe))
            .Cast<Pipe>()
            .Where(pipe => pipe.MEPSystem?.GetTypeId() == systemTypeId)
            .Cast<Element>()
            .ToList();

    private static List<Element> GetDuctElements(Document document, ElementId systemTypeId)
        => new FilteredElementCollector(document)
            .OfClass(typeof(Duct))
            .Cast<Duct>()
            .Where(duct => duct.MEPSystem?.GetTypeId() == systemTypeId)
            .Cast<Element>()
            .ToList();

    private static List<Element> GetFittingElements(Document document, ElementId systemTypeId)
    {
        var result = new List<Element>();
        foreach (var instance in new FilteredElementCollector(document).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>())
        {
            try
            {
                var parameter = instance.Parameters
                    .Cast<Parameter>()
                    .FirstOrDefault(item =>
                        item.Definition.Name.Contains("系统类型", StringComparison.OrdinalIgnoreCase)
                        || item.Definition.Name.Contains("System Type", StringComparison.OrdinalIgnoreCase));
                if (parameter is not null && parameter.HasValue && parameter.AsElementId() == systemTypeId)
                {
                    result.Add(instance);
                }
            }
            catch (Exception)
            {
                // 并非所有族实例都有系统类型参数。
            }
        }

        return result;
    }

    private static void CollectSizeDistribution(IEnumerable<Element> elements, IDictionary<string, int> sizes)
    {
        foreach (var element in elements)
        {
            var parameter = element.Parameters
                .Cast<Parameter>()
                .FirstOrDefault(item =>
                    item.StorageType == StorageType.Double &&
                    (item.Definition.Name.Contains("直径", StringComparison.OrdinalIgnoreCase)
                     || item.Definition.Name.Contains("Diameter", StringComparison.OrdinalIgnoreCase)
                     || item.Definition.Name.Contains("宽度", StringComparison.OrdinalIgnoreCase)
                     || item.Definition.Name.Contains("Width", StringComparison.OrdinalIgnoreCase)
                     || item.Definition.Name.Contains("高度", StringComparison.OrdinalIgnoreCase)
                     || item.Definition.Name.Contains("Height", StringComparison.OrdinalIgnoreCase)));
            if (parameter is null)
            {
                continue;
            }

            var value = Math.Round(parameter.AsDouble() * 304.8, 0)
                .ToString(System.Globalization.CultureInfo.InvariantCulture);
            sizes[value] = sizes.TryGetValue(value, out var count) ? count + 1 : 1;
        }
    }

    private static IReadOnlyDictionary<string, object?> ReadProperties(MEPSystemType type)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["system_type_id"] = type.Id.Value,
            ["name"] = type.Name,
            ["system_classification"] = type.SystemClassification.ToString(),
            ["can_be_deleted"] = type.CanBeDeleted,
            ["can_be_renamed"] = type.CanBeRenamed,
            ["can_be_copied"] = type.CanBeCopied,
        };

        foreach (Parameter parameter in type.Parameters)
        {
            try
            {
                if (parameter.Definition is null || result.ContainsKey(parameter.Definition.Name))
                {
                    continue;
                }

                result[parameter.Definition.Name] = parameter.HasValue
                    ? parameter.AsValueString()
                      ?? parameter.AsString()
                      ?? parameter.AsDouble().ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : null;
            }
            catch (Exception)
            {
                // 某些参数无法转换，跳过。
            }
        }

        return result;
    }

    private static MepSystemInfo Describe(Document document, MEPSystem system, string kind, string fallbackTypeName)
    {
        var memberIds = new List<int>();
        var uninsulated = 0;
        var sizes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (Element member in system.Elements)
            {
                memberIds.Add((int)member.Id.Value);

                if (!HasInsulationElement(document, member))
                {
                    uninsulated++;
                }

                var diameter = member.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM)
                               ?? member.get_Parameter(BuiltInParameter.RBS_CURVE_DIAMETER_PARAM);
                if (diameter is not null)
                {
                    var text = Math.Round(diameter.AsDouble() * 304.8, 0).ToString(System.Globalization.CultureInfo.InvariantCulture);
                    sizes[text] = sizes.TryGetValue(text, out var count) ? count + 1 : 1;
                }
            }
        }
        catch (Exception)
        {
            // 成员不可枚举时留空。
        }

        var systemTypeName = fallbackTypeName;
        try
        {
            // 注意：PipingSystem.SystemType / MechanicalSystem.SystemType 返回的是**枚举**
            // （PipeSystemType / DuctSystemType），不是元素类型；这里取枚举名做展示。
            switch (system)
            {
                case PipingSystem piping:
                    systemTypeName = piping.SystemType.ToString();
                    break;
                case MechanicalSystem mechanical:
                    systemTypeName = mechanical.SystemType.ToString();
                    break;
            }
        }
        catch (Exception)
        {
            // 忽略。
        }

        return new MepSystemInfo(
            (int)system.Id.Value,
            system.Name ?? string.Empty,
            systemTypeName,
            kind,
            memberIds.Count,
            memberIds,
            uninsulated,
            sizes);
    }

    private static bool HasInsulationElement(Document document, Element member)
    {
        try
        {
            return member.GetDependentElements(null)
                .Select(id => document.GetElement(id))
                .Any(element => element is not null &&
                                element.GetType().Name.Contains("Insulation", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception)
        {
            return false;
        }
    }

    public MepSystemInfo? CreateSystem(object document, int systemTypeId, string? newName)
    {
        var doc = ModelingService.RequireDocument(document);
        var typeId = new ElementId(systemTypeId);
        var type = doc.GetElement(typeId)
                   ?? throw new InvalidOperationException($"未找到系统类型 {systemTypeId}。");

        // 元素类型名在不同版本里可能是 PipingSystemType / MechanicalSystemType，
        // 而 PipeSystemType/DuctSystemType 是枚举；这里按"先试管道再试风管"的兜底链创建，
        // 两条都失败时把 Revit 的原始错误一并抛出，便于定位。
        var typeName = type.GetType().Name;
        var errors = new List<string>();
        MEPSystem? system = null;

        try
        {
            system = string.IsNullOrWhiteSpace(newName)
                ? PipingSystem.Create(doc, typeId)
                : PipingSystem.Create(doc, typeId, newName!);
        }
        catch (Exception ex)
        {
            errors.Add($"PipingSystem: {ex.GetBaseException().Message}");
        }

        if (system is null)
        {
            try
            {
                system = string.IsNullOrWhiteSpace(newName)
                    ? MechanicalSystem.Create(doc, typeId)
                    : MechanicalSystem.Create(doc, typeId, newName!);
            }
            catch (Exception ex)
            {
                errors.Add($"MechanicalSystem: {ex.GetBaseException().Message}");
            }
        }

        if (system is null)
        {
            throw new InvalidOperationException(
                $"元素 {systemTypeId}（{typeName}）不能作为管道/风管系统类型创建系统：{string.Join("；", errors)}");
        }

        var kind = system is PipingSystem ? "piping" : "mechanical";
        return Describe(doc, system, kind, kind == "piping" ? "PipingSystem" : "MechanicalSystem");
    }

    public MepSystemInfo? DuplicateSystem(object document, int sourceSystemTypeId, string newName)
    {
        var doc = ModelingService.RequireDocument(document);
        if (string.IsNullOrWhiteSpace(newName))
        {
            throw new InvalidOperationException("必须指定新系统名称 (new_system_name)。");
        }

        var source = doc.GetElement(new ElementId(sourceSystemTypeId)) as MEPSystemType
                     ?? throw new InvalidOperationException($"找不到源系统类型 ID: {sourceSystemTypeId}");
        if (!source.CanBeCopied)
        {
            throw new InvalidOperationException($"系统类型 '{source.Name}' 不允许复制");
        }

        if (new FilteredElementCollector(doc)
            .OfClass(typeof(MEPSystemType))
            .Cast<MEPSystemType>()
            .Any(type => string.Equals(type.Name, newName, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"系统类型名称 '{newName}' 已存在");
        }

        var duplicate = source.Duplicate(newName) as MEPSystemType
                        ?? throw new InvalidOperationException("复制系统类型失败");
        return new MepSystemInfo(
            (int)duplicate.Id.Value,
            duplicate.Name,
            duplicate.SystemClassification.ToString(),
            source is PipingSystemType ? "piping" : "mechanical",
            0,
            Array.Empty<int>(),
            0,
            new Dictionary<string, int>());
    }

    public int RenameSystem(object document, int systemId, string newName)
    {
        var doc = ModelingService.RequireDocument(document);
        if (string.IsNullOrWhiteSpace(newName))
        {
            throw new InvalidOperationException("必须提供 new_name/new_system_name。");
        }

        if (doc.GetElement(new ElementId(systemId)) is not MEPSystemType systemType)
        {
            throw new InvalidOperationException($"元素 {systemId} 不是 MEP 系统类型。");
        }

        if (!systemType.CanBeRenamed)
        {
            throw new InvalidOperationException($"系统类型 '{systemType.Name}' 不允许重命名");
        }

        systemType.Name = newName;
        return 1;
    }

    public bool DeleteSystem(object document, int systemId, bool force)
    {
        var doc = ModelingService.RequireDocument(document);
        if (doc.GetElement(new ElementId(systemId)) is not MEPSystemType systemType)
        {
            throw new InvalidOperationException($"元素 {systemId} 不是 MEP 系统类型。");
        }

        if (!systemType.CanBeDeleted)
        {
            throw new InvalidOperationException(
                $"系统类型 '{systemType.Name}' 不允许删除（可能是内置系统类型）");
        }

        var usedCount = GetPipeElements(doc, systemType.Id).Count + GetDuctElements(doc, systemType.Id).Count;
        if (usedCount > 0 && !force)
        {
            throw new InvalidOperationException(
                $"系统类型 '{systemType.Name}' 正在被 {usedCount} 个元素使用，无法删除。请先更改元素系统类型，或用 force=true。");
        }

        var deleted = doc.Delete(systemType.Id);
        return deleted.Count > 0;
    }
}
