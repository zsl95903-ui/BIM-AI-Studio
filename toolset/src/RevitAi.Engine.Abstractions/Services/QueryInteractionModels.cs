namespace RevitAi.Engine.Abstractions.Services;

// ---------------------------------------------------------------------------------------------
// 批次「查询与交互」：元素查询/过滤、房间边界、轴网交点、明细表字段与数据、碰撞检查、
// 删除链接、项目单位与类别可见性。
// ---------------------------------------------------------------------------------------------

public sealed record ElementSummary(
    int ElementId,
    string Name,
    string CategoryName,
    int TypeId,
    string TypeName,
    int LevelId,
    string LevelName,
    double? X = null,
    double? Y = null,
    double? Z = null,
    bool IsElementType = false);

public sealed record BoundaryLoopInfo(int Index, bool IsOuter, double LengthMm, IReadOnlyList<Point2> Points);

public sealed record GridIntersectionInfo(bool Exists, double X, double Y, double Z, string Message);

public sealed record ScheduleFieldInfo(
    int Index,
    string Name,
    string FieldType,
    bool IsHidden,
    string Heading,
    int ParameterId = 0,
    string UniqueId = "");

public sealed record ScheduleDataInfo(
    string ScheduleName,
    IReadOnlyList<string> Headers,
    IReadOnlyList<IReadOnlyList<string>> Rows,
    int TotalRows,
    int ReturnedRows);

public sealed record CollisionPairInfo(
    int SourceId,
    int TargetId,
    bool Intersects,
    int SourceDocumentIndex = 0,
    int TargetDocumentIndex = 0,
    int? SourceLinkInstanceId = null,
    int? TargetLinkInstanceId = null,
    string SourceCategory = "",
    string TargetCategory = "",
    string SourceTypeName = "",
    string TargetTypeName = "",
    IReadOnlyList<Point3>? CollisionPoints = null);

public interface IAdvancedQueryService
{
    IReadOnlyList<ElementSummary> QueryElements(
        object document,
        string operation,
        IReadOnlyList<int> elementIds,
        string? categoryName,
        int? typeId,
        string? typeName,
        int limit);

    IReadOnlyList<ElementSummary> FilterElements(
        object document,
        IReadOnlyList<int> elementIds,
        string? categoryName,
        string? typeName,
        string? levelName,
        int limit);

    IReadOnlyList<BoundaryLoopInfo> GetRoomBoundaries(object document, int roomId);

    GridIntersectionInfo GetGridIntersection(object document, int grid1Id, int grid2Id);

    IReadOnlyList<ScheduleFieldInfo> GetScheduleFields(object document, int? scheduleId, string? scheduleName);

    ScheduleDataInfo ReadScheduleData(
        object document,
        int? scheduleId,
        string? scheduleName,
        int startRow,
        int maxRows,
        bool includeHeader);

    IReadOnlyList<CollisionPairInfo> CheckCollision(
        object document,
        IReadOnlyList<int> sourceIds,
        IReadOnlyList<int> targetIds,
        int? sourceLinkInstanceId = null,
        int? targetLinkInstanceId = null);

    bool DeleteLink(object document, int linkId);
}

public interface ISelectionQueryService
{
    IReadOnlyList<int> GetSelectedElementIds(object uiDocument);
}

public interface IProjectSettingsService
{
    int SetProjectUnits(object document, string? length, string? area, string? volume, string? angle, string? slope);

    int SetCategoryVisibility(object document, int viewId, string categoryName, bool visible);
}
