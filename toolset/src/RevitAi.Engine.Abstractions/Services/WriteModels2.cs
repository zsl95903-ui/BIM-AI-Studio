namespace RevitAi.Engine.Abstractions.Services;

// ---------------------------------------------------------------------------------------------
// 批次 2 剩余契约：过滤器创建、标记创建、明细表创建与导出、视图导出（图片/DWG）。
// ---------------------------------------------------------------------------------------------

public sealed record ExportResult(string OutputPath, int ItemCount, string Unit);

public interface IFilterCreationService
{
    /// <summary>按 类别 + 参数 + 值 创建 ParameterFilterElement；rule 支持 equals/contains/greater/less。</summary>
    int CreateFilter(object document, string filterName, string categoryName, string parameterName, string? value, string? rule);
}

public interface ITagCreationService
{
    int CreateTag(object document, int elementId, int? tagTypeId, int? viewId, double xMm, double yMm, string? orientation, bool addLeader);
}

public interface IScheduleService
{
    int CreateSchedule(
        object document,
        string categoryName,
        string? name,
        IReadOnlyList<string> fields,
        IReadOnlyList<int>? fieldParameterIds = null,
        int? sortByParameterId = null,
        string? sortOrder = null,
        int? filterFieldParameterId = null,
        string? filterType = null,
        string? filterValue = null,
        int? groupByParameterId = null);

    /// <summary>导出明细表为 CSV（不依赖库）；scheduleId 与 scheduleName 至少给一个。</summary>
    ExportResult ExportScheduleToCsv(object document, int? scheduleId, string? scheduleName, string? outputPath);
}

public interface IExportService
{
    ExportResult ExportViewImages(
        object document,
        IReadOnlyList<int> viewIds,
        string? outputDirectory,
        int pixelSize,
        string? quality = null,
        string? fileNamePattern = null);

    ExportResult ExportViewsAsDwg(
        object document,
        IReadOnlyList<int> viewIds,
        string? outputDirectory,
        string? exportSettingName,
        bool exportAs3D = false,
        bool sharedLevels = false);
}
