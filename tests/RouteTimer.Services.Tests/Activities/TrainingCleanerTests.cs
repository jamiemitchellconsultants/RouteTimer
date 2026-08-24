using RouteTimer.Domain.Activities;
using RouteTimer.Services.Activities;
using RouteTimer.Services.Routes;

namespace RouteTimer.Services.Tests.Activities;

public sealed class TrainingCleanerTests
{
    [Fact]
    public void Clean_excludes_pauses_and_gaps_but_keeps_recorded_zero_power()
    {
        var parsed = ActivityFixtures.WithPauseGapAndCoasting();

        var cleaned = new TrainingCleaner(RouteProcessingOptions.Default).Clean(parsed);

        Assert.DoesNotContain(cleaned.Samples, sample => sample.CrossesDiscontinuity);
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
