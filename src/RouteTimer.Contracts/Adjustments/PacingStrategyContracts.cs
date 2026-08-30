using System.Text.Json.Serialization;

namespace RouteTimer.Contracts.Adjustments;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(SegmentSpecificGainsRequest), "segment-specific-gains")]
[JsonDerivedType(typeof(NpIfTargetRequest), "np-if-target")]
[JsonDerivedType(typeof(TimeTargetRequest), "time-target")]
[JsonDerivedType(typeof(RpeZoneShiftRequest), "rpe-zone-shift")]
[JsonDerivedType(typeof(VariableMatchBurningRequest), "variable-match-burning")]
public abstract record PacingStrategyRequest;

public sealed record SegmentGainsRuleRequest(
    double? MinGradient,
    double? MaxGradient,
    int? MinSequence,
    int? MaxSequence,
    double? MinCumulativeDistanceMetres,
    double? MaxCumulativeDistanceMetres,
    double? Factor,
    double? DeltaWatts);

public sealed record SegmentSpecificGainsRequest(IReadOnlyList<SegmentGainsRuleRequest> Rules) : PacingStrategyRequest;

public sealed record NpIfTargetRequest(double TargetIntensityFactor, double FtpWatts, string Mode) : PacingStrategyRequest;

public sealed record TimeTargetRequest(
    double TargetMovingSeconds,
    string Distribution,
    double? ClimbBias,
    bool IncludeFeasibilityReport) : PacingStrategyRequest;

public sealed record ZoneAssignmentRequest(bool AllSegments, double? MinGradient, double? MaxGradient, int Zone, string Placement);

public sealed record RpeZoneShiftRequest(
    string ThresholdMode,
    double? FtpWatts,
    IReadOnlyList<ZoneAssignmentRequest> Assignments) : PacingStrategyRequest;

public sealed record MatchBurnWindowRequest(
    string Selector,
    double? MinGradient,
    double? MaxGradient,
    double? MinDistanceMetres,
    double? MaxDistanceMetres,
    int? MinSequence,
    int? MaxSequence,
    string Intensity,
    double? AbsoluteWatts,
    double? PercentCp,
    int? CpZone);

public sealed record VariableMatchBurningRequest(
    double? CriticalPowerWatts,
    double? WPrimeJoules,
    IReadOnlyList<MatchBurnWindowRequest> Windows,
    double ConservationDurationSeconds,
    double ConservationTargetCpFraction,
    double RecoveryDurationSeconds,
    double RecoveryTargetCpFraction,
    bool IncludeFatigueReport,
    bool EnableRefinement) : PacingStrategyRequest;

public sealed record PacingStrategyCapabilityResponse(
    bool Enabled,
    bool SegmentSpecificGains,
    bool NpIfTarget,
    bool TimeTarget,
    bool RpeZoneShift,
    bool VariableMatchBurning,
    int MaximumDefinitionBytes,
    int MaximumRules,
    int MaximumPhases);
