namespace RouteTimer.Services.Activities;

public sealed record ParsedFitActivity(
    string Name,
    ActivitySport Sport,
    DateTimeOffset StartedAt,
    IReadOnlyList<RawRideSample> Samples,
    TimeSpan? DeviceTimerTime,
    double? DeviceDistanceMetres);
