using RouteTimer.Domain.Routes;
using RouteTimer.Services.Activities;

namespace RouteTimer.Services.Tests.Activities;

internal static class ActivityFixtures
{
    public static ParsedFitActivity WithPauseGapAndCoasting() => Build(powerCoverage: 1, includePauseAndGap: true);

    public static ParsedFitActivity WithPowerCoverage(double powerCoverage) => Build(powerCoverage, includePauseAndGap: false);

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
}
