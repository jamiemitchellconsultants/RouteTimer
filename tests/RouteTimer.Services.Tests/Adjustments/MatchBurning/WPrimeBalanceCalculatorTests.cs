using System;
using RouteTimer.Domain.Models;
using RouteTimer.Domain.Predictions;
using RouteTimer.Services.Adjustments.MatchBurning;
using Xunit;

namespace RouteTimer.Services.Tests.Adjustments.MatchBurning;

public class WPrimeBalanceCalculatorTests
{
    [Fact]
    public void Calculate_spends_work_above_cp_linearly()
    {
        var segment = new PredictionSegment(
            1, 500, .04, 350, 8, TimeSpan.FromSeconds(60), ConfidenceLevel.High);

        var result = WPrimeBalanceCalculator.Calculate([segment], 250, 20_000);

        Assert.Equal(14_000, Assert.Single(result.Points).DisplayBalanceJoules, 9);
        Assert.Equal(6_000, result.WorkAboveCriticalPowerJoules, 9);
        Assert.Null(result.FirstInfeasibleSequence);
    }
}
