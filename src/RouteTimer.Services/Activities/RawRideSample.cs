using RouteTimer.Domain.Routes;

namespace RouteTimer.Services.Activities;

public sealed record RawRideSample(
    DateTimeOffset Timestamp,
    GeoPoint? Position,
    double? SpeedMetresPerSecond,
    ushort? PowerWatts,
    byte? HeartRate,
    byte? Cadence,
    bool TimerRunning);
