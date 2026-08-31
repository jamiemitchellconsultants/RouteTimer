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
using RouteTimer.Services.Models;
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
        var before = Snapshot(baseline);
        // Climbs only, so the rest of the route is untouched and the direction is unambiguous.
        var definition = new SegmentGainsDefinition([new SegmentGainsRule(0.01, null, null, null, null, null, 1.10, null)]);
        var handler = new SegmentGainsHandler(_predictor);

        var computation = handler.Run(context, definition, CancellationToken.None);

        AssertWellFormed(baseline, computation, handler, SegmentGainsHandler.AlgorithmVersion);
        AssertSameResult(before, baseline);
        AssertSameResult(computation.Adjusted, handler.Run(context, definition, CancellationToken.None).Adjusted);

        var climbs = baseline.Segments.Select((segment, index) => (segment, index))
            .Where(pair => pair.segment.Gradient >= 0.01)
            .ToList();
        if (climbs.Count > 0)
        {
            Assert.All(climbs, pair => Assert.True(
                computation.Adjusted.Segments[pair.index].PowerWatts > pair.segment.PowerWatts,
                $"Sequence {pair.segment.Sequence} at {pair.segment.Gradient:P1} did not gain power."));
        }

        Assert.True(
            computation.Adjusted.MovingTime <= baseline.MovingTime,
            "More power on the climbs must not make the route slower.");
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void TimeTarget_runs_on_every_fixture_and_moves_towards_the_target(string fixture)
    {
        var (context, baseline) = BuildContext(fixture);
        var before = Snapshot(baseline);
        double target = baseline.MovingTime.TotalSeconds * 0.95;
        var definition = new TimeTargetDefinition(target, TimeTargetDistribution.Proportional, null, true);
        var handler = new TimeTargetHandler(_predictor);

        var computation = handler.Run(context, definition, CancellationToken.None);

        AssertWellFormed(baseline, computation, handler, TimeTargetHandler.AlgorithmVersion);
        AssertSameResult(before, baseline);
        AssertSameResult(computation.Adjusted, handler.Run(context, definition, CancellationToken.None).Adjusted);
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
        var before = Snapshot(baseline);
        var definition = new NpIfTargetDefinition(0.85, 250, NpIfScalingMode.Proportional);
        var handler = new NpIfTargetHandler(_predictor);

        var computation = handler.Run(context, definition, CancellationToken.None);

        AssertWellFormed(baseline, computation, handler, NpIfTargetHandler.AlgorithmVersion);
        AssertSameResult(before, baseline);
        AssertSameResult(computation.Adjusted, handler.Run(context, definition, CancellationToken.None).Adjusted);

        var report = (NpIfTargetReport)computation.Report;
        Assert.Equal(212.5, report.TargetNormalizedPowerWatts, 9);
        Assert.True(report.AchievedNormalizedPowerWatts > 0);
        Assert.Equal(report.AchievedNormalizedPowerWatts / 250, report.AchievedIntensityFactor, 9);
        Assert.True(report.EvaluationCount <= 40, $"{report.EvaluationCount} evaluations exceeds the search budget.");

        // The adjusted NP is at least as close to the target as leaving the baseline alone would be.
        var baselineNp = NormalizedPowerCalculator.CalculateNormalizedPower(
            baseline.Segments.Select(segment => segment.PowerWatts).ToList(),
            baseline.Segments.Select(segment => segment.MovingTime.TotalSeconds).ToList());
        Assert.True(
            Math.Abs(report.AchievedNormalizedPowerWatts - report.TargetNormalizedPowerWatts)
                <= Math.Abs(baselineNp - report.TargetNormalizedPowerWatts) + 1e-6,
            $"Achieved NP {report.AchievedNormalizedPowerWatts:F1} W is further from target than baseline {baselineNp:F1} W.");

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
        var before = Snapshot(baseline);
        var definition = new ZoneShiftDefinition(
            ZoneThresholdMode.FtpBased,
            250,
            [new ZoneAssignment(true, null, null, 3, ZonePlacement.Midpoint)]);

        var handler = new ZoneShiftHandler(_predictor);
        var computation = handler.Run(context, definition, CancellationToken.None);

        AssertWellFormed(baseline, computation, handler, ZoneShiftHandler.AlgorithmVersion);
        AssertSameResult(before, baseline);
        AssertSameResult(computation.Adjusted, handler.Run(context, definition, CancellationToken.None).Adjusted);
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
        var before = Snapshot(baseline);
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

        var counting = new CountingPredictor(_predictor);
        var handler = new MatchBurningHandler(counting);
        var computation = handler.Run(context, definition, CancellationToken.None);

        AssertWellFormed(baseline, computation, handler, MatchBurningHandler.AlgorithmVersion);
        AssertSameResult(before, baseline);

        // Refinement replans once and re-simulates only if the plan changed: never more than two.
        Assert.InRange(counting.Calls, 1, 2);

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

        // Riding above critical power can only spend W-prime, never bank it.
        for (var index = 1; index < computation.Adjusted.Segments.Count; index++)
        {
            var segment = computation.Adjusted.Segments[index];
            if (segment.PowerWatts <= report.CriticalPowerWatts) continue;

            var previous = computation.Annotations[computation.Adjusted.Segments[index - 1].Sequence].WPrimeBalanceJoules;
            var current = computation.Annotations[segment.Sequence].WPrimeBalanceJoules;
            Assert.True(
                current <= previous,
                $"Sequence {segment.Sequence} rode {segment.PowerWatts:F0} W above CP but W-prime rose from {previous:F0} to {current:F0} J.");
        }
    }

    /// <summary>Counts how many times a handler replays the route, for the refinement cap.</summary>
    private sealed class CountingPredictor(IRoutePredictor inner) : IRoutePredictor
    {
        public int Calls { get; private set; }

        public PredictionResult Predict(
            PredictionRoute route,
            RiderProfile profile,
            RiderModel model,
            IPowerTargetPolicy? powerTargetPolicy = null,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return inner.Predict(route, profile, model, powerTargetPolicy, cancellationToken);
        }
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

        var handler = new MatchBurningHandler(_predictor);
        var computation = handler.Run(context, definition, CancellationToken.None);

        AssertWellFormed(baseline, computation, handler, MatchBurningHandler.AlgorithmVersion);
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

        var handler = new ZoneShiftHandler(_predictor);
        var computation = handler.Run(context, definition, CancellationToken.None);

        AssertWellFormed(baseline, computation, handler, ZoneShiftHandler.AlgorithmVersion);
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

    private static void AssertWellFormed(
        PredictionResult baseline,
        PacingStrategyComputation computation,
        IPacingStrategyHandler handler,
        string expectedAlgorithmVersion)
    {
        Assert.Equal(expectedAlgorithmVersion, computation.AlgorithmVersion);

        // The report has to survive canonicalization: a NaN or infinity anywhere in it fails the whole
        // adjustment at publication, not here.
        var reportJson = handler.CanonicalizeReport(computation.Report);
        Assert.False(string.IsNullOrWhiteSpace(reportJson));
        Assert.DoesNotContain("NaN", reportJson, StringComparison.Ordinal);
        Assert.DoesNotContain("Infinity", reportJson, StringComparison.Ordinal);

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

    /// <summary>
    /// Field-by-field, because these are collection-backed records: record equality would compare the
    /// collections by reference and report a false difference between two identical runs.
    /// </summary>
    private static void AssertSameResult(PredictionResult expected, PredictionResult actual)
    {
        Assert.Equal(expected.MovingTime, actual.MovingTime);
        Assert.Equal(expected.Confidence, actual.Confidence);
        Assert.Equal(expected.Warnings, actual.Warnings);
        Assert.Equal(expected.Segments.Count, actual.Segments.Count);
        for (var index = 0; index < expected.Segments.Count; index++)
        {
            var left = expected.Segments[index];
            var right = actual.Segments[index];
            Assert.Equal(left.Sequence, right.Sequence);
            Assert.Equal(left.DistanceMetres, right.DistanceMetres);
            Assert.Equal(left.Gradient, right.Gradient);
            Assert.Equal(left.PowerWatts, right.PowerWatts);
            Assert.Equal(left.SpeedMetresPerSecond, right.SpeedMetresPerSecond);
            Assert.Equal(left.MovingTime, right.MovingTime);
            Assert.Equal(left.Confidence, right.Confidence);
        }
    }

    private static PredictionResult Snapshot(PredictionResult result) => new(
        result.Segments.ToList(), result.MovingTime, result.Confidence, result.Warnings.ToList());

    // Break caught: the fixture matrix documented in backtesting.md drifts from what the harness
    // actually builds, so the recorded evidence describes routes nobody tested.
    [Theory]
    [InlineData(PacingFixtures.FlatShort, 12, 50.0)]
    [InlineData(PacingFixtures.FlatLong, 120, 100.0)]
    [InlineData(PacingFixtures.Rolling, 80, 80.0)]
    [InlineData(PacingFixtures.Mountainous, 100, 250.0)]
    [InlineData(PacingFixtures.Fractional, 31, 37.0)]
    public void The_fixture_matrix_matches_its_documented_shape(string fixture, int segments, double segmentMetres)
    {
        var route = PacingFixtures.BuildRoute(fixture);

        Assert.Equal(segments, route.Segments.Count);
        Assert.All(route.Segments, segment => Assert.Equal(segmentMetres, segment.SegmentDistanceMetres, 9));
        Assert.Equal(segments * segmentMetres, route.DistanceMetres, 6);
    }

    [Fact]
    public void The_fixture_matrix_covers_the_documented_gradient_and_duration_range()
    {
        var (_, flatShort) = BuildContext(PacingFixtures.FlatShort);
        var (_, mountainous) = BuildContext(PacingFixtures.Mountainous);
        var (_, fractional) = BuildContext(PacingFixtures.Fractional);

        Assert.True(flatShort.MovingTime.TotalMinutes < 10, "flat-short must stay under ten minutes.");
        Assert.True(mountainous.MovingTime.TotalMinutes > 30, "some fixture must cross the 30-minute duration band.");
        Assert.Contains(
            fractional.Segments,
            segment => Math.Abs(segment.MovingTime.TotalSeconds - Math.Round(segment.MovingTime.TotalSeconds)) > 1e-6);

        var rolling = PacingFixtures.BuildRoute(PacingFixtures.Rolling);
        Assert.Equal([-0.03, 0.0, 0.03, 0.05], rolling.Segments.Take(4).Select(segment => segment.Gradient));
        Assert.Contains(PacingFixtures.BuildRoute(PacingFixtures.Mountainous).Segments, segment => segment.Gradient >= 0.09);
    }

    // Break caught: the model degenerates to a single global figure, so gradient never changes baseline
    // power and every band-interpolation path in the lookup goes untested.
    [Fact]
    public void The_fixture_model_is_a_dense_grid_whose_power_rises_with_gradient()
    {
        var model = PacingFixtures.BuildModel();

        Assert.Equal(
            PowerModelBands.Gradient.Count * PowerModelBands.Duration.Count,
            model.PowerModel.Bands.Count);
        Assert.All(model.PowerModel.Bands, band => Assert.True(band.TypicalWatts > 0));

        var lookup = new PowerLookup(model.PowerModel);
        var flat = lookup.GetWatts(0.0, TimeSpan.FromMinutes(10));
        var climb = lookup.GetWatts(0.075, TimeSpan.FromMinutes(10));
        var late = lookup.GetWatts(0.075, TimeSpan.FromMinutes(200));

        Assert.True(climb.Watts > flat.Watts, "a steeper band must carry more watts.");
        Assert.True(late.Watts < climb.Watts, "the same gradient later in the ride must carry fewer watts.");
        Assert.False(climb.Extrapolated, "a dense grid must not report extrapolation for an in-range query.");
    }

    private static (PacingStrategyContext Context, PredictionResult Baseline) BuildContext(string fixture) =>
        PacingFixtures.BuildContext(fixture);
}
