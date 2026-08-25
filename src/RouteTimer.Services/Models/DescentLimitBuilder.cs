using RouteTimer.Domain.Activities;
using RouteTimer.Domain.Models;

namespace RouteTimer.Services.Models;

public sealed class DescentLimitBuilder : IDescentLimitBuilder
{
    private const double MinimumEvidenceSeconds = 5 * 60;
    private const double FullEvidenceSeconds = 20 * 60;
    private const int MinimumActivityCount = 2;
    private const int FullActivityCount = 3;

    public DescentLimitModel Build(IReadOnlyList<CleanedActivity> activities)
    {
        ArgumentNullException.ThrowIfNull(activities);

        var observations = CollectObservations(activities);
        var byCell = observations.ToLookup(value => (value.GradeKey, value.CurvatureKey));
        var fallback = DescentLimitModel.Conservative.Cells.ToDictionary(
            cell => (cell.GradeKey, cell.CurvatureKey));

        var cells = DescentGradeBand.All.SelectMany(grade => DescentCurvatureBand.All.Select(curvature =>
            BuildCell(grade, curvature, byCell[(grade.Key, curvature.Key)].ToArray(), fallback[(grade.Key, curvature.Key)])))
            .ToArray();
        return new DescentLimitModel(cells);
    }

    private static IReadOnlyList<Observation> CollectObservations(IReadOnlyList<CleanedActivity> activities)
    {
        var observations = new List<Observation>();
        for (var activityIndex = 0; activityIndex < activities.Count; activityIndex++)
        {
            var activity = activities[activityIndex];
            if (activity.Quality.Eligibility != ActivityEligibility.Eligible) continue;

            for (var sampleIndex = 1; sampleIndex < activity.Samples.Count; sampleIndex++)
            {
                var previous = activity.Samples[sampleIndex - 1];
                var ending = activity.Samples[sampleIndex];
                if (ending.CrossesDiscontinuity) continue;

                var duration = ending.MovingElapsed - previous.MovingElapsed;
                if (duration <= TimeSpan.Zero ||
                    !double.IsFinite(ending.SpeedMetresPerSecond) || ending.SpeedMetresPerSecond < 2)
                    continue;

                var grade = DescentGradeBand.Find(ending.Gradient);
                var curvature = DescentCurvatureBand.Find(ending.CurvaturePerMetre);
                if (grade is null || curvature is null) continue;

                observations.Add(new Observation(
                    activityIndex,
                    ending.SpeedMetresPerSecond,
                    duration.Ticks,
                    ending.CurvaturePerMetre,
                    grade.Key,
                    curvature.Key));
            }
        }

        return observations;
    }

    private static DescentLimitCell BuildCell(
        DescentGradeBand grade,
        DescentCurvatureBand curvature,
        IReadOnlyList<Observation> observations,
        DescentLimitCell fallback)
    {
        var evidence = TimeSpan.FromTicks(observations.Sum(value => value.DurationTicks));
        var evidenceSeconds = evidence.TotalSeconds;
        var activityCount = observations.Select(value => value.ActivityIndex).Distinct().Count();

        if (evidenceSeconds < MinimumEvidenceSeconds || activityCount < MinimumActivityCount)
            return fallback with { Evidence = evidence, ActivityCount = activityCount };

        var observedP90 = Percentile90(observations.Select(value => value.Speed).ToArray());
        var representativeCurvature = Median(observations.Select(value => value.CurvaturePerMetre).ToArray());
        var curvatureCap = representativeCurvature > 0 ? Math.Sqrt(2 / representativeCurvature) : 20;
        var hardCap = Math.Min(20, curvatureCap);
        var conservativeCap = Math.Min(grade.ConservativeCapMetresPerSecond, hardCap);
        var durationWeight = Math.Clamp(evidenceSeconds / FullEvidenceSeconds, 0, 1);
        var activityWeight = Math.Clamp(activityCount / (double)FullActivityCount, 0, 1);
        var shrinkage = Math.Min(durationWeight, activityWeight);
        var learnedCap = conservativeCap + (shrinkage * (observedP90 - conservativeCap));
        var effectiveCap = Math.Min(hardCap, Math.Max(2, learnedCap));
        var confidence = evidenceSeconds >= FullEvidenceSeconds && activityCount >= FullActivityCount
            ? ConfidenceLevel.High
            : ConfidenceLevel.Medium;

        return new DescentLimitCell(
            grade.Key,
            curvature.Key,
            effectiveCap,
            evidence,
            activityCount,
            confidence,
            false);
    }

    private static double Percentile90(IReadOnlyList<double> values)
    {
        var sorted = values.Order().ToArray();
        var rank = .9 * (sorted.Length - 1);
        var lower = (int)Math.Floor(rank);
        var upper = (int)Math.Ceiling(rank);
        if (lower == upper) return sorted[lower];
        return sorted[lower] + ((rank - lower) * (sorted[upper] - sorted[lower]));
    }

    private static double Median(IReadOnlyList<double> values)
    {
        var sorted = values.Order().ToArray();
        var middle = sorted.Length / 2;
        return sorted.Length % 2 == 1
            ? sorted[middle]
            : (sorted[middle - 1] + sorted[middle]) / 2;
    }

    private readonly record struct Observation(
        int ActivityIndex,
        double Speed,
        long DurationTicks,
        double CurvaturePerMetre,
        string GradeKey,
        string CurvatureKey);
}
