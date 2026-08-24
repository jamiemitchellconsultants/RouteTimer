namespace RouteTimer.Contracts.Jobs;

public sealed record JobResponse(
    Guid Id,
    string Type,
    Guid SubjectId,
    string State,
    int AttemptCount,
    DateTimeOffset CreatedAt,
    string? WorkerId,
    DateTimeOffset? LeaseExpiresAt,
    string? DiagnosticCode,
    string? DiagnosticMessage);
