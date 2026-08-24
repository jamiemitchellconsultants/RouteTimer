using RouteTimer.Domain.Activities;
using RouteTimer.Domain.Routes;
using RouteTimer.Services.Activities;

namespace RouteTimer.Services.Tests.Activities;

internal static class ActivityFixtures
{
    public static ParsedFitActivity WithPauseGapAndCoasting() => Build(powerCoverage: 1, includePauseAndGap: true);

    public static ParsedFitActivity WithPowerCoverage(double powerCoverage) => Build(powerCoverage, includePauseAndGap: false);

    public static ParsedFitActivity EligibleRideWithGap(TimeSpan gap)
    {
        var start = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var samples = new[]
        {
            Sample(start, 0),
            Sample(start, 5),
            Sample(start, 5 + gap.TotalSeconds),
            Sample(start, 10 + gap.TotalSeconds)
        };

        return new ParsedFitActivity("Gap", ActivitySport.Cycling, start, samples, TimeSpan.FromMinutes(12), 5000);
    }

    public static ParsedFitActivity RideWithFilteredGapBoundary()
    {
        var start = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var samples = new[]
        {
            Sample(start, 0),
            Sample(start, 5),
            new RawRideSample(start.AddSeconds(16), null, 7, 200, 140, 85, true),
            Sample(start, 21)
        };

        return new ParsedFitActivity("Filtered gap", ActivitySport.Cycling, start, samples, TimeSpan.FromMinutes(12), 5000);
    }

    public static CleanedActivity CleanedTwoSectionsWithSharpBoundary()
    {
        var start = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var samples = new[]
        {
            CleanSample(start, 0, 0, false),
            CleanSample(start, 5, 1, false),
            CleanSample(start, 20, 100, true)
        };
        return new CleanedActivity("Sections", samples, TimeSpan.FromSeconds(5), EligibleQuality());
    }

    public static CleanedActivity CleanedFrom(IReadOnlyList<GeoPoint> points)
    {
        var start = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var samples = points.Select((point, index) => new CleanRideSample(
            start.AddSeconds(index * 5), TimeSpan.FromSeconds(index * 5), point, 7, 200, 140, 85, false)).ToList();
        return new CleanedActivity("Geometry", samples, TimeSpan.FromSeconds((points.Count - 1) * 5), EligibleQuality());
    }

    public static IReadOnlyList<GeoPoint> NonlinearElevationPoints() =>
    [
        new(0, 0, 100),
        new(0, 0.00022483, 112),
        new(0, 0.00044966, 91),
        new(0, 0.00067449, 130),
        new(0, 0.00089932, 96),
        new(0, 0.00112415, 118),
        new(0, 0.00134898, 105),
    ];

    public static ParsedFitActivity NonlinearElevationRide()
    {
        var start = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var samples = NonlinearElevationPoints().Select((point, index) => new RawRideSample(
            start.AddSeconds(index * 5), point, 7, 200, 140, 85, true)).ToArray();
        return new ParsedFitActivity("Nonlinear elevation", ActivitySport.Cycling, start, samples, TimeSpan.FromSeconds(30), 150);
    }

    private static ParsedFitActivity Build(double powerCoverage, bool includePauseAndGap)
    {
        var start = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var samples = Enumerable.Range(0, 151).Select(index =>
        {
            var isPaused = includePauseAndGap && index == 5;
            var offset = index * 5 + (includePauseAndGap && index > 80 ? 20 : 0);
            var power = index == 3 ? (ushort?)0 : index < 151 * powerCoverage ? (ushort?)200 : null;
            return new RawRideSample(
                start.AddSeconds(offset),
                new GeoPoint(51 + (index * .0003), -2, 100 + index),
                7,
                power,
                140,
                85,
                !isPaused);
        }).ToList();

        return new ParsedFitActivity("Training", ActivitySport.Cycling, start, samples, TimeSpan.FromMinutes(12), 5_000);
    }

    private static RawRideSample Sample(DateTimeOffset start, double seconds) => new(
        start.AddSeconds(seconds), new GeoPoint(51, -2 + (seconds * .00001), 100), 7, 200, 140, 85, true);

    private static CleanRideSample CleanSample(DateTimeOffset start, double seconds, double elevation, bool crossesDiscontinuity) => new(
        start.AddSeconds(seconds), TimeSpan.FromSeconds(seconds), new GeoPoint(51, -2 + (seconds * .00001), elevation), 7, 200, 140, 85, crossesDiscontinuity);

    private static ActivityQuality EligibleQuality() => new(ActivityEligibility.Eligible, 1, 1, 1, 1, new Dictionary<string, int>(), []);
}
