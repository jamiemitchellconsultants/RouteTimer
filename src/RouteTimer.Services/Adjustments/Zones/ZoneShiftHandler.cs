using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using RouteTimer.Domain.Adjustments;
using RouteTimer.Domain.Adjustments.Zones;
using RouteTimer.Domain.Models;
using RouteTimer.Domain.Predictions;
using RouteTimer.Services.Adjustments;
using RouteTimer.Services.Predictions;

namespace RouteTimer.Services.Adjustments.Zones;

public sealed class ZoneShiftHandler(IRoutePredictor routePredictor) : IPacingStrategyHandler
{
    public const string AlgorithmVersion = "zone-shift-v1";
    public PacingStrategyType Type => PacingStrategyType.RpeZoneShift;

    public string Canonicalize(PacingStrategyDefinition strategy) =>
        PacingStrategyJson.Canonicalize((ZoneShiftDefinition)strategy);

    public PacingStrategyDefinition Deserialize(string canonicalJson) =>
        PacingStrategyJson.Deserialize<ZoneShiftDefinition>(canonicalJson);

    public string CanonicalizeReport(PacingStrategyReport report) =>
        PacingStrategyJson.CanonicalizeReport((ZoneShiftReport)report);

    public PacingStrategyComputation Run(
        PacingStrategyContext context,
        PacingStrategyDefinition strategy,
        CancellationToken cancellationToken)
    {
        if (context is null) throw new ArgumentNullException(nameof(context));
        if (strategy is not ZoneShiftDefinition definition)
            throw new ArgumentException("Definition must be ZoneShiftDefinition.", nameof(strategy));

        var resolvedZoneSet = PowerZoneResolver.Resolve(definition.ThresholdMode, definition.FtpWatts, context.Model);
        var policy = new ZoneShiftPolicy(definition, resolvedZoneSet);

        var adjusted = routePredictor.Predict(context.Route, context.Profile, context.Model, policy, cancellationToken);

        var warnings = new List<string>();
        if (definition.ThresholdMode == ZoneThresholdMode.ModelInferred)
        {
            warnings.Add(AdjustmentWarningCodes.RpeZoneThresholdInferred);
        }

        if (context.Model.PowerModel.Bands.Count == 0 || context.Model.PowerModel.Bands.All(b => b.Confidence == ConfidenceLevel.Low))
        {
            warnings.Add(AdjustmentWarningCodes.RpeZoneModelLowConfidence);
        }

        if (policy.UsedCappedZoneSevenTarget)
        {
            warnings.Add(AdjustmentWarningCodes.RpeZoneZ7Capped);
        }

        double totalMovingSeconds = adjusted.MovingTime.TotalSeconds;
        var zoneSeconds = new double[resolvedZoneSet.Zones.Count];
        var annotations = new Dictionary<int, PredictionAdjustmentAnnotation>();

        for (int i = 0; i < adjusted.Segments.Count; i++)
        {
            var seg = adjusted.Segments[i];
            int assignedZone;
            if (policy.AssignedZonesBySequence.TryGetValue(seg.Sequence, out var z))
            {
                assignedZone = z;
            }
            else
            {
                assignedZone = ClassifyZone(seg.PowerWatts, resolvedZoneSet.Zones);
            }

            if (assignedZone >= 1 && assignedZone <= zoneSeconds.Length)
            {
                zoneSeconds[assignedZone - 1] += seg.MovingTime.TotalSeconds;
            }

            annotations[seg.Sequence] = new PredictionAdjustmentAnnotation(
                assignedZone,
                null,
                null);
        }

        var distribution = new List<ZoneDistributionEntry>();
        for (int z = 1; z <= resolvedZoneSet.Zones.Count; z++)
        {
            double secs = zoneSeconds[z - 1];
            double pct = totalMovingSeconds > 0 ? (secs / totalMovingSeconds) * 100.0 : 0.0;
            distribution.Add(new ZoneDistributionEntry(z, secs, pct));
        }

        var powers = adjusted.Segments.Select(s => s.PowerWatts).ToList();
        var durations = adjusted.Segments.Select(s => s.MovingTime.TotalSeconds).ToList();
        double npWatts = NormalizedPowerCalculator.CalculateNormalizedPower(powers, durations);

        double baseMovingSeconds = context.Baseline.MovingTime.TotalSeconds;
        double movingTimeDelta = totalMovingSeconds - baseMovingSeconds;

        var (baselineSpeed, baselinePower) = RouteAverages(context.Route, context.Baseline);
        var (adjustedSpeed, adjustedPower) = RouteAverages(context.Route, adjusted);

        double avgSpeedDelta = adjustedSpeed - baselineSpeed;
        double avgPowerDelta = adjustedPower - baselinePower;

        var report = new ZoneShiftReport(
            resolvedZoneSet.ThresholdWatts,
            resolvedZoneSet.Provenance,
            resolvedZoneSet.Zones,
            policy.MatchCounts,
            distribution,
            adjustedPower,
            npWatts,
            movingTimeDelta,
            avgSpeedDelta,
            avgPowerDelta);

        return new PacingStrategyComputation(
            adjusted,
            report,
            annotations,
            warnings,
            AlgorithmVersion);
    }

    private static int ClassifyZone(double watts, IReadOnlyList<ResolvedPowerZone> zones)
    {
        for (int i = 0; i < zones.Count; i++)
        {
            var z = zones[i];
            if (i == zones.Count - 1)
            {
                return z.Zone;
            }

            if (watts < z.UpperWatts)
            {
                return z.Zone;
            }
        }

        return zones[^1].Zone;
    }

    private static (double AverageSpeedMetresPerSecond, double AveragePowerWatts) RouteAverages(PredictionRoute route, PredictionResult result)
    {
        if (result.MovingTime <= TimeSpan.Zero) return (0, 0);

        var speed = route.DistanceMetres / result.MovingTime.TotalSeconds;
        var power = result.Segments.Sum(segment => segment.PowerWatts * segment.MovingTime.TotalSeconds) / result.MovingTime.TotalSeconds;
        return (speed, power);
    }
}
