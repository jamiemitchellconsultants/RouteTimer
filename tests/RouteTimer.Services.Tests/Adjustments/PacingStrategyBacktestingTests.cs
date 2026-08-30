using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using RouteTimer.Domain.Adjustments;
using RouteTimer.Domain.Adjustments.MatchBurning;
using RouteTimer.Domain.Adjustments.NpIf;
using RouteTimer.Domain.Adjustments.SegmentGains;
using RouteTimer.Domain.Adjustments.TimeTarget;
using RouteTimer.Domain.Adjustments.Zones;
using RouteTimer.Domain.Models;
using RouteTimer.Domain.Physics;
using RouteTimer.Domain.Predictions;
using RouteTimer.Domain.Profile;
using RouteTimer.Services.Adjustments;
using RouteTimer.Services.Adjustments.MatchBurning;
using RouteTimer.Services.Adjustments.NpIf;
using RouteTimer.Services.Adjustments.SegmentGains;
using RouteTimer.Services.Adjustments.TimeTarget;
using RouteTimer.Services.Adjustments.Zones;
using RouteTimer.Services.Predictions;
using RouteTimer.Services.Routes;
using Xunit;

namespace RouteTimer.Services.Tests.Adjustments;

/// <summary>
/// Deterministic backtesting across the synthetic fixture matrix documented in
/// docs/pacing-strategies/backtesting.md. Every strategy runs on every fixture and must produce a
/// well-formed computation: sequence parity with the baseline, finite non-negative power and speed,
/// positive per-segment time, known warning codes, and a non-empty algorithm version.
/// </summary>
public class PacingStrategyBacktestingTests
{
    private readonly RoutePredictor _predictor = new(new DescentSpeedLimiter());

