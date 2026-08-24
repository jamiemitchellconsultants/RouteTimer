using RouteTimer.Domain.Physics;
using RouteTimer.Services.Physics;

namespace RouteTimer.Services.Tests.Physics;

public sealed class CyclingForcesTests
{
    [Theory]
    [InlineData(0.00, 10.0, 75, 10, 245, 8)]
    [InlineData(0.05, 5.0, 75, 10, 261, 12)]
    public void Required_power_matches_known_tolerance(double grade, double speed, double riderKg, double bikeKg, double expectedWatts, double tolerance)
    {
        var watts = CyclingForces.RequiredRiderPower(grade, speed, riderKg + bikeKg, PhysicalCoefficients.Default);

        Assert.InRange(watts, expectedWatts - tolerance, expectedWatts + tolerance);
    }
}
