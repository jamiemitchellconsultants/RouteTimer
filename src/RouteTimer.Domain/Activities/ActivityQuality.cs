namespace RouteTimer.Domain.Activities;

public enum ActivityEligibility
{
    Eligible,
    Ineligible
}

public sealed record ActivityQuality(
    ActivityEligibility Eligibility,
    double PositionCoverage,
    double ElevationCoverage,
    double SpeedCoverage,
    double PowerCoverage,
    IReadOnlyDictionary<string, int> ExclusionCounts,
    IReadOnlyList<string> ReasonCodes);
