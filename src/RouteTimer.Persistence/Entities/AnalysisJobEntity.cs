namespace RouteTimer.Persistence.Entities;

public sealed class AnalysisJobEntity
{
    public Guid Id { get; set; }
    public string Type { get; set; } = string.Empty;
    public Guid SubjectId { get; set; }
    public string State { get; set; } = string.Empty;
    public int AttemptCount { get; set; }
    public string? WorkerId { get; set; }
    public DateTimeOffset? LeaseExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
