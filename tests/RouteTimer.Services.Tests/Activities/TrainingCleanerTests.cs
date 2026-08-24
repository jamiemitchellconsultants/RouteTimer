using RouteTimer.Domain.Activities;
using RouteTimer.Services.Activities;
using RouteTimer.Services.Routes;

namespace RouteTimer.Services.Tests.Activities;

public sealed class TrainingCleanerTests
{
    [Fact]
    public void Clean_marks_first_retained_sample_after_gap_without_dropping_it()
    {
        var parsed = ActivityFixtures.EligibleRideWithGap(TimeSpan.FromSeconds(11));

        var cleaned = new TrainingCleaner(RouteProcessingOptions.Default).Clean(parsed);

        var boundary = Assert.Single(cleaned.Samples, sample => sample.CrossesDiscontinuity);
        Assert.Equal(parsed.Samples[2].Timestamp, boundary.Timestamp);
        Assert.Equal(TimeSpan.FromSeconds(10), cleaned.MovingDuration);
    }

    [Fact]
    public void Clean_retains_recorded_zero_power_when_marking_a_gap()
    {
        var parsed = ActivityFixtures.WithPauseGapAndCoasting();

        var cleaned = new TrainingCleaner(RouteProcessingOptions.Default).Clean(parsed);

        Assert.Single(cleaned.Samples, sample => sample.CrossesDiscontinuity);
        Assert.Contains(cleaned.Samples, sample => sample.PowerWatts == 0);
        Assert.Equal(ActivityEligibility.Eligible, cleaned.Quality.Eligibility);
    }

    [Fact]
    public void Clean_marks_insufficient_power_coverage_ineligible()
    {
        var parsed = ActivityFixtures.WithPowerCoverage(0.5);

        var cleaned = new TrainingCleaner(RouteProcessingOptions.Default).Clean(parsed);

        Assert.Equal(ActivityEligibility.Ineligible, cleaned.Quality.Eligibility);
        Assert.Contains("insufficient-power-coverage", cleaned.Quality.ReasonCodes);
    }
}
