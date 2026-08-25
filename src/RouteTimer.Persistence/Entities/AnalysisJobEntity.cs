namespace RouteTimer.Persistence.Entities;

public sealed class AnalysisJobEntity
{
    public Guid Id { get; set; }
    public string Type { get; set; } = string.Empty;
    public Guid SubjectId { get; set; }
    public string State { get; set; } = string.Empty;
    public int ProgressPercent { get; set; }
    public string ProgressStage { get; set; } = "queued";
    public int AttemptCount { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? WorkerId { get; set; }
    public DateTimeOffset? LeaseExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    // Operator-facing diagnostic only: never store a stack trace or other internal detail here.
    public string? DiagnosticCode { get; set; }
    public string? DiagnosticMessage { get; set; }
}
