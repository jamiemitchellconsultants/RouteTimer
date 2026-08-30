using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RouteTimer.Domain.Adjustments;
using RouteTimer.Domain.Adjustments.SegmentGains;
using RouteTimer.Domain.Adjustments.TimeTarget;
using RouteTimer.Services.Routes;
using RouteTimer.Domain.Adjustments.Zones;
using RouteTimer.Domain.Models;
using RouteTimer.Domain.Physics;
using RouteTimer.Domain.Predictions;
using RouteTimer.Domain.Profile;
using RouteTimer.Services.Adjustments;
using RouteTimer.Services.Adjustments.SegmentGains;
using RouteTimer.Services.Adjustments.TimeTarget;
using RouteTimer.Services.Adjustments.Zones;
using RouteTimer.Services.Predictions;
using Xunit;

namespace RouteTimer.Services.Tests.Adjustments;

public class PacingStrategyBacktestingTests
{
    private readonly RoutePredictor _predictor = new(new DescentSpeedLimiter());

    [Fact]
    public void SegmentGains_factor_climb_increases_power_and_keeps_sequence_parity()
    {
        var (route, profile, model, baseline) = BuildFlatShortFixture();
        var context = new PacingStrategyContext(Guid.NewGuid(), route, baseline, profile, model);

        var rule = new SegmentGainsRule(-0.01, 0.01, null, null, null, null, 1.10, null);
        var definition = new SegmentGainsDefinition([rule]);
        var handler = new SegmentGainsHandler(_predictor);

        var computation = handler.Run(context, definition, CancellationToken.None);

        AssertSequenceParity(baseline, computation.Adjusted);
        Assert.True(computation.Adjusted.MovingTime <= baseline.MovingTime);
    }

    [Fact]
    public void TimeTarget_converges_or_reports_infeasible_within_40_evaluations()
    {
        var (route, profile, model, baseline) = BuildFlatShortFixture();
        var context = new PacingStrategyContext(Guid.NewGuid(), route, baseline, profile, model);

        var definition = new TimeTargetDefinition(
            baseline.MovingTime.TotalSeconds * 0.95,
            TimeTargetDistribution.Proportional,
            null,
            true);
        var handler = new TimeTargetHandler(_predictor);

        var computation = handler.Run(context, definition, CancellationToken.None);

        AssertSequenceParity(baseline, computation.Adjusted);
        var report = (TimeTargetReport)computation.Report;
        Assert.True(report.EvaluationCount <= 40);
    }

    [Fact]
    public void ZoneShift_all_segments_annotates_every_segment()
    {
        var (route, profile, model, baseline) = BuildFlatShortFixture();
        var context = new PacingStrategyContext(Guid.NewGuid(), route, baseline, profile, model);

        var assignment = new ZoneAssignment(true, null, null, 3, ZonePlacement.Midpoint);
        var definition = new ZoneShiftDefinition(ZoneThresholdMode.ModelInferred, null, [assignment]);
        var handler = new ZoneShiftHandler(_predictor);

        var computation = handler.Run(context, definition, CancellationToken.None);

        AssertSequenceParity(baseline, computation.Adjusted);
        Assert.Equal(baseline.Segments.Count, computation.Annotations.Count);
        var report = (ZoneShiftReport)computation.Report;
        double sumPct = report.Distribution.Sum(d => d.Percentage);
        Assert.Equal(100.0, sumPct, 1);
    }

    private static void AssertSequenceParity(PredictionResult baseline, PredictionResult adjusted)
    {
        Assert.Equal(
            baseline.Segments.Select(segment => segment.Sequence),
            adjusted.Segments.Select(segment => segment.Sequence));
        Assert.Equal(baseline.Segments.Count, adjusted.Segments.Count);
        Assert.All(adjusted.Segments, segment =>
        {
            Assert.True(double.IsFinite(segment.PowerWatts));
            Assert.True(double.IsFinite(segment.SpeedMetresPerSecond));
            Assert.True(segment.MovingTime > TimeSpan.Zero);
        });
    }

    private (PredictionRoute Route, RiderProfile Profile, RiderModel Model, PredictionResult Baseline) BuildFlatShortFixture()
    {
        var segments = new List<PredictionRouteSegment>();
        double cumDist = 0;
        for (int i = 1; i <= 12; i++)
        {
            cumDist += 50;
            segments.Add(new PredictionRouteSegment(i, 45.0, 7.0 + i * 0.001, 10, cumDist, 50, 0.0, 0.0));
        }
        var route = new PredictionRoute(segments, 600, 0);
        var profile = new RiderProfile(75, 10);
        var model = new RiderModel(new PowerModel([], 200), PhysicalCoefficients.Default, DescentLimitModel.Conservative, true, "v1");
        var baseline = _predictor.Predict(route, profile, model);
        return (route, profile, model, baseline);
    }
}
