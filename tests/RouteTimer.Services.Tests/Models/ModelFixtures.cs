using RouteTimer.Domain.Activities;
using RouteTimer.Domain.Models;
using RouteTimer.Domain.Routes;

namespace RouteTimer.Services.Tests.Models;

internal static class ModelFixtures
{
    public static IReadOnlyList<CleanedActivity> ThreeActivities(IReadOnlyList<ushort> flatWatts) => flatWatts.Select((watts, index) => Activity(index, watts)).ToList();

    public static PowerModel SimpleModel() => new(
        [
            new PowerBand("-1:1", "0:30", 180, TimeSpan.FromMinutes(20), 3, 1, ConfidenceLevel.High),
            new PowerBand("1:3", "0:30", 260, TimeSpan.FromMinutes(20), 3, 1, ConfidenceLevel.High)
        ],
        220);

    private static CleanedActivity Activity(int activityIndex, ushort watts)
    {
        var start = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero).AddDays(activityIndex);
        var samples = new[]
        {
            new CleanRideSample(start, TimeSpan.Zero, new GeoPoint(51, -2, 100), 7, watts, null, null, false, 0),
            new CleanRideSample(start.AddMinutes(20), TimeSpan.FromMinutes(20), new GeoPoint(51.01, -2, 100), 7, watts, null, null, false, 0)
        };
        return new CleanedActivity("Ride", samples, TimeSpan.FromMinutes(20), new ActivityQuality(ActivityEligibility.Eligible, 1, 1, 1, 1, new Dictionary<string, int>(), []));
    }
}
