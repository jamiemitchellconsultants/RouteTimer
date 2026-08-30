namespace RouteTimer.Domain.Adjustments;

/// <summary>
/// Optional per-segment values a strategy may attach to one adjusted segment. Absent for strategies
/// that don't produce the corresponding kind of annotation.
/// </summary>
public sealed record PredictionAdjustmentAnnotation(int? ZoneNumber, string? StrategyPhase, double? WPrimeBalanceJoules);
