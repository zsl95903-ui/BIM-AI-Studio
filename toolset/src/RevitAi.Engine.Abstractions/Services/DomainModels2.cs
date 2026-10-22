namespace RevitAi.Engine.Abstractions.Services;

// ---------------------------------------------------------------------------------------------
// 收尾批：道路（墙→路面板）、CAD 图层管理、剪切顺序修正、UI 选择/缩放/激活视图。
// ---------------------------------------------------------------------------------------------

public sealed record RoadResult(
    int FloorId,
    int WallCount,
    double WidthMm,
    bool ClosedLoop,
    IReadOnlyList<Point2> Outline,
    IReadOnlyList<string> Notes);

public sealed record CadLayerInfo(
    string Name,
    int? CategoryId,
    bool Visible,
    int? ColorIndex = null,
    bool IsLocked = false,
    string? LineTypeName = null);

public sealed record CadPoint(double X, double Y, double Z = 0);

public sealed record CadLineInfo(
    string LayerName,
    string LineType,
    double StartX,
    double StartY,
    double StartZ,
    double EndX,
    double EndY,
    double EndZ,
    string? BlockName = null);

public sealed record CadTextInfo(
    string LayerName,
    string Content,
    double X,
    double Y,
    double Z,
    double Height = 0,
    double Width = 0,
    double Rotation = 0,
    string? BlockName = null);

public sealed record CadBlockInfo(
    string LayerName,
    string Name,
    double X,
    double Y,
    double Z,
    double ScaleX = 1,
    double ScaleY = 1,
    double ScaleZ = 1,
    double Rotation = 0);

public sealed record CadAnalysisLayerInfo(
    string LayerName,
    int BlockCount,
    int LineCount,
    int TextCount,
    int TotalCount,
    bool IsEmpty,
    int? ColorIndex,
    bool IsVisible,
    bool IsLocked,
    IReadOnlyList<string> ContentSummary);

public sealed record CadQueryResult(
    string Operation,
    int LinkId,
    string Source,
    int LayerCount,
    int NonEmptyLayerCount,
    IReadOnlyList<CadLayerInfo> Layers,
    IReadOnlyList<CadLineInfo> Lines,
    IReadOnlyList<CadTextInfo> Texts,
    IReadOnlyList<CadBlockInfo> Blocks,
    IReadOnlyList<CadAnalysisLayerInfo> Analysis,
    string? Message);

public sealed record CutOrderResult(int Checked, int Fixed, bool DryRun, IReadOnlyList<string> Details);

public sealed record SelectionResult(int Selected, int Cleared, bool Zoomed, string Message);

public interface IDomainExtraService
{
    RoadResult CreateRoadFromWalls(
        object document,
        IReadOnlyList<int> wallIds,
        double? laneWidthMeters,
        double? sidewalkWidthMeters,
        double? defaultRoadWidthMeters);

    CadQueryResult QueryCad(object document, int linkId, string operation, string? layerName);

    CutOrderResult FixCuttingOrder(
        object document,
        IReadOnlyList<int> cuttingIds,
        IReadOnlyList<int> toCutIds,
        bool dryRun);
}

public interface IViewInteractionService
{
    /// <summary>uiDocument 为 UIDocument；服务内部负责类型校验。</summary>
    SelectionResult SelectElements(object uiDocument, IReadOnlyList<int> elementIds, bool append, bool zoomToFit);

    SelectionResult ClearSelection(object uiDocument);

    SelectionResult ZoomToElements(object uiDocument, IReadOnlyList<int> elementIds);

    SelectionResult ActivateView(object uiDocument, int viewId);
}
