namespace RouteTimer.Domain.Adjustments;

/// <summary>
/// Closed union of pacing-adjustment strategy result reports, mirroring <see cref="PacingStrategyDefinition"/>.
/// Concrete subtypes are added one per strategy as each is delivered.
/// </summary>
public abstract record PacingStrategyReport(PacingStrategyType Type);
