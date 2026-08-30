using System.Text.Json;

namespace RouteTimer.Contracts.Adjustments;

public sealed record PredictionAdjustmentSubmissionResponse(Guid AdjustmentId, Guid JobId, Guid PredictionId);

public sealed record PredictionAdjustmentSummaryResponse(
    Guid Id,
    Guid PredictionId,
    string StrategyType,
    string State,
    double? MovingSeconds,
    double? AverageSpeedMetresPerSecond,
    double? AveragePowerWatts,
    string? Confidence,
    IReadOnlyList<string> Warnings,
    string? AlgorithmVersion,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt);

public sealed record PredictionAdjustmentSegmentResponse(
    int Sequence,
    double PowerWatts,
    double SpeedMetresPerSecond,
    double SegmentMovingSeconds,
    double CumulativeMovingSeconds,
    string Confidence,
    int? ZoneNumber,
    string? StrategyPhase,
    double? WPrimeBalanceJoules);

/// <summary>
/// <see cref="Strategy"/> and <see cref="Report"/> carry the adjustment's own canonical JSON verbatim
/// (each already self-describes its strategy via a "type" property), rather than a second polymorphic
/// contract type mirroring the request union - the detail response is read-only, so there is nothing
/// to validate structurally on the way out that canonicalization did not already validate on the way in.
/// </summary>
public sealed record PredictionAdjustmentDetailResponse(
    PredictionAdjustmentSummaryResponse Summary,
    JsonElement Strategy,
    JsonElement? Report,
    IReadOnlyList<PredictionAdjustmentSegmentResponse> Segments);
