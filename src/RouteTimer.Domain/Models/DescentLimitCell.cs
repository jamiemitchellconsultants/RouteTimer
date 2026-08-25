namespace RouteTimer.Domain.Models;

public sealed record DescentLimitCell(
    string GradeKey,
    string CurvatureKey,
    double SpeedCapMetresPerSecond,
    TimeSpan Evidence,
    int ActivityCount,
    ConfidenceLevel Confidence,
    bool IsFallback);
