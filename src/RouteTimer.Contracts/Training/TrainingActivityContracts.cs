namespace RouteTimer.Contracts.Training;

public sealed record TrainingActivitySummaryResponse(
    Guid Id,
    Guid UploadId,
    string SourceFileName,
    DateTimeOffset? StartedAt,
    DateTimeOffset? EndedAt,
    string? DeviceManufacturer,
    string? DeviceProduct,
    double? DistanceMetres,
    double? AscentMetres,
    double MovingSeconds,
    string Eligibility,
    double PositionCoverage,
    double ElevationCoverage,
    double SpeedCoverage,
    double PowerCoverage,
    IReadOnlyList<string> ReasonCodes,
    DateTimeOffset CreatedAt);

public sealed record TrainingActivityDetailResponse(
    TrainingActivitySummaryResponse Summary,
    IReadOnlyDictionary<string, int> ExclusionCounts);
