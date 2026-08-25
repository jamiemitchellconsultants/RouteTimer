using RouteTimer.Domain.Routes;

namespace RouteTimer.Domain.Activities;

public sealed record CleanRideSample(
    DateTimeOffset Timestamp,
    TimeSpan MovingElapsed,
    GeoPoint Position,
    double SpeedMetresPerSecond,
    ushort? PowerWatts,
    byte? HeartRate,
    byte? Cadence,
    bool CrossesDiscontinuity,
    double Gradient = 0,
    double CurvaturePerMetre = 0);
