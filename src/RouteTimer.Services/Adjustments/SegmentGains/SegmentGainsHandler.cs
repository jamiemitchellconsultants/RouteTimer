using RouteTimer.Domain.Adjustments;
using RouteTimer.Domain.Adjustments.SegmentGains;
using RouteTimer.Domain.Predictions;
using RouteTimer.Services.Predictions;

namespace RouteTimer.Services.Adjustments.SegmentGains;

/// <summary>
/// Segment-specific gains' full vertical slice: replays the baseline route through
/// <see cref="IRoutePredictor"/> with a <see cref="SegmentGainsPolicy"/> substituted for the model's
/// own power lookup, so the adjusted result's speed and moving time come from the same physics as the
/// baseline rather than a naive per-segment recompute.
/// </summary>
public sealed class SegmentGainsHandler(IRoutePredictor routePredictor) : IPacingStrategyHandler
{
    public const string AlgorithmVersion = "segment-gains-v1";

    public PacingStrategyType Type => PacingStrategyType.SegmentSpecificGains;

    public string Canonicalize(PacingStrategyDefinition strategy) =>
        PacingStrategyJson.Canonicalize((SegmentGainsDefinition)strategy);

    public PacingStrategyDefinition Deserialize(string canonicalJson) =>
        PacingStrategyJson.Deserialize<SegmentGainsDefinition>(canonicalJson);

    public string CanonicalizeReport(PacingStrategyReport report) =>
        PacingStrategyJson.CanonicalizeReport((SegmentGainsReport)report);

    public PacingStrategyComputation Run(PacingStrategyContext context, PacingStrategyDefinition strategy, CancellationToken cancellationToken)
    {
        var definition = (SegmentGainsDefinition)strategy;
        var policy = new SegmentGainsPolicy(definition.Rules);
        var adjusted = routePredictor.Predict(context.Route, context.Profile, context.Model, policy, cancellationToken);

        var warnings = new List<string>();
        if (definition.Rules.Count == 0) warnings.Add(AdjustmentWarningCodes.SegmentGainsNoRules);
        if (policy.AnyClamped) warnings.Add(AdjustmentWarningCodes.SegmentGainsPowerClamped);

        var (baselineSpeed, baselinePower) = RouteAverages(context.Route, context.Baseline);
        var (adjustedSpeed, adjustedPower) = RouteAverages(context.Route, adjusted);
        var ruleHitCounts = policy.HitCounts
            .Select((count, index) => new SegmentGainsRuleHitCount(index, count))
            .ToArray();

        var report = new SegmentGainsReport(
            policy.MatchedSegmentCount,
            policy.UnmatchedSegmentCount,
            ruleHitCounts,
            (adjusted.MovingTime - context.Baseline.MovingTime).TotalSeconds,
            adjustedSpeed - baselineSpeed,
            adjustedPower - baselinePower);

        return new PacingStrategyComputation(adjusted, report, new Dictionary<int, PredictionAdjustmentAnnotation>(), warnings, AlgorithmVersion);
    }

    private static (double AverageSpeedMetresPerSecond, double AveragePowerWatts) RouteAverages(PredictionRoute route, PredictionResult result)
    {
        if (result.MovingTime <= TimeSpan.Zero) return (0, 0);

        var speed = route.DistanceMetres / result.MovingTime.TotalSeconds;
        var power = result.Segments.Sum(segment => segment.PowerWatts * segment.MovingTime.TotalSeconds) / result.MovingTime.TotalSeconds;
        return (speed, power);
    }
}
