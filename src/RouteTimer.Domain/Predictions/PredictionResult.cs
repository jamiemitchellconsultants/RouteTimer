using RouteTimer.Domain.Models;

namespace RouteTimer.Domain.Predictions;

public sealed record PredictionResult(IReadOnlyList<PredictionSegment> Segments, TimeSpan MovingTime, ConfidenceLevel Confidence);
