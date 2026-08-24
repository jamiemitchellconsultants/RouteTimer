using RouteTimer.Domain.Models;
using RouteTimer.Services.Models;

namespace RouteTimer.Services.Tests.Models;

public sealed class PowerLookupTests
{
    [Fact]
    public void GetWatts_interpolates_between_adjacent_gradient_bands()
    {
        var model = ModelFixtures.SimpleModel();

        var estimate = new PowerLookup(model).GetWatts(0.015, TimeSpan.FromMinutes(10));

        Assert.InRange(estimate.Watts, 199, 241);
        Assert.False(estimate.Extrapolated);
    }
}
