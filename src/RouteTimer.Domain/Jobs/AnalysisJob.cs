namespace RouteTimer.Domain.Jobs;

public enum JobType { ParseTraining, BuildModel, PredictRoute }
public enum JobState { Queued, Running, Succeeded, Failed }

public sealed record AnalysisJob(Guid Id, JobType Type, Guid SubjectId, JobState State, int AttemptCount, string? WorkerId, DateTimeOffset? LeaseExpiresAt, DateTimeOffset CreatedAt, string? DiagnosticCode = null, string? DiagnosticMessage = null);