    public static TheoryData<string> Fixtures => [.. PacingFixtures.All];

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void SegmentGains_runs_on_every_fixture(string fixture)
    {
        var (context, baseline) = BuildContext(fixture);
        var definition = new SegmentGainsDefinition([new SegmentGainsRule(-0.01, 0.01, null, null, null, null, 1.10, null)]);

        var computation = new SegmentGainsHandler(_predictor).Run(context, definition, CancellationToken.None);

        AssertWellFormed(baseline, computation);
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void TimeTarget_runs_on_every_fixture_and_moves_towards_the_target(string fixture)
    {
        var (context, baseline) = BuildContext(fixture);
        double target = baseline.MovingTime.TotalSeconds * 0.95;
        var definition = new TimeTargetDefinition(target, TimeTargetDistribution.Proportional, null, true);

        var computation = new TimeTargetHandler(_predictor).Run(context, definition, CancellationToken.None);

        AssertWellFormed(baseline, computation);
        var report = (TimeTargetReport)computation.Report;
        Assert.Equal(target, report.TargetMovingSeconds, 9);
        Assert.True(
            report.AbsoluteMissSeconds <= Math.Abs(baseline.MovingTime.TotalSeconds - target),
            $"Adjusted result ({report.AchievedMovingSeconds:F1}s) is further from the target than the baseline.");
        // The bounded search caps itself at 40 route simulations per adjustment.
        Assert.True(report.EvaluationCount <= 40, $"{report.EvaluationCount} evaluations exceeds the search budget.");
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void NpIfTarget_runs_on_every_fixture_and_reports_its_achieved_intensity(string fixture)
    {
        var (context, baseline) = BuildContext(fixture);
        var definition = new NpIfTargetDefinition(0.85, 250, NpIfScalingMode.Proportional);

        var computation = new NpIfTargetHandler(_predictor).Run(context, definition, CancellationToken.None);

        AssertWellFormed(baseline, computation);
        var report = (NpIfTargetReport)computation.Report;
        Assert.Equal(212.5, report.TargetNormalizedPowerWatts, 9);
        Assert.True(report.AchievedNormalizedPowerWatts > 0);
        Assert.Equal(report.AchievedNormalizedPowerWatts / 250, report.AchievedIntensityFactor, 9);

        // Either the search hit the target, or it says so.
        Assert.True(
            report.Converged || computation.Warnings.Contains(AdjustmentWarningCodes.NpIfClosestFeasible),
            "A non-converged NP/IF search must warn that it returned the closest feasible result.");

        var expectedNp = NormalizedPowerCalculator.CalculateNormalizedPower(
            computation.Adjusted.Segments.Select(segment => segment.PowerWatts).ToList(),
            computation.Adjusted.Segments.Select(segment => segment.MovingTime.TotalSeconds).ToList());
        Assert.Equal(expectedNp, report.AchievedNormalizedPowerWatts, 6);
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void ZoneShift_runs_on_every_fixture_and_annotates_every_segment(string fixture)
    {
        var (context, baseline) = BuildContext(fixture);
        var definition = new ZoneShiftDefinition(
            ZoneThresholdMode.FtpBased,
            250,
            [new ZoneAssignment(true, null, null, 3, ZonePlacement.Midpoint)]);

        var computation = new ZoneShiftHandler(_predictor).Run(context, definition, CancellationToken.None);

        AssertWellFormed(baseline, computation);
        Assert.Equal(baseline.Segments.Count, computation.Annotations.Count);
        Assert.All(computation.Annotations.Values, annotation => Assert.NotNull(annotation.ZoneNumber));

        var report = (ZoneShiftReport)computation.Report;
        Assert.Equal(100.0, report.Distribution.Sum(entry => entry.Percentage), 6);
        Assert.Equal(baseline.Segments.Count, report.AssignmentMatchCounts.Single());
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void MatchBurning_runs_on_every_fixture_and_keeps_its_balance_inside_capacity(string fixture)
    {
        var (context, baseline) = BuildContext(fixture);
        var definition = new MatchBurningDefinition(
            250,
            20_000,
            [new MatchBurnWindow(0.02, null, null, null, null, null, null, 1.20, null)],
            120,
            0.80,
            300,
            0.70,
            includeFatigueReport: true,
            enableRefinement: true);

        var computation = new MatchBurningHandler(_predictor).Run(context, definition, CancellationToken.None);

        AssertWellFormed(baseline, computation);
        var report = (MatchBurningReport)computation.Report;
        Assert.Equal(250, report.CriticalPowerWatts, 9);
        Assert.Equal(CapacityProvenance.Supplied, report.CriticalPowerProvenance);
        Assert.InRange(report.MinimumWPrimeBalanceJoules, 0, 20_000);
        Assert.InRange(report.FinalWPrimeBalanceJoules, 0, 20_000);
        Assert.InRange(report.DepletedFraction, 0, 1);
        Assert.True(report.RefinementEnabled);
        Assert.True(report.RefinementRan);

        // Every segment carries its phase and its W' balance.
        Assert.Equal(baseline.Segments.Count, computation.Annotations.Count);
        Assert.All(computation.Annotations.Values, annotation =>
        {
            Assert.NotNull(annotation.StrategyPhase);
            Assert.NotNull(annotation.WPrimeBalanceJoules);
            Assert.InRange(annotation.WPrimeBalanceJoules!.Value, 0, 20_000);
        });
        Assert.Equal(
            report.Windows.Single().MatchedSegmentCount,
            computation.Annotations.Values.Count(annotation => annotation.StrategyPhase == "burn"));
    }

    [Fact]
    public void MatchBurning_windows_that_match_nothing_are_reported_as_a_warning()
    {
        var (context, baseline) = BuildContext(PacingFixtures.FlatShort);
        var definition = new MatchBurningDefinition(
            250,
            20_000,
            [new MatchBurnWindow(0.20, null, null, null, null, null, null, 1.20, null)],
            0,
            0.80,
            0,
            0.70,
            includeFatigueReport: false,
            enableRefinement: false);

        var computation = new MatchBurningHandler(_predictor).Run(context, definition, CancellationToken.None);

        AssertWellFormed(baseline, computation);
        Assert.Contains(AdjustmentWarningCodes.MatchBurningWindowNoMatch, computation.Warnings);
        Assert.Equal(0, ((MatchBurningReport)computation.Report).Windows.Single().MatchedSegmentCount);
    }

    /// <summary>
    /// Regression: zone 1's lower-bound target used to resolve to a flat 5 W, which the physics
    /// cannot hold on a climb - the replay threw and the whole adjustment failed.
    /// </summary>
    [Fact]
    public void ZoneShift_zone_one_lower_bound_completes_on_a_mountainous_route()
    {
        var (context, baseline) = BuildContext(PacingFixtures.Mountainous);
        var definition = new ZoneShiftDefinition(
            ZoneThresholdMode.FtpBased,
            250,
            [new ZoneAssignment(true, null, null, 1, ZonePlacement.LowerBound)]);

        var computation = new ZoneShiftHandler(_predictor).Run(context, definition, CancellationToken.None);

        AssertWellFormed(baseline, computation);
        Assert.All(computation.Adjusted.Segments, segment =>
            Assert.True(segment.PowerWatts >= PowerZoneResolver.MinimumTargetWatts(250)));
    }

    /// <summary>
    /// Regression: per-assignment match counts are addressed by the index the caller submitted,
    /// even though the all-segments fallback is always matched last.
    /// </summary>
    [Fact]
    public void ZoneShift_match_counts_follow_the_submitted_assignment_order()
    {
        var (context, _) = BuildContext(PacingFixtures.Mountainous);
        var fallbackFirst = new ZoneShiftDefinition(
            ZoneThresholdMode.FtpBased,
            250,
            [
                new ZoneAssignment(true, null, null, 3, ZonePlacement.Midpoint),
                new ZoneAssignment(false, 0.05, null, 4, ZonePlacement.Midpoint),
            ]);

        var computation = new ZoneShiftHandler(_predictor).Run(context, fallbackFirst, CancellationToken.None);
        var counts = ((ZoneShiftReport)computation.Report).AssignmentMatchCounts;

        int steepSegments = context.Route.Segments.Count(segment => segment.Gradient >= 0.05);
        Assert.True(steepSegments > 0, "The mountainous fixture must contain segments at or above 5%.");

        // Index 1 is the gradient rule the caller sent second; it still wins over the fallback.
        Assert.Equal(steepSegments, counts[1]);
        Assert.Equal(context.Route.Segments.Count - steepSegments, counts[0]);
    }

    private static void AssertWellFormed(PredictionResult baseline, PacingStrategyComputation computation)
    {
        Assert.Equal(
            baseline.Segments.Select(segment => segment.Sequence),
            computation.Adjusted.Segments.Select(segment => segment.Sequence));
        Assert.All(computation.Adjusted.Segments, segment =>
        {
            Assert.True(double.IsFinite(segment.PowerWatts) && segment.PowerWatts >= 0);
            Assert.True(double.IsFinite(segment.SpeedMetresPerSecond) && segment.SpeedMetresPerSecond >= 0);
            Assert.True(segment.MovingTime > TimeSpan.Zero);
        });
        Assert.False(string.IsNullOrWhiteSpace(computation.AlgorithmVersion));
        Assert.All(computation.Warnings, warning => Assert.True(
            AdjustmentWarningCodes.IsKnown(warning),
            $"Unknown warning code '{warning}' would be rejected at publication."));

        var sequences = computation.Adjusted.Segments.Select(segment => segment.Sequence).ToHashSet();
        Assert.All(computation.Annotations.Keys, sequence => Assert.Contains(sequence, sequences));
    }

    private static (PacingStrategyContext Context, PredictionResult Baseline) BuildContext(string fixture) =>
        PacingFixtures.BuildContext(fixture);
}
