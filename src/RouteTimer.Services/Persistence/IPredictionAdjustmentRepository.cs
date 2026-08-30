using RouteTimer.Domain.Adjustments;
using RouteTimer.Domain.Models;

namespace RouteTimer.Services.Persistence;

public sealed record QueuedAdjustmentCreation(
    Guid PredictionId,
    PacingStrategyType StrategyType,
    string StrategyJson,
    string StrategyAlgorithmVersion,
    DateTimeOffset CreatedAt);

public enum AdjustmentBaselineStatus { Ready, BaselineNotFound, BaselineNotReady }

/// <summary>
/// The repository creates only the adjustment row; enqueueing its <c>AdjustPrediction</c> job (and
/// returning that job's id alongside this one) is a separate service-layer step so a failed enqueue
/// can be handled with explicit compensating cleanup instead of silently rolling back inside one
/// repository transaction.
/// </summary>
public sealed record QueuedAdjustmentCreationResult(AdjustmentBaselineStatus BaselineStatus, Guid? AdjustmentId);

public sealed record AdjustmentForProcessing(Guid Id, Guid PredictionId, PacingStrategyType StrategyType, string StrategyJson);

public sealed record PersistedAdjustmentSegment(
    int Sequence,
    double PowerWatts,
    double SpeedMetresPerSecond,
    TimeSpan SegmentMovingTime,
    TimeSpan CumulativeMovingTime,
    ConfidenceLevel Confidence,
    int? ZoneNumber,
    string? StrategyPhase,
    double? WPrimeBalanceJoules);

public sealed record AdjustmentPublication(
    TimeSpan MovingTime,
    double AverageSpeedMetresPerSecond,
    double AveragePowerWatts,
    ConfidenceLevel Confidence,
    IReadOnlyList<string> Warnings,
    string ReportJson,
    IReadOnlyList<PersistedAdjustmentSegment> Segments);

public sealed record PredictionAdjustmentSummary(
    Guid Id,
    Guid PredictionId,
    PacingStrategyType StrategyType,
    AdjustmentState State,
    TimeSpan? MovingTime,
    double? AverageSpeedMetresPerSecond,
    double? AveragePowerWatts,
    ConfidenceLevel? Confidence,
    IReadOnlyList<string> Warnings,
    string StrategyAlgorithmVersion,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt);

public sealed record PredictionAdjustmentDetail(
    Guid Id,
    Guid PredictionId,
    PacingStrategyType StrategyType,
    string StrategyJson,
    AdjustmentState State,
    TimeSpan? MovingTime,
    double? AverageSpeedMetresPerSecond,
    double? AveragePowerWatts,
    ConfidenceLevel? Confidence,
    IReadOnlyList<string> Warnings,
    string? ResultJson,
    string StrategyAlgorithmVersion,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt,
    IReadOnlyList<PersistedAdjustmentSegment> Segments);

/// <summary>Owns the append-only <c>prediction_adjustments</c>/<c>prediction_adjustment_segments</c> aggregate and its durable job lifecycle.</summary>
public interface IPredictionAdjustmentRepository
{
    Task<QueuedAdjustmentCreationResult> CreateQueuedAsync(QueuedAdjustmentCreation creation, CancellationToken cancellationToken);
    Task<AdjustmentForProcessing?> GetForProcessingAsync(Guid adjustmentId, CancellationToken cancellationToken);
    Task<bool> TryPublishAsync(Guid adjustmentId, Guid jobId, string workerId, AdjustmentPublication publication, CancellationToken cancellationToken);
    Task<bool> DeleteAsync(Guid predictionId, Guid adjustmentId, DateTimeOffset now, CancellationToken cancellationToken);
    Task FailAsync(Guid adjustmentId, string code, string message, CancellationToken cancellationToken);
    Task<IReadOnlyList<PredictionAdjustmentSummary>> GetSummariesAsync(Guid predictionId, CancellationToken cancellationToken);
    Task<PredictionAdjustmentDetail?> GetAsync(Guid predictionId, Guid adjustmentId, CancellationToken cancellationToken);
}
