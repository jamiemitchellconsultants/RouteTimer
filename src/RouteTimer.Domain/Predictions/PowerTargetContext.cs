using RouteTimer.Domain.Models;

namespace RouteTimer.Domain.Predictions;

public sealed record PowerTargetContext(
    PredictionRouteSegment Segment,
    TimeSpan ElapsedMovingTime,
    PowerEstimate BaselineEstimate);
