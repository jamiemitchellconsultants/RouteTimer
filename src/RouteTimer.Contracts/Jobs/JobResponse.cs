namespace RouteTimer.Contracts.Jobs;

public sealed record JobResponse(
    Guid Id,
    string Type,
    Guid SubjectId,
    string State,
    int ProgressPercent,
    string ProgressStage,
    int AttemptCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? CompletedAt,
    DateTimeOffset? LeaseExpiresAt,
    string? DiagnosticCode,
    string? DiagnosticMessage);
