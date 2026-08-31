using System;
using RouteTimer.Domain.Adjustments.Zones;
using RouteTimer.Domain.Models;
using RouteTimer.Domain.Physics;
using RouteTimer.Services.Adjustments.Zones;
using Xunit;

namespace RouteTimer.Services.Tests.Adjustments.Zones;

public class PowerZoneResolverTests
{
    [Fact]
    public void Resolve_builds_the_finite_zone_seven_targets_from_ftp()
    {
        var set = PowerZoneResolver.Resolve(
            ZoneThresholdMode.FtpBased,
            300,
            new RiderModel(new PowerModel([], 249), PhysicalCoefficients.Default, DescentLimitModel.Conservative, true, "v1"));
        var zoneSeven = Assert.Single(set.Zones, zone => zone.Zone == 7);

        Assert.Equal(450, zoneSeven.LowerWatts, 9);
        Assert.Equal(453, zoneSeven.LowerTargetWatts, 9);
        Assert.Equal(480, zoneSeven.MidpointTargetWatts, 9);
        Assert.Equal(600, zoneSeven.UpperTargetWatts, 9);
    }
}
