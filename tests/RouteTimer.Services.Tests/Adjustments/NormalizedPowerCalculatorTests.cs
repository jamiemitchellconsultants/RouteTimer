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

        Assert.Equal(200.0, np, 2);
    }

    [Fact]
    public void Variable_power_30s_at_100w_and_30s_at_300w_returns_expected_normalized_power()
    {
        double[] powers = [100, 300];
        double[] durations = [30, 30];

        double np = NormalizedPowerCalculator.CalculateNormalizedPower(powers, durations);

        // 30 s at 100 W + 30 s at 300 W trailing 30s rolling average yields ~223.07 W
        Assert.Equal(223.07, np, 2);
    }

    [Fact]
    public void Short_route_under_30_seconds_falls_back_to_weighted_mean()
    {
        double[] powers = [100, 300];
        double[] durations = [10, 10]; // Total 20s

        double np = NormalizedPowerCalculator.CalculateNormalizedPower(powers, durations);

        Assert.Equal(200.0, np, 2);
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
