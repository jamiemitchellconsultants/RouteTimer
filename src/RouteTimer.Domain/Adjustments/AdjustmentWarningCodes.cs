using System;
using System.Collections.Generic;
using System.Linq;

namespace RouteTimer.Domain.Adjustments;

/// <summary>
/// Closed catalog of adjustment-specific warning codes, kept separate from
/// <see cref="RouteTimer.Domain.Predictions.PredictionWarningCodes"/> so baseline and adjustment
/// publication validate against disjoint vocabularies.
/// </summary>
public static class AdjustmentWarningCodes
{
    public const string SegmentGainsPowerClamped = "segment-gains-power-clamped";
    public const string SegmentGainsNoRules = "segment-gains-no-rules";
    public const string NpIfShortRouteApproximation = "np-if-short-route-approximation";
    public const string NpIfLowIntensity = "np-if-low-intensity";
    public const string NpIfHighIntensity = "np-if-high-intensity";
    public const string NpIfClosestFeasible = "np-if-closest-feasible";
    public const string TimeTargetNoClimbs = "time-target-no-climbs";
    public const string TimeTargetInfeasible = "time-target-infeasible";
    public const string RpeZoneZ7Capped = "rpe-zone-z7-capped";
    public const string RpeZoneThresholdInferred = "rpe-zone-threshold-inferred";
    public const string RpeZoneModelLowConfidence = "rpe-zone-model-low-confidence";
    public const string MatchBurningWPrimeInferredDefault = "match-burning-wprime-inferred-default";
    public const string MatchBurningCpLowConfidence = "match-burning-cp-low-confidence";
    public const string MatchBurningReserveBreach = "match-burning-reserve-breach";
    public const string MatchBurningOverlappingWindows = "match-burning-overlapping-windows";
    public const string MatchBurningWindowNoMatch = "match-burning-window-no-match";

    /// <summary>
    /// The strategy asked for a power some segment could not hold above the model's slowest describable
    /// speed. Emitted for every strategy from the publication boundary, not by an individual handler.
    /// </summary>
    public const string StrategyPowerBelowSustainableSpeed = "strategy-power-below-sustainable-speed";

    public static IReadOnlyList<string> All { get; } = Array.AsReadOnly(
    [
        SegmentGainsPowerClamped,
        SegmentGainsNoRules,
        NpIfShortRouteApproximation,
        NpIfLowIntensity,
        NpIfHighIntensity,
        NpIfClosestFeasible,
        TimeTargetNoClimbs,
        TimeTargetInfeasible,
        RpeZoneZ7Capped,
        RpeZoneThresholdInferred,
        RpeZoneModelLowConfidence,
        MatchBurningWPrimeInferredDefault,
        MatchBurningCpLowConfidence,
        MatchBurningReserveBreach,
        MatchBurningOverlappingWindows,
        MatchBurningWindowNoMatch,
        StrategyPowerBelowSustainableSpeed,
    ]);

    public static bool IsKnown(string? code) =>
        code is not null && All.Contains(code, StringComparer.Ordinal);
}
