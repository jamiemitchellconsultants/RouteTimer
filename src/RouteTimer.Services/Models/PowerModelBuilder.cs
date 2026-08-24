using RouteTimer.Domain.Activities;
using RouteTimer.Domain.Models;
using RouteTimer.Domain.Profile;

namespace RouteTimer.Services.Models;

public sealed class PowerModelBuilder : IPowerModelBuilder
{
    public PowerModel Build(RiderProfile profile, IReadOnlyList<CleanedActivity> activities)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var eligible = activities.Where(activity => activity.Quality.Eligibility == ActivityEligibility.Eligible).ToList();
        var values = eligible.SelectMany(activity => activity.Samples).Where(sample => sample.PowerWatts.HasValue).Select(sample => (double)sample.PowerWatts!.Value).Order().ToList();
        if (values.Count == 0) throw new InvalidOperationException("No eligible power evidence is available.");
        var median = Median(values);
        var evidence = eligible.Select(activity => activity.MovingDuration).Aggregate(TimeSpan.Zero, (total, value) => total + value);
        var confidence = eligible.Count >= 3 && evidence >= TimeSpan.FromMinutes(15) ? ConfidenceLevel.High : eligible.Count >= 2 && evidence >= TimeSpan.FromMinutes(5) ? ConfidenceLevel.Medium : ConfidenceLevel.Low;
        return new PowerModel([new PowerBand("-1:1", "0:30", median, evidence, eligible.Count, 1, confidence)], median);
    }

    private static double Median(IReadOnlyList<double> values) => values.Count % 2 == 1 ? values[values.Count / 2] : (values[(values.Count / 2) - 1] + values[values.Count / 2]) / 2;
}
