using RouteTimer.Domain.Adjustments;
using RouteTimer.Domain.Adjustments.SegmentGains;
using RouteTimer.Domain.Models;
using RouteTimer.Domain.Physics;
using RouteTimer.Domain.Predictions;
using RouteTimer.Domain.Profile;
using RouteTimer.Services.Adjustments;
using RouteTimer.Services.Adjustments.SegmentGains;
using RouteTimer.Services.Physics;
using RouteTimer.Services.Predictions;
using RouteTimer.Services.Tests.Predictions;

namespace RouteTimer.Services.Tests.Adjustments.SegmentGains;

public sealed class SegmentGainsHandlerTests
{
    // Break caught: a rule bounding more than one of gradient/sequence/distance is silently accepted.
    [Theory]
    [InlineData(.01, .05, 1, null, null, null)] // gradient + sequence
    [InlineData(null, null, 1, 3, 0d, 100d)] // sequence + distance
    [InlineData(.01, null, null, null, 0d, 100d)] // gradient + distance
    public void SegmentGainsRule_rejects_more_than_one_selector(
        double? minGradient, double? maxGradient, int? minSequence, int? maxSequence, double? minDistance, double? maxDistance)
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            new SegmentGainsRule(minGradient, maxGradient, minSequence, maxSequence, minDistance, maxDistance, 1.1, null));

        Assert.Contains("exactly one", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    // Break caught: a rule with no bound at all on any selector is accepted as if it matched everything.
    [Fact]
    public void SegmentGainsRule_rejects_a_rule_with_no_selector_bound()
    {
        Assert.Throws<ArgumentException>(() => new SegmentGainsRule(null, null, null, null, null, null, 1.1, null));
    }

    // Break caught: a rule with both a factor and a delta watts (or neither) is accepted.
    [Theory]
    [InlineData(1.1, 5d)]
    [InlineData(null, null)]
    public void SegmentGainsRule_requires_exactly_one_of_factor_or_delta(double? factor, double? deltaWatts)
    {
        Assert.Throws<ArgumentException>(() => new SegmentGainsRule(.01, null, null, null, null, null, factor, deltaWatts));
    }

    // Break caught: negative deltas are rejected outright instead of being allowed to reduce power (and floor at 10 W).
    [Fact]
    public void SegmentGainsRule_accepts_a_negative_delta_and_floors_the_result_at_ten_watts()
    {
        var rule = new SegmentGainsRule(.01, null, null, null, null, null, null, -195);

        var (watts, clamped) = rule.Apply(200);

        Assert.Equal(10, watts);
        Assert.True(clamped);
    }

    // Break caught: a delta that lands exactly at the floor is reported as clamped even though nothing was cut off.
    [Fact]
    public void SegmentGainsRule_does_not_report_clamping_when_the_result_lands_exactly_on_the_floor()
    {
        var rule = new SegmentGainsRule(.01, null, null, null, null, null, null, -190);

        var (watts, clamped) = rule.Apply(200);

        Assert.Equal(10, watts);
        Assert.False(clamped);
    }

    // Break caught: more than ten rules is accepted instead of rejected.
    [Fact]
    public void SegmentGainsDefinition_rejects_more_than_ten_rules()
    {
        var rules = Enumerable.Range(0, 11)
            .Select(index => new SegmentGainsRule(null, null, index, index, null, null, 1.1, null))
            .ToArray();

        Assert.Throws<ArgumentException>(() => new SegmentGainsDefinition(rules));
    }

    [Fact]
    public void SegmentGainsDefinition_accepts_exactly_ten_rules()
    {
        var rules = Enumerable.Range(0, 10)
            .Select(index => new SegmentGainsRule(null, null, index, index, null, null, 1.1, null))
            .ToArray();

        var definition = new SegmentGainsDefinition(rules);

        Assert.Equal(10, definition.Rules.Count);
    }

    // Break caught: gradient/sequence/distance selectors use exclusive bounds instead of the documented inclusive ones.
    [Theory]
    [InlineData(.02, true)]
    [InlineData(.019999, false)]
    public void Gradient_selector_bound_is_inclusive(double gradient, bool shouldMatch)
    {
        var rule = new SegmentGainsRule(.02, null, null, null, null, null, 2.0, null);
        var segment = new PredictionRouteSegment(1, 51, -2, 0, 100, 100, gradient, 0);

        Assert.Equal(shouldMatch, rule.Matches(segment));
    }

    [Theory]
    [InlineData(2, true)]
    [InlineData(3, false)]
    public void Sequence_selector_bound_is_inclusive(int sequence, bool shouldMatch)
    {
        var rule = new SegmentGainsRule(null, null, 1, 2, null, null, 2.0, null);
        var segment = new PredictionRouteSegment(sequence, 51, -2, 0, 100, 100, .01, 0);

        Assert.Equal(shouldMatch, rule.Matches(segment));
    }

    [Theory]
    [InlineData(200, true)]
    [InlineData(200.0001, false)]
    public void Distance_selector_bound_is_inclusive(double cumulativeDistance, bool shouldMatch)
    {
        var rule = new SegmentGainsRule(null, null, null, null, null, 200, 2.0, null);
        var segment = new PredictionRouteSegment(1, 51, -2, 0, cumulativeDistance, 100, .01, 0);

        Assert.Equal(shouldMatch, rule.Matches(segment));
    }

    // Break caught: a later, broader rule overrides an earlier, more specific rule instead of first-match-wins.
    [Fact]
    public void Run_applies_the_first_matching_rule_in_submitted_order()
    {
        var specific = new SegmentGainsRule(.03, null, null, null, null, null, 1.5, null);
        var broad = new SegmentGainsRule(null, null, 1, 1, null, null, 3.0, null);
        var definition = new SegmentGainsDefinition([specific, broad]);

        var (_, adjusted) = RunSingleSegment(definition, gradient: .05, baselineWatts: 200);

        Assert.Equal(300, adjusted.Segments[0].PowerWatts); // 200 * 1.5, not 200 * 3.0
    }

    // Break caught: a segment matching no rule has its baseline power changed anyway.
    [Fact]
    public void Run_leaves_an_unmatched_segment_at_its_baseline_power()
    {
        var definition = new SegmentGainsDefinition([new SegmentGainsRule(.10, null, null, null, null, null, 2.0, null)]);

        var (baseline, adjusted) = RunSingleSegment(definition, gradient: .01, baselineWatts: 200);

        Assert.Equal(baseline.Segments[0].PowerWatts, adjusted.Segments[0].PowerWatts);
    }

    // Break caught: the no-rules and power-clamped warnings never actually surface from a real run.
    [Fact]
    public void Run_reports_no_rules_and_power_clamped_warnings()
    {
        var noRules = new SegmentGainsDefinition([]);
        var (_, _, noRulesWarnings, _) = RunFull(noRules, gradient: .01, baselineWatts: 200);
        Assert.Contains(AdjustmentWarningCodes.SegmentGainsNoRules, noRulesWarnings);

        var clamping = new SegmentGainsDefinition([new SegmentGainsRule(.01, null, null, null, null, null, null, -1000)]);
        var (_, _, clampedWarnings, _) = RunFull(clamping, gradient: .01, baselineWatts: 200);
        Assert.Contains(AdjustmentWarningCodes.SegmentGainsPowerClamped, clampedWarnings);
        Assert.DoesNotContain(AdjustmentWarningCodes.SegmentGainsNoRules, clampedWarnings);
    }

    // Break caught: matched/unmatched segment counts and per-rule hit counts drift from what actually matched.
    [Fact]
    public void Run_reports_matched_unmatched_counts_and_per_rule_hit_counts()
    {
        var route = PredictionRoute.FromProcessed(PredictionFixtures.Route((100, .02, 0), (100, .05, 0), (100, -.01, 0)));
        var climbRule = new SegmentGainsRule(.03, null, null, null, null, null, 1.5, null);
        var flatRule = new SegmentGainsRule(0, .03, null, null, null, null, 1.1, null);
        var definition = new SegmentGainsDefinition([climbRule, flatRule]);
        var model = PredictionFixtures.Model(new PowerModel([], 200), PhysicalCoefficients.Default, calibrated: true);
        var profile = new RiderProfile(75, 10);
        var baseline = PredictionFixtures.Predict(route, model, profile);
        var handler = new SegmentGainsHandler(new RoutePredictor(new DescentSpeedLimiter()));

        var computation = handler.Run(
            new PacingStrategyContext(Guid.NewGuid(), route, baseline, profile, model), definition, CancellationToken.None);

        var report = Assert.IsType<SegmentGainsReport>(computation.Report);
        Assert.Equal(2, report.MatchedSegmentCount); // segment 1 (flat rule) and segment 2 (climb rule)
        Assert.Equal(1, report.UnmatchedSegmentCount); // segment 3 (descent) matches neither
        Assert.Equal([new SegmentGainsRuleHitCount(0, 1), new SegmentGainsRuleHitCount(1, 1)], report.RuleHitCounts);
    }

    // Break caught: the adjusted route is recomputed with a naive per-segment estimate instead of replaying full physics,
    // so speed and moving time silently stop reflecting the changed power.
    [Fact]
    public void Run_recomputes_speed_and_moving_time_from_the_adjusted_power_via_full_physics()
    {
        var definition = new SegmentGainsDefinition([new SegmentGainsRule(0, 1, null, null, null, null, 3.0, null)]);

        var (baseline, adjusted) = RunSingleSegment(definition, gradient: .02, baselineWatts: 100);

        Assert.Equal(300, adjusted.Segments[0].PowerWatts);
        Assert.NotEqual(baseline.Segments[0].SpeedMetresPerSecond, adjusted.Segments[0].SpeedMetresPerSecond);
        Assert.True(adjusted.Segments[0].SpeedMetresPerSecond > baseline.Segments[0].SpeedMetresPerSecond);
        Assert.True(adjusted.MovingTime < baseline.MovingTime);
    }

    // Break caught: the report's algorithm version is missing or unstable across runs, breaking reproducibility guarantees.
    [Fact]
    public void Run_stamps_a_stable_algorithm_version()
    {
        var definition = new SegmentGainsDefinition([]);

        var (_, _, _, algorithmVersion) = RunFull(definition, gradient: .01, baselineWatts: 200);

        Assert.Equal(SegmentGainsHandler.AlgorithmVersion, algorithmVersion);
    }

    // Break caught: canonicalizing then deserializing a segment-gains definition through its own handler drops rules.
    [Fact]
    public void Handler_canonicalizes_and_deserializes_its_own_definition()
    {
        var definition = new SegmentGainsDefinition([new SegmentGainsRule(.02, .05, null, null, null, null, 1.2, null)]);
        var handler = new SegmentGainsHandler(new RoutePredictor(new DescentSpeedLimiter()));

        var json = handler.Canonicalize(definition);
        var restored = Assert.IsType<SegmentGainsDefinition>(handler.Deserialize(json));

        // SegmentGainsDefinition's Rules is a ReadOnlyCollection, which compares by reference, not
        // structurally - the same quirk documented for PredictionRoute/PredictionResult - so compare
        // the rule sequence element-by-element instead of the whole record.
        Assert.Equal(definition.Rules, restored.Rules);
    }

    private static (PredictionResult Baseline, PredictionResult Adjusted) RunSingleSegment(SegmentGainsDefinition definition, double gradient, double baselineWatts)
    {
        var (baseline, adjusted, _, _) = RunFull(definition, gradient, baselineWatts);
        return (baseline, adjusted);
    }

    private static (PredictionResult Baseline, PredictionResult Adjusted, IReadOnlyList<string> Warnings, string AlgorithmVersion) RunFull(
        SegmentGainsDefinition definition, double gradient, double baselineWatts)
    {
        var route = PredictionRoute.FromProcessed(PredictionFixtures.Route((100, gradient, 0)));
        var model = PredictionFixtures.Model(new PowerModel([], baselineWatts), PhysicalCoefficients.Default, calibrated: true);
        var profile = new RiderProfile(75, 10);
        var baseline = PredictionFixtures.Predict(route, model, profile);
        var handler = new SegmentGainsHandler(new RoutePredictor(new DescentSpeedLimiter()));

        var computation = handler.Run(
            new PacingStrategyContext(Guid.NewGuid(), route, baseline, profile, model), definition, CancellationToken.None);

        return (baseline, computation.Adjusted, computation.Warnings, computation.AlgorithmVersion);
    }
}
