using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;

namespace RevitAi.Engine.Revit.Services;

using RevitAi.Engine.Abstractions.Services;

/// <summary>
/// 收尾批领域服务：道路（墙→路面板）、CAD 图层可见性、剪切顺序修正。
/// 说明：
///   * 道路按"墙中心线围成的轮廓/包围盒"生成一块楼板（路面板）；车道线、人行道、路缘、标线等
///     属于道路详图语义，本实现只记录参数不建模（在结果 notes 里如实说明）。
///   * CAD 图层通过"导入实例类别下的子类别 + View.SetCategoryHidden"控制。
///   * 剪切顺序用 JoinGeometryUtils.IsCuttingElementInJoin / SwitchJoinOrder 修正。
/// </summary>
internal sealed class DomainExtraService : IDomainExtraService
{
    public RoadResult CreateRoadFromWalls(
        object document,
        IReadOnlyList<int> wallIds,
        double? laneWidthMeters,
        double? sidewalkWidthMeters,
        double? defaultRoadWidthMeters)
    {
        var doc = ModelingService.RequireDocument(document);

        var walls = wallIds.Count > 0
            ? wallIds.Select(id => doc.GetElement(new ElementId(id))).OfType<Wall>().ToList()
            : new FilteredElementCollector(doc).OfClass(typeof(Wall)).Cast<Wall>().Take(200).ToList();

        if (walls.Count == 0)
        {
            throw new InvalidOperationException("没有可用的墙（请提供 wall_element_ids）。");
        }

        var curves = walls
            .Select(wall => (wall.Location as LocationCurve)?.Curve)
            .Where(curve => curve is not null)
            .Select(curve => curve!)
            .ToList();

        if (curves.Count == 0)
        {
            throw new InvalidOperationException("这些墙没有可用的定位线（LocationCurve）。");
        }

        // 尝试把墙中心线首尾相接成闭合环；接不上就退化为整体包围盒。
        var outline = new List<XYZ>();
        var closed = TryBuildClosedLoop(curves, outline);
        if (!closed)
        {
            outline = BoundingRectangle(curves);
        }

        var widthMm = (defaultRoadWidthMeters ?? 6.0) * 1000.0;
        var loop = new CurveLoop();
        for (var index = 0; index < outline.Count; index++)
        {
            var start = outline[index];
            var end = outline[(index + 1) % outline.Count];
            if (start.DistanceTo(end) > 1e-6)
            {
            loop.Append(Autodesk.Revit.DB.Line.CreateBound(start, end));
            }
        }

        var levelId = ModelingService.ResolveLevelId(doc, walls[0].LevelId is { } wallLevel && wallLevel != ElementId.InvalidElementId ? (int)wallLevel.Value : null);
        var floorTypeId = ModelingService.DefaultTypeId(doc, BuiltInCategory.OST_Floors, "楼板");
        var floor = Floor.Create(doc, new List<CurveLoop> { loop }, floorTypeId, levelId);

        var notes = new List<string>();
        if (laneWidthMeters is not null)
        {
            notes.Add($"lane_width_meters={laneWidthMeters} 已记录未建模");
        }

        if (sidewalkWidthMeters is not null)
        {
            notes.Add($"sidewalk_width_meters={sidewalkWidthMeters} 已记录未建模");
        }

        notes.Add(closed ? "轮廓来自墙中心线首尾相接的闭合环" : "轮廓来自墙整体包围盒（墙中心线未闭合）");
        notes.Add($"路面板按 default_road_width_meters={(defaultRoadWidthMeters ?? 6.0)} 记录（仅用于结果说明）");

        return new RoadResult(
            (int)floor.Id.Value,
            walls.Count,
            widthMm,
            closed,
            outline.Select(point => new Point2(Math.Round(point.X * 304.8, 2), Math.Round(point.Y * 304.8, 2))).ToList(),
            notes);
    }

