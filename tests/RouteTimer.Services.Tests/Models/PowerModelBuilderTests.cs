using RouteTimer.Domain.Models;
using RouteTimer.Domain.Profile;
using RouteTimer.Services.Models;

namespace RouteTimer.Services.Tests.Models;

public sealed class PowerModelBuilderTests
{
    [Fact]
    public void Build_uses_robust_median_and_distinct_activity_coverage()
    {
        var activities = ModelFixtures.ThreeActivities([180, 200, 1000]);

        var model = new PowerModelBuilder().Build(new RiderProfile(75, 10), activities);

        var flatEarly = model.Bands.Single(band => band.GradeKey == "-1:1" && band.DurationKey == "0:30");
        Assert.InRange(flatEarly.TypicalWatts, 180, 220);
        Assert.Equal(ConfidenceLevel.High, flatEarly.Confidence);
    }
}
