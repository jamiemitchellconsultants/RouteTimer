using System;
using RouteTimer.Services.Adjustments;
using Xunit;

namespace RouteTimer.Services.Tests.Adjustments;

public class BoundedPacingSearchTests
{
    [Fact]
    public void Monotonic_function_finds_exact_target()
    {
        double m = BoundedPacingSearch.FindMultiplier(0.5, 2.0, 250, m => 200 * m, tolerance: 0.001);
        Assert.Equal(1.25, m, 3);
    }

    [Fact]
    public void Non_monotonic_function_finds_closest_local_minimum()
    {
        double m = BoundedPacingSearch.FindMultiplier(0.5, 2.0, 288, m => 200 * Math.Pow(m, 2), tolerance: 0.001);
        Assert.Equal(1.2, m, 2);
    }

    [Fact]
    public void Invalid_bounds_or_null_evaluator_throws()
    {
        Assert.Throws<ArgumentNullException>(() => BoundedPacingSearch.FindMultiplier(0.5, 2.0, 100, null!, tolerance: 0.001));
        Assert.Throws<ArgumentException>(() => BoundedPacingSearch.FindMultiplier(2.0, 0.5, 100, m => m, tolerance: 0.001));
    }

    // Break caught: a loose tolerance lets the coarse grid return its first "close enough" point and
    // stop, so a better point later on the same grid is never evaluated. On the 9-point grid over
    // [0.5, 2.0] the exact answer is 1.25; returning early yields 1.0625 (212.5 against a target of
    // 250). This is what a flat 30-second tolerance did to a 73-second route.
    [Fact]
    public void A_loose_tolerance_still_returns_the_best_point_on_the_grid()
    {
        double found = BoundedPacingSearch.FindMultiplier(0.5, 2.0, 250, m => 200 * m, tolerance: 60);

        Assert.Equal(1.25, found, 4);
    }

    // Break caught: the tolerance is a stopping rule, so a tighter one must never return a worse answer.
    [Fact]
    public void A_tighter_tolerance_is_never_worse_than_a_looser_one()
    {
        double Evaluate(double m) => 200 * Math.Pow(m, 1.5);

        var loose = 200 * Math.Pow(BoundedPacingSearch.FindMultiplier(0.5, 2.0, 250, Evaluate, tolerance: 60), 1.5);
        var tight = 200 * Math.Pow(BoundedPacingSearch.FindMultiplier(0.5, 2.0, 250, Evaluate, tolerance: 0.01), 1.5);

        Assert.True(Math.Abs(tight - 250) <= Math.Abs(loose - 250) + 1e-9);
    }
}