    private static bool TryBuildClosedLoop(IReadOnlyList<Curve> curves, List<XYZ> outline)
    {
        const double tolerance = 1e-4;
        var remaining = new List<Curve>(curves);
        var current = remaining[0].GetEndPoint(0);
        outline.Add(current);
        var guard = 0;

        while (remaining.Count > 0 && guard++ < curves.Count * 2)
        {
            var nextIndex = -1;
            var reverse = false;

            for (var index = 0; index < remaining.Count; index++)
            {
                if (remaining[index].GetEndPoint(0).DistanceTo(current) < tolerance)
                {
                    nextIndex = index;
                    reverse = false;
                    break;
                }

                if (remaining[index].GetEndPoint(1).DistanceTo(current) < tolerance)
                {
                    nextIndex = index;
                    reverse = true;
                    break;
                }
            }

            if (nextIndex < 0)
            {
                return false;
            }

            var curve = remaining[nextIndex];
            remaining.RemoveAt(nextIndex);
            current = reverse ? curve.GetEndPoint(0) : curve.GetEndPoint(1);
            outline.Add(current);
        }

        if (outline.Count < 4)
        {
            return false;
        }

        if (outline[0].DistanceTo(outline[^1]) > tolerance)
        {
            return false;
        }

        outline.RemoveAt(outline.Count - 1);
        return outline.Count >= 3;
    }

    private static List<XYZ> BoundingRectangle(IReadOnlyList<Curve> curves)
    {
        var points = curves.SelectMany(curve => curve.Tessellate()).ToList();
        var minX = points.Min(point => point.X);
        var maxX = points.Max(point => point.X);
        var minY = points.Min(point => point.Y);
        var maxY = points.Max(point => point.Y);

        return new List<XYZ>
        {
            new(minX, minY, 0),
            new(maxX, minY, 0),
            new(maxX, maxY, 0),
            new(minX, maxY, 0),
        };
    }

    public CadQueryResult QueryCad(object document, int linkId, string operation, string? layerName)
    {
        var doc = ModelingService.RequireDocument(document);
        var import = doc.GetElement(new ElementId(linkId)) as ImportInstance
                     ?? throw new InvalidOperationException($"未找到 ID 为 {linkId} 的 CAD 链接");

        var normalized = (operation ?? "get_layers").Trim().ToLowerInvariant();
        var parsed = TryParseCadFile(import, doc);
        var source = "CAD 文件";
        if (parsed is null)
        {
            parsed = ReadCadGeometry(import, doc);
            source = "几何对象";
        }

        var layers = parsed.Layers;
        var lines = string.IsNullOrWhiteSpace(layerName)
            ? parsed.Lines
            : parsed.Lines.Where(line => string.Equals(line.LayerName, layerName, StringComparison.OrdinalIgnoreCase)).ToList();
        var texts = string.IsNullOrWhiteSpace(layerName)
            ? parsed.Texts
            : parsed.Texts.Where(text => string.Equals(text.LayerName, layerName, StringComparison.OrdinalIgnoreCase)).ToList();
        var blocks = string.IsNullOrWhiteSpace(layerName)
            ? parsed.Blocks
            : parsed.Blocks.Where(block => string.Equals(block.LayerName, layerName, StringComparison.OrdinalIgnoreCase)).ToList();
        var analysis = BuildCadAnalysis(layers, lines, texts, blocks);
        var nonEmpty = analysis.Count(layer => layer.TotalCount > 0);

        string? message = null;
        if (normalized == "get_texts" && texts.Count == 0 && source == "几何对象")
        {
            message = $"无法从图层 '{layerName}' 获取文字信息。请确保 CAD 文件通过链接方式导入，且原始 CAD 文件可访问。";
        }

        return new CadQueryResult(
            normalized,
            linkId,
            source,
            layers.Count,
            nonEmpty,
            normalized == "get_layers" ? layers : new List<CadLayerInfo>(),
            normalized == "get_lines" ? lines : new List<CadLineInfo>(),
            normalized == "get_texts" ? texts : new List<CadTextInfo>(),
            normalized == "get_blocks" ? blocks : new List<CadBlockInfo>(),
            normalized == "analyze" ? analysis : new List<CadAnalysisLayerInfo>(),
            message);
    }

