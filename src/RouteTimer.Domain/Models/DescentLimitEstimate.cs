namespace RouteTimer.Domain.Models;

public sealed record DescentLimitEstimate(
    double SpeedCapMetresPerSecond,
    ConfidenceLevel Confidence,
    bool UsedFallback);
