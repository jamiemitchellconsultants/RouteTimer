namespace RouteTimer.Services.Activities;

public sealed record ParsedFitActivity(
    string Name,
    ActivitySport Sport,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    string? DeviceManufacturer,
    string? DeviceProduct,
    IReadOnlyList<RawRideSample> Samples,
    TimeSpan? DeviceTimerTime,
    double? DeviceDistanceMetres,
    double? DeviceAscentMetres);
