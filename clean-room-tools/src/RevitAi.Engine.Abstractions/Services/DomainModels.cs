namespace RevitAi.Engine.Abstractions.Services;

// ---------------------------------------------------------------------------------------------
// 批次 5 领域重工程（第一批）：地形、保温、MEP 系统。
// ---------------------------------------------------------------------------------------------

public sealed record Point3(double X, double Y, double Z);

public sealed record TopographyInfo(
    int ElementId,
    int PointCount,
    int AddedPoints,
    double MinElevationMm,
    double MaxElevationMm);

public sealed record InsulationInfo(
    int ElementId,
    string ElementName,
    string TypeName,
    bool HasInsulation,
    double ThicknessMm,
    string MaterialName,
    int LayerIndex);

public sealed record InsulationMutationResult(
    string Operation,
    int Requested,
    int SuccessCount,
    int UpdatedCount,
    int SkippedCount,
    int FailedCount,
    IReadOnlyList<int> AffectedElementIds,
    IReadOnlyList<int> SkippedElementIds,
    IReadOnlyList<string> Errors,
    double? ThicknessMm,
    string? MaterialName,
    int? SystemTypeId,
    string ElementKind);

public sealed record MepSystemInfo(
    int SystemId,
    string Name,
    string SystemTypeName,
    string Kind,
    int MemberCount,
    IReadOnlyList<int> MemberIds,
    int UninsulatedMemberCount,
    IReadOnlyDictionary<string, int> SizeDistribution);

public sealed record MepSystemTypeInfo(
    int SystemTypeId,
    string Name,
    string Classification,
    string ElementKind,
    int ElementCount,
    int InsulatedCount,
    int UninsulatedCount,
    bool IsInUse,
    bool CanBeDeleted,
    bool CanBeRenamed,
    bool CanBeCopied,
    double CoverageRate);

public sealed record MepSystemQueryResult(
    string Operation,
    string Target,
    IReadOnlyList<MepSystemTypeInfo> Systems,
    IReadOnlyList<int> ElementIds,
    IReadOnlyDictionary<string, int> Breakdown,
    IReadOnlyDictionary<string, int> SizeDistribution,
    IReadOnlyDictionary<string, object?>? Properties,
    int TotalCount,
    int InUseCount,
    int UnusedCount,
    string? Message);

public interface ITopographyService
{
    int CreateTopography(object document, IReadOnlyList<Point3> points);

    TopographyInfo AddPointsFromFloors(object document, int topographyId, IReadOnlyList<int> floorIds);
}

public interface IInsulationService
{
    IReadOnlyList<InsulationInfo> QueryInsulation(
        object document,
        IReadOnlyList<int> elementIds,
        bool onlyUninsulated,
        int limit);

    InsulationMutationResult ManageInsulation(
        object document,
        string operation,
        IReadOnlyList<int> elementIds,
        double? thicknessMm,
        string? materialName,
        bool overrideExisting,
        int? systemTypeId);

    IReadOnlyList<InsulationInfo> QueryMepInsulation(
        object document,
        IReadOnlyList<int> elementIds,
        string target,
        bool onlyUninsulated,
        int limit);

    IReadOnlyList<string> ListInsulationTypes(object document, string target);
}

public interface IMepSystemService
{
    MepSystemQueryResult QueryMepSystems(
        object document,
        string operation,
        string target,
        int? systemTypeId,
        bool onlyUninsulated,
        bool includeSizeDistribution,
        bool includeCategoryBreakdown,
        bool includeFittings,
        int limit);

    MepSystemInfo? CreateSystem(object document, int systemTypeId, string? newName);

    MepSystemInfo? DuplicateSystem(object document, int sourceSystemTypeId, string newName);

    int RenameSystem(object document, int systemId, string newName);

    bool DeleteSystem(object document, int systemId, bool force);
}
