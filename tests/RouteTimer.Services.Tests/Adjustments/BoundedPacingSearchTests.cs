using System;
using RouteTimer.Services.Adjustments;
using Xunit;

namespace RouteTimer.Services.Tests.Adjustments;

public class BoundedPacingSearchTests
{
    [Fact]
    public void Monotonic_function_finds_exact_target()
    {
        double m = BoundedPacingSearch.FindMultiplier(0.5, 2.0, 250, m => 200 * m);
        Assert.Equal(1.25, m, 3);
    }

    [Fact]
    public void Non_monotonic_function_finds_closest_local_minimum()
    {
        double m = BoundedPacingSearch.FindMultiplier(0.5, 2.0, 288, m => 200 * Math.Pow(m, 2));
        Assert.Equal(1.2, m, 2);
    }

    [Fact]
    public void Invalid_bounds_or_null_evaluator_throws()
    {
        Assert.Throws<ArgumentNullException>(() => BoundedPacingSearch.FindMultiplier(0.5, 2.0, 100, null!));
        Assert.Throws<ArgumentException>(() => BoundedPacingSearch.FindMultiplier(2.0, 0.5, 100, m => m));
    }
}
