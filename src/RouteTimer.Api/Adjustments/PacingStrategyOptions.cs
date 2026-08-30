using RouteTimer.Domain.Adjustments;

namespace RouteTimer.Api.Adjustments;

/// <summary>
/// Server-side feature gate for pacing adjustments, bound from the <c>PacingStrategies</c>
/// configuration section. The parent <see cref="Enabled"/> flag hides adjustment creation entirely;
/// each strategy also has its own flag so strategies can be rolled out independently. This is an
/// availability gate only - it is not a substitute for server-side validation of the strategy itself.
/// </summary>
public sealed record PacingStrategyOptions(
    bool Enabled,
    bool SegmentSpecificGains,
    bool NpIfTarget,
    bool TimeTarget,
    bool RpeZoneShift,
    bool VariableMatchBurning,
    int MaximumDefinitionBytes,
    int MaximumRules,
    int MaximumPhases)
{
    public static PacingStrategyOptions Bind(IConfiguration configuration)
    {
        var section = configuration.GetSection("PacingStrategies");
        return new PacingStrategyOptions(
            section.GetValue("Enabled", false),
            section.GetValue("SegmentSpecificGains", false),
            section.GetValue("NpIfTarget", false),
            section.GetValue("TimeTarget", false),
            section.GetValue("RpeZoneShift", false),
            section.GetValue("VariableMatchBurning", false),
            section.GetValue("MaximumDefinitionBytes", 65536),
            section.GetValue("MaximumRules", 10),
            section.GetValue("MaximumPhases", 10));
    }

    public bool IsEnabled(PacingStrategyType type) => Enabled && type switch
    {
        PacingStrategyType.SegmentSpecificGains => SegmentSpecificGains,
        PacingStrategyType.NpIfTarget => NpIfTarget,
        PacingStrategyType.TimeTarget => TimeTarget,
        PacingStrategyType.RpeZoneShift => RpeZoneShift,
        PacingStrategyType.VariableMatchBurning => VariableMatchBurning,
        _ => false,
    };

    public IReadOnlyList<PacingStrategyType> EnabledTypes =>
        Enum.GetValues<PacingStrategyType>().Where(IsEnabled).ToArray();
}