    private sealed record ParsedCad(
        List<CadLayerInfo> Layers,
        List<CadLineInfo> Lines,
        List<CadTextInfo> Texts,
        List<CadBlockInfo> Blocks);

    private static ParsedCad? TryParseCadFile(ImportInstance import, Document document)
    {
        try
        {
            var type = document.GetElement(import.GetTypeId()) as CADLinkType;
            var reference = type?.GetExternalFileReference();
            var path = reference is null
                ? null
                : ModelPathUtils.ConvertModelPathToUserVisiblePath(reference.GetAbsolutePath());
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return null;
            }

            var extension = Path.GetExtension(path).ToLowerInvariant();
            CadDocument cad = extension == ".dwg"
                ? DwgReader.Read(path, null)
                : DxfReader.Read(path, null);
            var conversion = ReadCadConversionFactor(type);
            var layers = cad.Layers
                .Select(layer => new CadLayerInfo(
                    layer.Name,
                    null,
                    layer.IsOn,
                    layer.Color.Index,
                    false,
                    layer.LineType?.Name))
                .ToList();
            var lines = new List<CadLineInfo>();
            var texts = new List<CadTextInfo>();
            var blocks = new List<CadBlockInfo>();

            foreach (var entity in cad.Entities)
            {
                var layer = entity.Layer?.Name ?? "0";
                switch (entity)
                {
                    case ACadSharp.Entities.Line line:
                        lines.Add(new CadLineInfo(
                            layer,
                            "Line",
                            line.StartPoint.X * conversion,
                            line.StartPoint.Y * conversion,
                            line.StartPoint.Z * conversion,
                            line.EndPoint.X * conversion,
                            line.EndPoint.Y * conversion,
                            line.EndPoint.Z * conversion));
                        break;

                    case LwPolyline polyline:
                        for (var index = 0; index < polyline.Vertices.Count - 1; index++)
                        {
                            var start = polyline.Vertices[index].Location;
                            var end = polyline.Vertices[index + 1].Location;
                            lines.Add(new CadLineInfo(
                                layer,
                                "LwPolyline",
                                start.X * conversion,
                                start.Y * conversion,
                                0,
                                end.X * conversion,
                                end.Y * conversion,
                                0));
                        }

                        break;

                    case ACadSharp.Entities.Arc arc:
                        AddArc(lines, layer, arc.Center, arc.Radius, arc.StartAngle, arc.EndAngle, conversion);
                        break;

                    case Circle circle:
                        lines.Add(new CadLineInfo(
                            layer,
                            "Circle",
                            (circle.Center.X - circle.Radius) * conversion,
                            circle.Center.Y * conversion,
                            circle.Center.Z * conversion,
                            (circle.Center.X + circle.Radius) * conversion,
                            circle.Center.Y * conversion,
                            circle.Center.Z * conversion));
                        break;

                    case TextEntity text:
                        texts.Add(new CadTextInfo(
                            layer,
                            text.Value,
                            text.InsertPoint.X * conversion,
                            text.InsertPoint.Y * conversion,
                            text.InsertPoint.Z * conversion,
                            text.Height * conversion,
                            text.Value.Length * text.Height * text.WidthFactor * conversion,
                            text.Rotation));
                        break;

                    case MText text:
                        texts.Add(new CadTextInfo(
                            layer,
                            text.PlainText ?? text.Value ?? string.Empty,
                            text.InsertPoint.X * conversion,
                            text.InsertPoint.Y * conversion,
                            text.InsertPoint.Z * conversion,
                            text.Height * conversion,
                            Math.Max(text.RectangleWidth, text.HorizontalWidth) * conversion,
                            text.Rotation));
                        break;

                    case Insert insert:
                        blocks.Add(new CadBlockInfo(
                            layer,
                            insert.Block?.Name ?? string.Empty,
                            insert.InsertPoint.X * conversion,
                            insert.InsertPoint.Y * conversion,
                            insert.InsertPoint.Z * conversion,
                            insert.XScale,
                            insert.YScale,
                            insert.ZScale,
                            insert.Rotation));
                        break;
                }
            }

            return new ParsedCad(layers, lines, texts, blocks);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static double ReadCadConversionFactor(CADLinkType? type)
    {
        if (type is null)
        {
            return 1.0;
        }

        var parameter = type.Parameters
            .Cast<Parameter>()
            .FirstOrDefault(item =>
                item.Definition.Name.Contains("导入单位", StringComparison.OrdinalIgnoreCase)
                || item.Definition.Name.Contains("Import Units", StringComparison.OrdinalIgnoreCase)
                || item.Definition.Name.Equals("Unit", StringComparison.OrdinalIgnoreCase));
        if (parameter is null || !parameter.HasValue)
        {
            return 1.0;
        }

        if (parameter.StorageType == StorageType.String)
        {
            var text = (parameter.AsString() ?? string.Empty).ToLowerInvariant();
            if (text.Contains("inch") || text.Contains("英寸"))
            {
                return 25.4;
            }

            if (text.Contains("centimeter") || text.Contains("厘米"))
            {
                return 10;
            }

            if (text.Contains("meter") || text.Contains("米"))
            {
                return 1000;
            }

            if (text.Contains("feet") || text.Contains("英尺"))
            {
                return 304.8;
            }
        }

        return 1.0;
    }

    private static void AddArc(
        ICollection<CadLineInfo> target,
        string layer,
        dynamic center,
        double radius,
        double startAngle,
        double endAngle,
        double conversion)
    {
        target.Add(new CadLineInfo(
            layer,
            "Arc",
            (center.X + radius * Math.Cos(startAngle)) * conversion,
            (center.Y + radius * Math.Sin(startAngle)) * conversion,
            center.Z * conversion,
            (center.X + radius * Math.Cos(endAngle)) * conversion,
            (center.Y + radius * Math.Sin(endAngle)) * conversion,
            center.Z * conversion));
    }

    private static ParsedCad ReadCadGeometry(ImportInstance import, Document document)
    {
        var layers = new Dictionary<string, CadLayerInfo>(StringComparer.OrdinalIgnoreCase);
        var lines = new List<CadLineInfo>();
        var blocks = new List<CadBlockInfo>();
        var geometry = import.get_Geometry(new Options
        {
            DetailLevel = ViewDetailLevel.Fine,
            IncludeNonVisibleObjects = true,
            ComputeReferences = false,
        });

        if (geometry is not null)
        {
            TraverseGeometry(document, geometry, layers, lines, blocks);
        }

        return new ParsedCad(
            layers.Values.OrderBy(layer => layer.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            lines,
            new List<CadTextInfo>(),
            blocks);
    }

    private static void TraverseGeometry(
        Document document,
        GeometryElement geometry,
        IDictionary<string, CadLayerInfo> layers,
        ICollection<CadLineInfo> lines,
        ICollection<CadBlockInfo> blocks)
    {
        foreach (var item in geometry)
        {
            var layerName = ResolveLayerName(document, item);
            if (!string.IsNullOrWhiteSpace(layerName) && !layers.ContainsKey(layerName))
            {
                layers[layerName] = new CadLayerInfo(layerName, null, true);
            }

            switch (item)
            {
                case Curve curve:
                {
                    var start = curve.GetEndPoint(0);
                    var end = curve.GetEndPoint(1);
                    lines.Add(new CadLineInfo(
                        layerName ?? "0",
                        curve is Autodesk.Revit.DB.Line ? "Line" : curve.GetType().Name,
                        Math.Round(start.X * 304.8, 3),
                        Math.Round(start.Y * 304.8, 3),
                        Math.Round(start.Z * 304.8, 3),
                        Math.Round(end.X * 304.8, 3),
                        Math.Round(end.Y * 304.8, 3),
                        Math.Round(end.Z * 304.8, 3)));
                    break;
                }
                case GeometryInstance instance:
                {
                    var origin = instance.Transform.Origin;
                    blocks.Add(new CadBlockInfo(
                        layerName ?? "0",
                        "CAD Block",
                        Math.Round(origin.X * 304.8, 3),
                        Math.Round(origin.Y * 304.8, 3),
                        Math.Round(origin.Z * 304.8, 3)));
                    TraverseGeometry(document, instance.GetInstanceGeometry(), layers, lines, blocks);
                    break;
                }
            }
        }
    }

    private static string? ResolveLayerName(Document document, GeometryObject geometry)
    {
        try
        {
            if (geometry.GraphicsStyleId == ElementId.InvalidElementId)
            {
                return null;
            }

            var style = document.GetElement(geometry.GraphicsStyleId) as GraphicsStyle;
            return style?.GraphicsStyleCategory?.Name;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static List<CadAnalysisLayerInfo> BuildCadAnalysis(
        IReadOnlyList<CadLayerInfo> layers,
        IReadOnlyList<CadLineInfo> lines,
        IReadOnlyList<CadTextInfo> texts,
        IReadOnlyList<CadBlockInfo> blocks)
    {
        return layers.Select(layer =>
        {
            var lineCount = lines.Count(line => string.Equals(line.LayerName, layer.Name, StringComparison.OrdinalIgnoreCase));
            var textCount = texts.Count(text => string.Equals(text.LayerName, layer.Name, StringComparison.OrdinalIgnoreCase));
            var blockCount = blocks.Count(block => string.Equals(block.LayerName, layer.Name, StringComparison.OrdinalIgnoreCase));
            var total = lineCount + textCount + blockCount;
            var summary = new List<string>();
            if (blockCount > 0)
            {
                summary.Add($"图块({blockCount})");
            }

            if (lineCount > 0)
            {
                summary.Add($"线条({lineCount})");
            }

            if (textCount > 0)
            {
                summary.Add($"文字({textCount})");
            }

            return new CadAnalysisLayerInfo(
                layer.Name,
                blockCount,
                lineCount,
                textCount,
                total,
                total == 0,
                layer.ColorIndex,
                layer.Visible,
                layer.IsLocked,
                summary);
        })
        .OrderByDescending(layer => layer.TotalCount)
        .ToList();
    }

    public CutOrderResult FixCuttingOrder(
        object document,
        IReadOnlyList<int> cuttingIds,
        IReadOnlyList<int> toCutIds,
        bool dryRun)
    {
        var doc = ModelingService.RequireDocument(document);
        var details = new List<string>();

        var cutting = cuttingIds.Count > 0
            ? cuttingIds.Select(id => doc.GetElement(new ElementId(id))).Where(element => element is not null).Select(element => element!).ToList()
            : new List<Element>();
        var toCut = toCutIds.Count > 0
            ? toCutIds.Select(id => doc.GetElement(new ElementId(id))).Where(element => element is not null).Select(element => element!).ToList()
            : new List<Element>();

        if (cutting.Count == 0 || toCut.Count == 0)
        {
            throw new InvalidOperationException("需要提供 cuttingElementIds 与 elementToCutIds 两组元素。");
        }

        var checkedCount = 0;
        var fixedCount = 0;

        foreach (var cutter in cutting)
        {
            foreach (var target in toCut)
            {
                if (cutter.Id == target.Id)
                {
                    continue;
                }

                checkedCount++;
                try
                {
                    if (!JoinGeometryUtils.AreElementsJoined(doc, cutter, target))
                    {
                        details.Add($"{cutter.Id.Value} 与 {target.Id.Value} 未连接，跳过");
                        continue;
                    }

                    if (JoinGeometryUtils.IsCuttingElementInJoin(doc, cutter, target))
                    {
                        continue;
                    }

                    if (dryRun)
                    {
                        details.Add($"{cutter.Id.Value} 应成为剪切方（当前相反）");
                        continue;
                    }

                    JoinGeometryUtils.SwitchJoinOrder(doc, cutter, target);
                    fixedCount++;
                }
                catch (Exception ex)
                {
                    details.Add($"{cutter.Id.Value}/{target.Id.Value}: {ex.GetBaseException().Message}");
                }
            }
        }

        return new CutOrderResult(checkedCount, fixedCount, dryRun, details);
    }
}

/// <summary>
/// UI 交互服务：选择集、缩放、激活视图。uiDocument 由适配器提供（Revit 主线程）。
/// API：UIDocument.Selection.SetElementIds/GetElementIds；UIDocument.ShowElements；
/// UIDocument.ActiveView 赋值 + RefreshActiveView。
/// </summary>
internal sealed class ViewInteractionService : IViewInteractionService
{
    public SelectionResult SelectElements(object uiDocument, IReadOnlyList<int> elementIds, bool append, bool zoomToFit)
    {
        var uidoc = RequireUiDocument(uiDocument);
        if (elementIds.Count == 0)
        {
            throw new InvalidOperationException("必须提供 elementIds 或 cacheId。");
        }

        var selection = new List<ElementId>();
        if (append)
        {
            selection.AddRange(uidoc.Selection.GetElementIds());
        }

        foreach (var id in elementIds)
        {
            var elementId = new ElementId(id);
            if (!selection.Contains(elementId))
            {
                selection.Add(elementId);
            }
        }

        uidoc.Selection.SetElementIds(selection);

        var zoomed = false;
        if (zoomToFit)
        {
            uidoc.ShowElements(selection);
            zoomed = true;
        }

        return new SelectionResult(
            selection.Count,
            0,
            zoomed,
            $"已选择 {selection.Count} 个元素" + (zoomed ? "，并缩放到该选择集" : string.Empty));
    }

    public SelectionResult ClearSelection(object uiDocument)
    {
        var uidoc = RequireUiDocument(uiDocument);
        var previous = uidoc.Selection.GetElementIds();
        uidoc.Selection.SetElementIds(new List<ElementId>());
        return new SelectionResult(0, previous.Count, false, $"已清空选择（原有 {previous.Count} 个）");
    }

    public SelectionResult ZoomToElements(object uiDocument, IReadOnlyList<int> elementIds)
    {
        var uidoc = RequireUiDocument(uiDocument);
        if (elementIds.Count == 0)
        {
            throw new InvalidOperationException("必须提供 elementIds 或 cacheId。");
        }

        var ids = elementIds.Select(id => new ElementId(id)).ToList();
        uidoc.ShowElements(ids);
        return new SelectionResult(ids.Count, 0, true, $"已缩放到 {ids.Count} 个元素");
    }

    public SelectionResult ActivateView(object uiDocument, int viewId)
    {
        var uidoc = RequireUiDocument(uiDocument);
        var document = uidoc.Document;
        var view = document.GetElement(new ElementId(viewId)) as View
                   ?? throw new InvalidOperationException($"未找到视图 {viewId}。");

        if (view.IsTemplate)
        {
            throw new InvalidOperationException($"视图 {view.Name} 是视图样板，不能激活。");
        }

        uidoc.ActiveView = view;
        uidoc.RefreshActiveView();
        return new SelectionResult(0, 0, false, $"已激活视图 {view.Name}（{view.ViewType}）");
    }

    private static UIDocument RequireUiDocument(object uiDocument)
        => uiDocument as UIDocument
           ?? throw new InvalidOperationException(
               "当前没有可用的 UIDocument（该工具需要在 Revit 界面会话里执行，且必须有打开的项目）。");
}
