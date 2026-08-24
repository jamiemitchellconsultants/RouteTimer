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

    /// <summary>
    /// A single-sample activity landing in exactly one gradient/duration cell: <paramref name="movingDuration"/>
    /// is the activity's total moving time (and, with one sample, also this sample's full weight);
    /// <paramref name="elapsed"/> is the cumulative moving time at the sample, which selects the duration band.
    /// </summary>
    public static CleanedActivity SingleSampleActivity(int index, double gradient, TimeSpan elapsed, TimeSpan movingDuration, ushort watts)
    {
        var start = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero).AddDays(index);
        var sample = new CleanRideSample(start + elapsed, elapsed, new GeoPoint(51 + (index * 0.01), -2, 100), 7, watts, null, null, false, gradient);
        return new CleanedActivity($"Ride{index}", [sample], movingDuration, new ActivityQuality(ActivityEligibility.Eligible, 1, 1, 1, 1, new Dictionary<string, int>(), []));
    }

    /// <summary>
    /// Every gradient band away from "-1:1" and every duration band away from "0:30" is left with zero
    /// evidence, so the empty-cell shrinkage cascade (gradient-only -&gt; duration-only -&gt; global) can be
    /// exercised and predicted exactly:
    /// - gradientOnlyMedian("-1:1") = 200 (samples A, B, equal weight within activity1, lower weighted median).
    /// - gradientOnlyMedian("3:6") = 250 (samples C, D, equal weight within activity2).
    /// - durationOnlyMedian("0:30") = 250 (samples A=200 at weight 2700s, C=250 at weight 6000s; C's larger
    ///   weight from activity2's longer moving duration carries the weighted median).
    /// - durationOnlyMedian("60:120") = 300 (only sample B).
    /// - durationOnlyMedian("180:+") = 400 (only sample D).
    /// - globalMedian = 250 (A=200@2700s, B=300@2700s, C=250@6000s, D=400@6000s; lower weighted median).
    /// </summary>
    public static IReadOnlyList<CleanedActivity> GradientDurationSpread()
    {
        var activity1 = new CleanedActivity(
            "Ride1",
            [
                new CleanRideSample(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero), TimeSpan.FromMinutes(5), new GeoPoint(51, -2, 100), 7, 200, null, null, false, 0),
                new CleanRideSample(new DateTimeOffset(2026, 1, 1, 13, 30, 0, TimeSpan.Zero), TimeSpan.FromMinutes(90), new GeoPoint(51.01, -2, 100), 7, 300, null, null, false, 0)
            ],
            TimeSpan.FromMinutes(90),
            new ActivityQuality(ActivityEligibility.Eligible, 1, 1, 1, 1, new Dictionary<string, int>(), []));

        var activity2 = new CleanedActivity(
            "Ride2",
            [
                new CleanRideSample(new DateTimeOffset(2026, 1, 2, 12, 0, 0, TimeSpan.Zero), TimeSpan.FromMinutes(5), new GeoPoint(51.02, -2, 100), 7, 250, null, null, false, .04),
                new CleanRideSample(new DateTimeOffset(2026, 1, 2, 15, 20, 0, TimeSpan.Zero), TimeSpan.FromMinutes(200), new GeoPoint(51.03, -2, 100), 7, 400, null, null, false, .04)
            ],
            TimeSpan.FromMinutes(200),
            new ActivityQuality(ActivityEligibility.Eligible, 1, 1, 1, 1, new Dictionary<string, int>(), []));

        return [activity1, activity2];
    }

    /// <summary>
    /// Strong direct evidence in the "-1:1"/"0:30" cell (3 activities, watts all 200, 60 minutes total),
    /// plus 5 activities pulling the "-1:1" gradient-only reference median far away (watts 800, in the
    /// "60:120" duration band, so they don't touch the "0:30" cell directly). Demonstrates the target
    /// cell's own direct evidence dominates the blend rather than being pulled hard toward the reference.
    /// </summary>
    public static IReadOnlyList<CleanedActivity> StrongEvidenceWithDivergentGradientReference()
    {
        var strong = Enumerable.Range(0, 3).Select(index => Activity(index, 200)).ToList();
        var pull = Enumerable.Range(3, 5).Select(index => SingleSampleActivity(index, 0, TimeSpan.FromMinutes(90), TimeSpan.FromMinutes(90), 800)).ToList();
        return [.. strong, .. pull];
    }

    /// <summary>
    /// One cell with 3 activities: two short/cheap ones (10 and 15 minutes) and one very long, dominant
    /// one (600 minutes) at a much higher wattage. Without the per-activity cap, the weighted median
    /// would be dragged all the way to the dominant activity's wattage; with the cap (capped at the
    /// median raw per-activity contribution) it isn't.
    /// </summary>
    public static IReadOnlyList<CleanedActivity> DominantActivityCell() =>
    [
        SingleSampleActivity(0, 0, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10), 100),
        SingleSampleActivity(1, 0, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), 110),
        SingleSampleActivity(2, 0, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(600), 1000)
    ];

    /// <summary>Activities each contributing a single sample of <paramref name="minutesEach"/> to the same cell, for confidence-boundary tests.</summary>
    public static IReadOnlyList<CleanedActivity> CellWithEvidence(int activityCount, double minutesEach, ushort watts = 200) =>
        Enumerable.Range(0, activityCount)
            .Select(index => SingleSampleActivity(index, 0, TimeSpan.FromMinutes(minutesEach), TimeSpan.FromMinutes(minutesEach), watts))
            .ToList();

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
