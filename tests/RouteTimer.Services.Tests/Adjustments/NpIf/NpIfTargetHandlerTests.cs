using System;
using System.Linq;
using System.Threading;
using RouteTimer.Domain.Adjustments;
using RouteTimer.Domain.Adjustments.NpIf;
using RouteTimer.Services.Adjustments.NpIf;
using RouteTimer.Services.Predictions;
using RouteTimer.Services.Routes;
using RouteTimer.Services.Tests.Adjustments;
using Xunit;

namespace RouteTimer.Services.Tests.Adjustments.NpIf;

public class NpIfTargetHandlerTests
{
    private readonly RoutePredictor _predictor = new(new DescentSpeedLimiter());

    [Theory]
    [InlineData(0)]
    [InlineData(1.500001)]
    public void Definition_rejects_target_if_outside_the_closed_upper_range(double targetIf)
    {
        Assert.Throws<ArgumentException>(() =>
            new NpIfTargetDefinition(targetIf, 300, NpIfScalingMode.Proportional));
    }

    [Fact]
    public void Lowering_the_target_intensity_lowers_the_achieved_normalized_power()
    {
        var (context, _) = PacingFixtures.BuildContext(PacingFixtures.Rolling);
        var handler = new NpIfTargetHandler(_predictor);

        var easy = (NpIfTargetReport)handler
            .Run(context, new NpIfTargetDefinition(0.70, 250, NpIfScalingMode.Proportional), CancellationToken.None)
            .Report;
        var hard = (NpIfTargetReport)handler
            .Run(context, new NpIfTargetDefinition(1.00, 250, NpIfScalingMode.Proportional), CancellationToken.None)
            .Report;

        Assert.Equal(175, easy.TargetNormalizedPowerWatts, 9);
        Assert.Equal(250, hard.TargetNormalizedPowerWatts, 9);
        Assert.True(
            easy.AchievedNormalizedPowerWatts < hard.AchievedNormalizedPowerWatts,
            $"Easy NP {easy.AchievedNormalizedPowerWatts:F1} W should be below hard NP {hard.AchievedNormalizedPowerWatts:F1} W.");
        Assert.True(easy.SelectedParameter < hard.SelectedParameter);
    }

    [Fact]
    public void Additive_mode_shifts_every_segment_by_the_selected_offset()
    {
        var (context, baseline) = PacingFixtures.BuildContext(PacingFixtures.FlatLong);
        var definition = new NpIfTargetDefinition(0.95, 250, NpIfScalingMode.Additive);

        var computation = new NpIfTargetHandler(_predictor).Run(context, definition, CancellationToken.None);
        var report = (NpIfTargetReport)computation.Report;

        Assert.Equal(NpIfScalingMode.Additive, report.Mode);
        foreach (var (adjusted, baselineSegment) in computation.Adjusted.Segments.Zip(baseline.Segments))
        {
            Assert.Equal(Math.Max(0, baselineSegment.PowerWatts + report.SelectedParameter), adjusted.PowerWatts, 6);
        }
    }

    [Fact]
    public void A_short_route_is_flagged_as_an_approximation()
    {
        var (context, baseline) = PacingFixtures.BuildContext(PacingFixtures.FlatShort);
        Assert.True(baseline.MovingTime.TotalSeconds < 600, "The flat-short fixture must stay under ten minutes.");

        var computation = new NpIfTargetHandler(_predictor)
            .Run(context, new NpIfTargetDefinition(0.85, 250, NpIfScalingMode.Proportional), CancellationToken.None);

        Assert.Contains(AdjustmentWarningCodes.NpIfShortRouteApproximation, computation.Warnings);
        Assert.True(((NpIfTargetReport)computation.Report).UsedShortRouteApproximation);
    }
}
