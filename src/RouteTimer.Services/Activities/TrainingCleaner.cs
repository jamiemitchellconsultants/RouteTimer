using RouteTimer.Domain.Activities;
using RouteTimer.Domain.Routes;
using RouteTimer.Services.Routes;

namespace RouteTimer.Services.Activities;

public sealed class TrainingCleaner(RouteProcessingOptions routeOptions) : ITrainingCleaner
{
    private static readonly string[] ExclusionKeys = ["paused", "gap", "missing-position", "missing-elevation", "missing-speed", "missing-power", "implausible"];

    public CleanedActivity Clean(ParsedFitActivity activity)
    {
        ArgumentNullException.ThrowIfNull(activity);
        _ = routeOptions;

        var exclusions = ExclusionKeys.ToDictionary(key => key, _ => 0, StringComparer.Ordinal);
        var ordered = activity.Samples
            .OrderBy(sample => sample.Timestamp)
            .GroupBy(sample => sample.Timestamp)
            .Select(group => group.Last())
            .ToList();
        var timerEventsPresent = ordered.Any(sample => sample.TimerRunning);
        var movingCandidates = new List<(RawRideSample Sample, bool CrossesDiscontinuity)>();
        RawRideSample? prior = null;

        foreach (var sample in ordered)
        {
            var moving = timerEventsPresent ? sample.TimerRunning : sample.SpeedMetresPerSecond >= 1;
            if (!moving)
            {
                exclusions["paused"]++;
                prior = sample;
                continue;
            }

            var crossesDiscontinuity = prior is not null && sample.Timestamp - prior.Timestamp > TimeSpan.FromSeconds(10);
            if (crossesDiscontinuity)
            {
                exclusions["gap"]++;
            }

            movingCandidates.Add((sample, crossesDiscontinuity));
            prior = sample;
        }

        var denominator = Math.Max(movingCandidates.Count, 1);
        var positionCoverage = movingCandidates.Count(candidate => HasValidPosition(candidate.Sample.Position)) / (double)denominator;
        var elevationCoverage = movingCandidates.Count(candidate => candidate.Sample.Position.HasValue && double.IsFinite(candidate.Sample.Position.Value.ElevationMetres)) / (double)denominator;
        var speedCoverage = movingCandidates.Count(candidate => IsValidSpeed(candidate.Sample.SpeedMetresPerSecond)) / (double)denominator;
        var powerCoverage = movingCandidates.Count(candidate => candidate.Sample.PowerWatts.HasValue) / (double)denominator;

        var samples = new List<CleanRideSample>();
        var elapsed = TimeSpan.Zero;
        RawRideSample? previousClean = null;
        foreach (var (sample, crossesDiscontinuity) in movingCandidates)
        {
            if (!HasValidPosition(sample.Position))
            {
                exclusions["missing-position"]++;
                continue;
            }

            var position = sample.Position!.Value;
            if (!double.IsFinite(position.ElevationMetres))
            {
                exclusions["missing-elevation"]++;
                continue;
            }

            if (!IsValidSpeed(sample.SpeedMetresPerSecond))
            {
                exclusions["missing-speed"]++;
                continue;
            }

            if (sample.PowerWatts is null)
            {
                exclusions["missing-power"]++;
            }

            if (sample.SpeedMetresPerSecond > 40)
            {
                exclusions["implausible"]++;
                continue;
            }

            if (previousClean is not null && !crossesDiscontinuity)
            {
                elapsed += sample.Timestamp - previousClean.Timestamp;
            }

            samples.Add(new CleanRideSample(sample.Timestamp, elapsed, position, sample.SpeedMetresPerSecond.GetValueOrDefault(), sample.PowerWatts, sample.HeartRate, sample.Cadence, crossesDiscontinuity));
            previousClean = sample;
        }

        var reasons = new List<string>();
        if (elapsed < TimeSpan.FromMinutes(10)) reasons.Add("insufficient-moving-time");
        if (positionCoverage < .95) reasons.Add("insufficient-position-coverage");
        if (elevationCoverage < .95) reasons.Add("insufficient-elevation-coverage");
        if (speedCoverage < .95) reasons.Add("insufficient-speed-coverage");
        if (powerCoverage < .80) reasons.Add("insufficient-power-coverage");

        var cleaned = new CleanedActivity(
            activity.Name,
            samples,
            elapsed,
            new ActivityQuality(reasons.Count == 0 ? ActivityEligibility.Eligible : ActivityEligibility.Ineligible, positionCoverage, elevationCoverage, speedCoverage, powerCoverage, exclusions, reasons));
        return new TrainingGeometryEnricher(routeOptions).Enrich(cleaned);
    }

    private static bool HasValidPosition(GeoPoint? position) => position.HasValue && double.IsFinite(position.Value.Latitude) && double.IsFinite(position.Value.Longitude);

    private static bool IsValidSpeed(double? speed) => speed is not null && double.IsFinite(speed.Value) && speed.Value >= 0;
}
