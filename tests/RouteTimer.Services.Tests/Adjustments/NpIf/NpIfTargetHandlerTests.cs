using System;
using RouteTimer.Domain.Adjustments.NpIf;
using Xunit;

namespace RouteTimer.Services.Tests.Adjustments.NpIf;

public class NpIfTargetHandlerTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1.500001)]
    public void Definition_rejects_target_if_outside_the_closed_upper_range(double targetIf)
    {
        Assert.Throws<ArgumentException>(() =>
            new NpIfTargetDefinition(targetIf, 300, NpIfScalingMode.Proportional));
    }

    [Fact]
    public void Objective_calculation_matches_exact_math()
    {
        const double ftp = 300;
        const double targetIf = 0.8;
        const double achievedNp = 247;
        var objective = achievedNp - (ftp * targetIf);
        Assert.Equal(7, objective, 12);
    }
}
