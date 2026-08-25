namespace RouteTimer.Contracts.Models;

using RouteTimer.Contracts.Jobs;

public sealed record PowerBandCoverageResponse(
    string GradeKey,
    string DurationKey,
    double TypicalWatts,
    double EvidenceSeconds,
    int ActivityCount,
    double ShrinkageWeight,
    string Confidence);

public sealed record PhysicalCoefficientsResponse(
    double DrivetrainEfficiency,
    double AirDensity,
    double RollingCoefficient,
    double CdA);

public sealed record ModelStatusResponse(
    bool IsReady,
    string? BlockingReason,
    Guid? ModelId,
    string? AlgorithmVersion,
    DateTimeOffset? CreatedAt,
    bool? WasCalibrated,
    bool? DescentWasLearned,
    string? ValidationStatus,
    double? ValidationMedianAbsolutePercentageError,
    double? ValidationP90AbsolutePercentageError,
    PhysicalCoefficientsResponse? PhysicalCoefficients,
    IReadOnlyList<PowerBandCoverageResponse> PowerBands,
    int LearnedDescentCellCount,
    int FallbackDescentCellCount,
    JobResponse? RebuildJob);

public sealed record ModelRebuildResponse(Guid JobId);
