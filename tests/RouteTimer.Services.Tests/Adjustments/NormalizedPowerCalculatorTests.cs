using System;
using RouteTimer.Services.Adjustments;
using Xunit;

namespace RouteTimer.Services.Tests.Adjustments;

public class NormalizedPowerCalculatorTests
{
    [Fact]
    public void Constant_power_returns_same_power()
    {
        double[] powers = [200, 200, 200];
        double[] durations = [10, 20, 30];

        double np = NormalizedPowerCalculator.CalculateNormalizedPower(powers, durations);

        Assert.Equal(200.0, np, 4);
    }

    [Fact]
    public void Variable_power_returns_higher_than_average_power()
    {
        double[] powers = [100, 300];
        double[] durations = [30, 30];

        double avgPower = 200.0;
        double np = NormalizedPowerCalculator.CalculateNormalizedPower(powers, durations);

        Assert.True(np > avgPower);
        Assert.Equal(253.04, np, 2);
    }

    [Fact]
    public void Null_or_empty_inputs_throw_exception()
    {
        Assert.Throws<ArgumentNullException>(() => NormalizedPowerCalculator.CalculateNormalizedPower(null!, [10]));
        Assert.Throws<ArgumentNullException>(() => NormalizedPowerCalculator.CalculateNormalizedPower([100], null!));
        Assert.Throws<ArgumentException>(() => NormalizedPowerCalculator.CalculateNormalizedPower([], []));
        Assert.Throws<ArgumentException>(() => NormalizedPowerCalculator.CalculateNormalizedPower([100], [10, 20]));
    }
}
