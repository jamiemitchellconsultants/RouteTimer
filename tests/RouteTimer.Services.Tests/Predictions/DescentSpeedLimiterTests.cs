using RouteTimer.Domain.Models;
using RouteTimer.Services.Predictions;

namespace RouteTimer.Services.Tests.Predictions;

public sealed class DescentSpeedLimiterTests
{
    [Theory]
    [InlineData(-.02, 0, 3)]
    [InlineData(-.04, 0, 3)]
    [InlineData(-.0400000001, 0, 6)]
    [InlineData(-.08, 0, 6)]
    [InlineData(-.0800000001, 0, 9)]
    [InlineData(-.03, .0019999999, 3)]
    [InlineData(-.03, .002, 4)]
    [InlineData(-.03, .0099999999, 4)]
    [InlineData(-.03, .01, 5)]
    public void Resolve_maps_exact_grade_and_curvature_boundaries_to_one_cell(
        double gradient,
        double curvature,
        double expectedCap)
    {
        var result = new DescentSpeedLimiter().Resolve(gradient, curvature, DistinctLearnedGrid());

        Assert.Equal(expectedCap, result.SpeedCapMetresPerSecond, 12);
        Assert.Equal(ConfidenceLevel.High, result.Confidence);
        Assert.False(result.UsedFallback);
    }

    [Theory]
    [InlineData(-.03, 0, 13)]
    [InlineData(-.06, 0, 16)]
    [InlineData(-.10, 0, 18)]
    [InlineData(-.10, .02, 10)]
    [InlineData(-.03, .01, 13)]
    [InlineData(-.06, .01, 14.142135623730951)]
    public void Resolve_uses_literal_conservative_grade_and_curvature_caps(
        double gradient,
        double curvature,
        double expectedCap)
    {
        var result = new DescentSpeedLimiter().Resolve(gradient, curvature, DescentLimitModel.Conservative);

        Assert.Equal(expectedCap, result.SpeedCapMetresPerSecond, 12);
        Assert.Equal(ConfidenceLevel.Low, result.Confidence);
        Assert.True(result.UsedFallback);
    }

    [Fact]
    public void Resolve_does_not_apply_grade_target_as_a_hard_cap_to_learned_cell()
    {
        var model = Grid((grade, curvature) =>
            new DescentLimitCell(grade, curvature, 17, TimeSpan.FromMinutes(20), 3, ConfidenceLevel.High, false));

        var result = new DescentSpeedLimiter().Resolve(-.03, 0, model);

        Assert.Equal(17, result.SpeedCapMetresPerSecond, 12);
        Assert.False(result.UsedFallback);
    }

    [Fact]
    public void Resolve_always_clamps_a_learned_cell_with_actual_route_curvature()
    {
        var model = Grid((grade, curvature) =>
            new DescentLimitCell(grade, curvature, 19, TimeSpan.FromMinutes(20), 3, ConfidenceLevel.High, false));

        var result = new DescentSpeedLimiter().Resolve(-.06, .008, model);

        Assert.Equal(15.811388300841896, result.SpeedCapMetresPerSecond, 12);
        Assert.False(result.UsedFallback);
    }

    [Fact]
    public void Resolve_lets_extreme_actual_curvature_override_the_two_metre_floor()
    {
        var model = Grid((grade, curvature) =>
            new DescentLimitCell(grade, curvature, 19, TimeSpan.FromMinutes(20), 3, ConfidenceLevel.High, false));

        var result = new DescentSpeedLimiter().Resolve(-.10, .8, model);

        Assert.Equal(1.5811388300841898, result.SpeedCapMetresPerSecond, 12);
        Assert.False(result.UsedFallback);
    }

    [Fact]
    public void Resolve_uses_no_cap_for_grade_above_descent_grid()
    {
        var result = new DescentSpeedLimiter().Resolve(-.0199999999, 0, DescentLimitModel.Conservative);

        Assert.True(double.IsPositiveInfinity(result.SpeedCapMetresPerSecond));
        Assert.Equal(ConfidenceLevel.High, result.Confidence);
        Assert.False(result.UsedFallback);
    }

    [Theory]
    [InlineData(-.06, -.001, 16)]
    [InlineData(-.06, double.NaN, 16)]
    public void Resolve_uses_conservative_fallback_for_out_of_grid_curvature(
        double gradient,
        double curvature,
        double expectedCap)
    {
        var result = new DescentSpeedLimiter().Resolve(gradient, curvature, DistinctLearnedGrid());

        Assert.Equal(expectedCap, result.SpeedCapMetresPerSecond, 12);
        Assert.Equal(ConfidenceLevel.Low, result.Confidence);
        Assert.True(result.UsedFallback);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(double.PositiveInfinity)]
    public void Resolve_uses_no_cap_for_non_finite_gradient(double gradient)
    {
        var result = new DescentSpeedLimiter().Resolve(gradient, 0, DescentLimitModel.Conservative);

        Assert.True(double.IsPositiveInfinity(result.SpeedCapMetresPerSecond));
        Assert.Equal(ConfidenceLevel.High, result.Confidence);
        Assert.False(result.UsedFallback);
    }

    private static DescentLimitModel DistinctLearnedGrid()
    {
        var caps = new Dictionary<(string Grade, string Curvature), double>
        {
            [("mild", "straight")] = 3,
            [("mild", "moderate")] = 4,
            [("mild", "tight")] = 5,
            [("medium", "straight")] = 6,
            [("medium", "moderate")] = 7,
            [("medium", "tight")] = 8,
            [("steep", "straight")] = 9,
            [("steep", "moderate")] = 10,
            [("steep", "tight")] = 11
        };
        return Grid((grade, curvature) =>
            new DescentLimitCell(grade, curvature, caps[(grade, curvature)], TimeSpan.FromMinutes(20), 3, ConfidenceLevel.High, false));
    }

    private static DescentLimitModel Grid(Func<string, string, DescentLimitCell> createCell)
    {
        var grades = new[] { "mild", "medium", "steep" };
        var curvatures = new[] { "straight", "moderate", "tight" };
        return new DescentLimitModel(
            grades.SelectMany(grade => curvatures.Select(curvature => createCell(grade, curvature))).ToArray());
    }
}
