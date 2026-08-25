namespace RouteTimer.Domain.Jobs;

public enum JobType { ParseTraining, BuildModel, PredictRoute }
public enum JobState { Queued, Running, Succeeded, Failed, Cancelled }

public sealed record AnalysisJob(
    Guid Id,
    JobType Type,
    Guid SubjectId,
    JobState State,
    int ProgressPercent,
    string ProgressStage,
    int AttemptCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? CompletedAt,
    string? WorkerId,
    DateTimeOffset? LeaseExpiresAt,
    string? DiagnosticCode = null,
    string? DiagnosticMessage = null);
