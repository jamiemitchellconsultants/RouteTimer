using System;
using RouteTimer.Domain.Adjustments.TimeTarget;
using Xunit;

namespace RouteTimer.Services.Tests.Adjustments.TimeTarget;

public class TimeTargetHandlerTests
{
    [Fact]
    public void Definition_rejects_a_climb_bias_in_proportional_mode()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            new TimeTargetDefinition(3600, TimeTargetDistribution.Proportional, 1.2, true));

        Assert.Contains("climb bias", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Climb_focused_scales_preserve_the_requested_weighted_mean()
    {
        const double climbFraction = 0.25;
        const double outerScale = 1.4;
        const double bias = 1.8;
        var normalizer = (climbFraction * bias) + (1 - climbFraction);
        var climb = outerScale * bias / normalizer;
        var other = outerScale / normalizer;

        Assert.Equal(outerScale, (climbFraction * climb) + ((1 - climbFraction) * other), 12);
    }
}
