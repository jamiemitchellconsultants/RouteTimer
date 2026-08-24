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
        var movingCandidates = new List<RawRideSample>();
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

            if (prior is not null && sample.Timestamp - prior.Timestamp > TimeSpan.FromSeconds(10))
            {
                exclusions["gap"]++;
                prior = sample;
                continue;
            }

            movingCandidates.Add(sample);
            prior = sample;
        }

        var denominator = Math.Max(movingCandidates.Count, 1);
        var positionCoverage = movingCandidates.Count(sample => HasValidPosition(sample.Position)) / (double)denominator;
        var elevationCoverage = movingCandidates.Count(sample => sample.Position.HasValue && double.IsFinite(sample.Position.Value.ElevationMetres)) / (double)denominator;
        var speedCoverage = movingCandidates.Count(sample => IsValidSpeed(sample.SpeedMetresPerSecond)) / (double)denominator;
        var powerCoverage = movingCandidates.Count(sample => sample.PowerWatts.HasValue) / (double)denominator;

        var samples = new List<CleanRideSample>();
        var elapsed = TimeSpan.Zero;
        RawRideSample? previousClean = null;
        foreach (var sample in movingCandidates)
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

            if (previousClean is not null)
            {
                elapsed += sample.Timestamp - previousClean.Timestamp;
            }

            samples.Add(new CleanRideSample(sample.Timestamp, elapsed, position, sample.SpeedMetresPerSecond.GetValueOrDefault(), sample.PowerWatts, sample.HeartRate, sample.Cadence, false));
            previousClean = sample;
        }

        var reasons = new List<string>();
        if (elapsed < TimeSpan.FromMinutes(10)) reasons.Add("insufficient-moving-time");
        if (positionCoverage < .95) reasons.Add("insufficient-position-coverage");
        if (elevationCoverage < .95) reasons.Add("insufficient-elevation-coverage");
        if (speedCoverage < .95) reasons.Add("insufficient-speed-coverage");
        if (powerCoverage < .80) reasons.Add("insufficient-power-coverage");

        return new CleanedActivity(
            activity.Name,
            samples,
            elapsed,
            new ActivityQuality(reasons.Count == 0 ? ActivityEligibility.Eligible : ActivityEligibility.Ineligible, positionCoverage, elevationCoverage, speedCoverage, powerCoverage, exclusions, reasons));
    }

    private static bool HasValidPosition(GeoPoint? position) => position.HasValue && double.IsFinite(position.Value.Latitude) && double.IsFinite(position.Value.Longitude);

    private static bool IsValidSpeed(double? speed) => speed is not null && double.IsFinite(speed.Value) && speed.Value >= 0;
}
