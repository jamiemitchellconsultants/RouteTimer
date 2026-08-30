using System;
using System.Linq;
using System.Threading;
using RouteTimer.Domain.Adjustments;
using RouteTimer.Domain.Adjustments.TimeTarget;
using RouteTimer.Services.Adjustments.TimeTarget;
using RouteTimer.Services.Predictions;
using RouteTimer.Services.Routes;
using RouteTimer.Services.Tests.Adjustments;
using Xunit;

namespace RouteTimer.Services.Tests.Adjustments.TimeTarget;

public class TimeTargetHandlerTests
{
    private readonly RoutePredictor _predictor = new(new DescentSpeedLimiter());

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
        var (context, baseline) = PacingFixtures.BuildContext(PacingFixtures.Mountainous);
        var definition = new TimeTargetDefinition(
            baseline.MovingTime.TotalSeconds * 0.90,
            TimeTargetDistribution.ClimbFocused,
            1.8,
            includeFeasibilityReport: true);

        var report = (TimeTargetReport)new TimeTargetHandler(_predictor)
            .Run(context, definition, CancellationToken.None).Report;

        double climbFraction = baseline.Segments.Where(segment => segment.Gradient >= 0.03)
            .Sum(segment => segment.MovingTime.TotalSeconds) / baseline.MovingTime.TotalSeconds;
        Assert.True(climbFraction is > 0 and < 1, "The mountainous fixture must mix climbs and non-climbs.");

        // The reported per-band scales are a redistribution of the outer scale, not an extra gain.
        Assert.Equal(
            report.SelectedOuterScale,
            (climbFraction * report.SelectedClimbScale) + ((1 - climbFraction) * report.SelectedOtherScale),
            9);
        Assert.True(report.SelectedClimbScale > report.SelectedOtherScale, "A climb bias above 1.0 must load the climbs harder.");
    }

    [Fact]
    public void Climb_focused_on_a_route_with_no_climbs_warns_and_falls_back_to_proportional()
    {
        var (context, baseline) = PacingFixtures.BuildContext(PacingFixtures.FlatLong);
        var definition = new TimeTargetDefinition(
            baseline.MovingTime.TotalSeconds * 0.95,
            TimeTargetDistribution.ClimbFocused,
            1.5,
            includeFeasibilityReport: false);

        var computation = new TimeTargetHandler(_predictor).Run(context, definition, CancellationToken.None);
        var report = (TimeTargetReport)computation.Report;

        Assert.Contains(AdjustmentWarningCodes.TimeTargetNoClimbs, computation.Warnings);
        Assert.Equal(report.SelectedOuterScale, report.SelectedClimbScale, 9);
        Assert.Equal(report.SelectedOuterScale, report.SelectedOtherScale, 9);
    }

    [Fact]
    public void An_unreachable_target_reports_infeasible_rather_than_failing()
    {
        var (context, _) = PacingFixtures.BuildContext(PacingFixtures.Mountainous);
        var definition = new TimeTargetDefinition(60, TimeTargetDistribution.Proportional, null, true);

        var computation = new TimeTargetHandler(_predictor).Run(context, definition, CancellationToken.None);
        var report = (TimeTargetReport)computation.Report;

        Assert.False(report.Converged);
        Assert.Contains(AdjustmentWarningCodes.TimeTargetInfeasible, computation.Warnings);
        Assert.Equal(TimeTargetFeasibilityVerdict.Impossible, report.Verdict);
        Assert.True(report.AchievedMovingSeconds > report.TargetMovingSeconds);
    }
}
