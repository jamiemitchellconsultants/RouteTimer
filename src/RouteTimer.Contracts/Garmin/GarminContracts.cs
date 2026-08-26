namespace RouteTimer.Contracts.Garmin;

public sealed record GarminLoginRequest(string Email, string Password)
{
    public override string ToString() => "GarminLoginRequest { Email = <redacted>, Password = <redacted> }";
}

public sealed record GarminMfaRequest(string ChallengeId, string Code)
{
    public override string ToString() => "GarminMfaRequest { ChallengeId = <redacted>, Code = <redacted> }";
}

public sealed record GarminConnectionResponse(
    string State,
    string? GarminUserId,
    string? DisplayName,
    string? ChallengeId);

public sealed record GarminActivitySummaryResponse(
    string ActivityId,
    string Name,
    DateTimeOffset StartedAt,
    string ActivityType,
    double? DistanceMetres,
    double? DurationSeconds,
    double? AscentMetres,
    double? AveragePowerWatts,
    bool AlreadyImported);

public sealed record GarminActivityPageResponse(
    IReadOnlyList<GarminActivitySummaryResponse> Activities,
    string? NextCursor);

public sealed record GarminImportRequest(IReadOnlyList<string> ActivityIds);

public sealed record GarminImportBatchResponse(
    IReadOnlyList<GarminImportResultResponse> Activities);

public sealed record GarminImportResultResponse(
    string ActivityId,
    string? Name,
    string Outcome,
    Guid? UploadId,
    Guid? JobId,
    string? ErrorCode);
