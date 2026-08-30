using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using RouteTimer.Domain.Adjustments;
using RouteTimer.Domain.Adjustments.MatchBurning;
using RouteTimer.Domain.Adjustments.Zones;
using RouteTimer.Domain.Models;
using RouteTimer.Domain.Predictions;
using RouteTimer.Services.Adjustments;
using RouteTimer.Services.Adjustments.Zones;
using RouteTimer.Services.Predictions;

namespace RouteTimer.Services.Adjustments.MatchBurning;

public sealed class MatchBurningHandler(IRoutePredictor routePredictor) : IPacingStrategyHandler
{
    public const string AlgorithmVersion = "match-burning-v1";
    public PacingStrategyType Type => PacingStrategyType.VariableMatchBurning;

    public string Canonicalize(PacingStrategyDefinition strategy) =>
        PacingStrategyJson.Canonicalize((MatchBurningDefinition)strategy);

    public PacingStrategyDefinition Deserialize(string canonicalJson) =>
        PacingStrategyJson.Deserialize<MatchBurningDefinition>(canonicalJson);

    public string CanonicalizeReport(PacingStrategyReport report) =>
        PacingStrategyJson.CanonicalizeReport((MatchBurningReport)report);

    public PacingStrategyComputation Run(
        PacingStrategyContext context,
        PacingStrategyDefinition strategy,
        CancellationToken cancellationToken)
    {
        if (context is null) throw new ArgumentNullException(nameof(context));
        if (strategy is not MatchBurningDefinition definition)
            throw new ArgumentException("Definition must be MatchBurningDefinition.", nameof(strategy));

        var capacity = CapacityResolver.Resolve(definition, context.Model.PowerModel);
        var cpZones = PowerZoneResolver.Resolve(ZoneThresholdMode.FtpBased, capacity.CriticalPowerWatts, context.Model);

        var plan1 = MatchPhasePlanner.Plan(context.Route, context.Baseline, definition);
        var policy1 = new MatchBurningPolicy(definition, capacity, plan1, cpZones);

        var firstAdjusted = routePredictor.Predict(context.Route, context.Profile, context.Model, policy1, cancellationToken);

        bool refinementRan = false;
        bool refinementChanged = false;
        PredictionResult finalAdjusted = firstAdjusted;
        MatchPhasePlan finalPlan = plan1;

        if (definition.EnableRefinement)
        {
            refinementRan = true;
            var plan2 = MatchPhasePlanner.Plan(context.Route, firstAdjusted, definition);
            if (HasPlanChanged(plan1, plan2))
            {
                refinementChanged = true;
                finalPlan = plan2;
                var policy2 = new MatchBurningPolicy(definition, capacity, plan2, cpZones);
                finalAdjusted = routePredictor.Predict(context.Route, context.Profile, context.Model, policy2, cancellationToken);
            }
        }

        var wPrimeResult = WPrimeBalanceCalculator.Calculate(
            finalAdjusted.Segments,
            capacity.CriticalPowerWatts,
            capacity.WPrimeJoules);

        var warnings = new List<string>(capacity.Warnings);

        if (finalPlan.HasOverlappingBurnWindows && !warnings.Contains(AdjustmentWarningCodes.MatchBurningOverlappingWindows))
        {
            warnings.Add(AdjustmentWarningCodes.MatchBurningOverlappingWindows);
        }

        if (finalPlan.WindowMatchCounts.Any(c => c == 0) && !warnings.Contains(AdjustmentWarningCodes.MatchBurningWindowNoMatch))
        {
            warnings.Add(AdjustmentWarningCodes.MatchBurningWindowNoMatch);
        }

        if (wPrimeResult.Points.Any(p => p.DisplayBalanceJoules < 0.20 * capacity.WPrimeJoules) && !warnings.Contains(AdjustmentWarningCodes.MatchBurningReserveBreach))
        {
            warnings.Add(AdjustmentWarningCodes.MatchBurningReserveBreach);
        }

        var windowReports = finalPlan.WindowMatchCounts
            .Select((count, index) => new MatchBurnWindowReport(index, count))
            .ToList();

        var phaseCounts = new Dictionary<MatchPhase, (int segCount, double seconds)>();
        foreach (var p in Enum.GetValues<MatchPhase>())
        {
            phaseCounts[p] = (0, 0.0);
        }

        for (int i = 0; i < finalAdjusted.Segments.Count; i++)
        {
            var seg = finalAdjusted.Segments[i];
            if (finalPlan.BySequence.TryGetValue(seg.Sequence, out var assignment))
            {
                var (curCount, curSecs) = phaseCounts[assignment.Phase];
                phaseCounts[assignment.Phase] = (curCount + 1, curSecs + seg.MovingTime.TotalSeconds);
            }
        }

        var phaseReports = phaseCounts
            .Select(kvp => new MatchPhaseReport(kvp.Key, kvp.Value.segCount, kvp.Value.seconds))
            .ToList();

        double totalMovingSeconds = finalAdjusted.MovingTime.TotalSeconds;
        double depletedFraction = (capacity.WPrimeJoules - wPrimeResult.MinimumBalanceJoules) / capacity.WPrimeJoules;

        var criticalSequences = definition.IncludeFatigueReport
            ? wPrimeResult.Points.Where(p => p.DisplayBalanceJoules == 0).Select(p => p.Sequence).ToList()
            : [];

        double baseMovingSeconds = context.Baseline.MovingTime.TotalSeconds;
        double movingTimeDelta = totalMovingSeconds - baseMovingSeconds;

        var (baselineSpeed, baselinePower) = RouteAverages(context.Route, context.Baseline);
        var (adjustedSpeed, adjustedPower) = RouteAverages(context.Route, finalAdjusted);

        double avgSpeedDelta = adjustedSpeed - baselineSpeed;
        double avgPowerDelta = adjustedPower - baselinePower;

        var report = new MatchBurningReport(
            capacity.CriticalPowerWatts,
            capacity.CriticalPowerProvenance,
            capacity.WPrimeJoules,
            capacity.WPrimeProvenance,
            windowReports,
            phaseReports,
            wPrimeResult.MinimumBalanceJoules,
            wPrimeResult.FinalBalanceJoules,
            depletedFraction,
            wPrimeResult.TimeAboveCriticalPowerSeconds,
            wPrimeResult.WorkAboveCriticalPowerJoules,
            criticalSequences,
            wPrimeResult.FirstInfeasibleSequence,
            wPrimeResult.Verdict,
            definition.EnableRefinement,
            refinementRan,
            refinementChanged,
            movingTimeDelta,
            avgSpeedDelta,
            avgPowerDelta);

        var annotations = new Dictionary<int, PredictionAdjustmentAnnotation>();
        for (int i = 0; i < finalAdjusted.Segments.Count; i++)
        {
            var seg = finalAdjusted.Segments[i];
            string phaseStr = finalPlan.BySequence.TryGetValue(seg.Sequence, out var assignment)
                ? assignment.Phase.ToString().ToLowerInvariant()
                : "baseline";

            annotations[seg.Sequence] = new PredictionAdjustmentAnnotation(
                null,
                phaseStr,
                wPrimeResult.Points[i].DisplayBalanceJoules);
        }

        return new PacingStrategyComputation(
            finalAdjusted,
            report,
            annotations,
            warnings,
            AlgorithmVersion);
    }

    private static bool HasPlanChanged(MatchPhasePlan plan1, MatchPhasePlan plan2)
    {
        if (plan1.BySequence.Count != plan2.BySequence.Count) return true;

        foreach (var kvp in plan1.BySequence)
        {
            if (!plan2.BySequence.TryGetValue(kvp.Key, out var assign2)) return true;
            if (kvp.Value.Phase != assign2.Phase || kvp.Value.BurnWindowIndex != assign2.BurnWindowIndex) return true;
        }

        return false;
    }

    private static (double AverageSpeedMetresPerSecond, double AveragePowerWatts) RouteAverages(PredictionRoute route, PredictionResult result)
    {
        if (result.MovingTime <= TimeSpan.Zero) return (0, 0);

        var speed = route.DistanceMetres / result.MovingTime.TotalSeconds;
        var power = result.Segments.Sum(segment => segment.PowerWatts * segment.MovingTime.TotalSeconds) / result.MovingTime.TotalSeconds;
        return (speed, power);
    }
}
