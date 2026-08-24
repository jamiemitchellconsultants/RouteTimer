using RouteTimer.Domain.Activities;
using RouteTimer.Domain.Models;
using RouteTimer.Domain.Profile;

namespace RouteTimer.Services.Models;

/// <summary>
/// Builds the personal typical-power model: a dense grid of the 8 gradient bands x 5 duration bands
/// defined in <see cref="PowerModelBands"/>, using robust (weighted) medians, per-activity duration
/// capping so no single ride dominates a cell, and shrinkage toward gradient-only/duration-only/global
/// reference medians for sparsely-evidenced cells. See design doc section 8 ("Personal Typical-Power
/// Model") for the full specification this implements.
/// </summary>
public sealed class PowerModelBuilder : IPowerModelBuilder
{
    // The same "High confidence" evidence threshold (15 minutes) doubles as the shrinkage half-life:
    // a cell needs about 15 minutes of its own direct evidence before it outweighs its reference median.
    private const double ShrinkageHalfLifeMinutes = 15.0;
    private const double HighConfidenceMinutes = 15.0;
    private const int HighConfidenceActivities = 3;
    private const double MediumConfidenceMinutes = 5.0;
    private const int MediumConfidenceActivities = 2;

    public PowerModel Build(RiderProfile profile, IReadOnlyList<CleanedActivity> activities)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var eligible = activities.Where(activity => activity.Quality.Eligibility == ActivityEligibility.Eligible).ToList();
        var contributions = CollectContributions(eligible);

        var globalMedian = WeightedMedian(contributions.Select(c => (c.Watts, c.Weight)));
        if (globalMedian is null) throw new InvalidOperationException("No eligible power evidence is available.");

        var gradientOnlyMedians = PowerModelBands.Gradient.ToDictionary(
            band => band.Key,
            band => WeightedMedian(contributions.Where(c => c.GradientKey == band.Key).Select(c => (c.Watts, c.Weight))));

        var durationOnlyMedians = PowerModelBands.Duration.ToDictionary(
            band => band.Key,
            band => WeightedMedian(contributions.Where(c => c.DurationKey == band.Key).Select(c => (c.Watts, c.Weight))));

        var byCell = contributions.ToLookup(c => (c.GradientKey, c.DurationKey));

        var bands = PowerModelBands.Gradient
            .SelectMany(gradientBand => PowerModelBands.Duration.Select(durationBand =>
            {
                var shrinkageTarget = gradientOnlyMedians[gradientBand.Key] ?? durationOnlyMedians[durationBand.Key] ?? globalMedian.Value;
                var cellContributions = byCell[(gradientBand.Key, durationBand.Key)].ToList();
                return BuildCell(gradientBand.Key, durationBand.Key, cellContributions, shrinkageTarget);
            }))
            .ToList();

        return new PowerModel(bands, globalMedian.Value);
    }

    private readonly record struct SampleContribution(int ActivityIndex, double Watts, double Weight, string GradientKey, string DurationKey);

    /// <summary>
    /// Flattens every eligible activity's power-bearing samples into weighted contributions, one per
    /// sample. Per-sample weight is the activity's total moving duration divided evenly across its own
    /// samples ("represented moving seconds") — the spec's evidence unit, uncapped at this stage.
    /// </summary>
    private static List<SampleContribution> CollectContributions(IReadOnlyList<CleanedActivity> eligible)
    {
        var contributions = new List<SampleContribution>();
        for (var activityIndex = 0; activityIndex < eligible.Count; activityIndex++)
        {
            var activity = eligible[activityIndex];
            if (activity.Samples.Count == 0) continue;
            var perSampleWeightSeconds = activity.MovingDuration.TotalSeconds / activity.Samples.Count;
            foreach (var sample in activity.Samples)
            {
                if (!sample.PowerWatts.HasValue) continue;
                var gradientBand = PowerModelBands.FindGradientBand(sample.Gradient);
                var durationBand = PowerModelBands.FindDurationBand(sample.MovingElapsed);
                contributions.Add(new SampleContribution(activityIndex, sample.PowerWatts.Value, perSampleWeightSeconds, gradientBand.Key, durationBand.Key));
            }
        }
        return contributions;
    }

    /// <summary>
    /// Aggregates one gradient x duration cell: caps each contributing activity's weight at the cell's
    /// median per-activity contribution (so one long/dominant ride can't swamp the cell), takes the
    /// resulting weighted median as the cell's direct estimate, then shrinks it toward
    /// <paramref name="shrinkageTarget"/> in proportion to how much direct evidence the cell has.
    /// </summary>
    private static PowerBand BuildCell(string gradeKey, string durationKey, IReadOnlyList<SampleContribution> cellContributions, double shrinkageTarget)
    {
        if (cellContributions.Count == 0)
            return new PowerBand(gradeKey, durationKey, shrinkageTarget, TimeSpan.Zero, 0, 0, ConfidenceLevel.Low);

        var byActivity = cellContributions
            .GroupBy(c => c.ActivityIndex)
            .Select(group => (RawContribution: group.Sum(c => c.Weight), Samples: group.ToList()))
            .ToList();
        var activityCount = byActivity.Count;

        var cap = Median(byActivity.Select(activity => activity.RawContribution).ToList());
        var cappedSamples = byActivity
            .SelectMany(activity =>
            {
                var scale = activity.RawContribution > cap ? cap / activity.RawContribution : 1.0;
                return activity.Samples.Select(sample => (sample.Watts, Weight: sample.Weight * scale));
            })
            .ToList();

        var evidence = TimeSpan.FromSeconds(cappedSamples.Sum(sample => sample.Weight));
        var evidenceMinutes = evidence.TotalMinutes;

        var shrinkageWeight = evidenceMinutes / (evidenceMinutes + ShrinkageHalfLifeMinutes);
        var typicalWatts = evidenceMinutes > 0
            ? (shrinkageWeight * WeightedMedian(cappedSamples)!.Value) + ((1 - shrinkageWeight) * shrinkageTarget)
            : shrinkageTarget;

        var confidence = evidenceMinutes >= HighConfidenceMinutes && activityCount >= HighConfidenceActivities ? ConfidenceLevel.High
            : evidenceMinutes >= MediumConfidenceMinutes && activityCount >= MediumConfidenceActivities ? ConfidenceLevel.Medium
            : ConfidenceLevel.Low;

        return new PowerBand(gradeKey, durationKey, typicalWatts, evidence, activityCount, shrinkageWeight, confidence);
    }

    /// <summary>Lower weighted median: the smallest value at which cumulative weight reaches 50% of the total.</summary>
    private static double? WeightedMedian(IEnumerable<(double Value, double Weight)> pairs)
    {
        var sorted = pairs.Where(pair => pair.Weight > 0).OrderBy(pair => pair.Value).ToList();
        var totalWeight = sorted.Sum(pair => pair.Weight);
        if (totalWeight <= 0) return null;
        var halfWeight = totalWeight / 2.0;
        var cumulative = 0.0;
        foreach (var (value, weight) in sorted)
        {
            cumulative += weight;
            if (cumulative >= halfWeight) return value;
        }
        return sorted[^1].Value;
    }

    private static double Median(IReadOnlyList<double> values)
    {
        var sorted = values.Order().ToList();
        return sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[(sorted.Count / 2) - 1] + sorted[sorted.Count / 2]) / 2;
    }
}
