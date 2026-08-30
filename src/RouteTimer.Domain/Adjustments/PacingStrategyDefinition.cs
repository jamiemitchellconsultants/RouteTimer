namespace RouteTimer.Domain.Adjustments;

public enum PacingStrategyType
{
    SegmentSpecificGains,
    NpIfTarget,
    TimeTarget,
    RpeZoneShift,
    VariableMatchBurning,
}

/// <summary>
/// Closed union of pacing-adjustment strategy definitions. Concrete subtypes are added one per
/// strategy as each is delivered; the dispatcher requires exactly one registered handler per
/// enabled <see cref="PacingStrategyType"/> and fails startup on missing or duplicate registrations.
/// </summary>
public abstract record PacingStrategyDefinition(PacingStrategyType Type);
