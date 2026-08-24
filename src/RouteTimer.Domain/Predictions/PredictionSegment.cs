using RouteTimer.Domain.Models;

namespace RouteTimer.Domain.Predictions;

public sealed record PredictionSegment(int Sequence, double DistanceMetres, double Gradient, double PowerWatts, double SpeedMetresPerSecond, TimeSpan MovingTime, ConfidenceLevel Confidence);
