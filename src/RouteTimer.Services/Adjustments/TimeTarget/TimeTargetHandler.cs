using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using RouteTimer.Domain.Adjustments;
using RouteTimer.Domain.Adjustments.TimeTarget;
using RouteTimer.Services.Adjustments;
using RouteTimer.Domain.Models;
using RouteTimer.Domain.Predictions;
using RouteTimer.Services.Models;
using RouteTimer.Services.Predictions;

namespace RouteTimer.Services.Adjustments.TimeTarget;

public sealed class TimeTargetHandler(IRoutePredictor routePredictor) : IPacingStrategyHandler
{
    public PacingStrategyType Type => PacingStrategyType.TimeTarget;
    public const string AlgorithmVersion = "time-target-v1";

    public string Canonicalize(PacingStrategyDefinition strategy) =>
        PacingStrategyJson.Canonicalize((TimeTargetDefinition)strategy);

    public PacingStrategyDefinition Deserialize(string canonicalJson) =>
        PacingStrategyJson.Deserialize<TimeTargetDefinition>(canonicalJson);

    public string CanonicalizeReport(PacingStrategyReport report) =>
        PacingStrategyJson.CanonicalizeReport((TimeTargetReport)report);

    public PacingStrategyComputation Run(
        PacingStrategyContext context,
        PacingStrategyDefinition strategy,
        CancellationToken cancellationToken)
    {
        if (context is null) throw new ArgumentNullException(nameof(context));
        if (strategy is not TimeTargetDefinition timeTargetDef)
            throw new ArgumentException("Definition must be TimeTargetDefinition.", nameof(strategy));

        var baselineResult = context.Baseline;
        double climbSeconds = baselineResult.Segments
            .Where(sr => sr.Gradient >= 0.03)
            .Sum(sr => sr.MovingTime.TotalSeconds);
        double baselineMovingSeconds = baselineResult.MovingTime.TotalSeconds;
        double climbFraction = baselineMovingSeconds > 0 ? climbSeconds / baselineMovingSeconds : 0;

        double bias = timeTargetDef.ClimbBias ?? 1.0;
        bool hasClimbs = climbFraction > 0;
        double normalizer = (climbFraction * bias) + (1.0 - climbFraction);

        var warnings = new List<string>();
        if (timeTargetDef.Distribution == TimeTargetDistribution.ClimbFocused && !hasClimbs)
        {
            warnings.Add(AdjustmentWarningCodes.TimeTargetNoClimbs);
        }

        int evalCount = 0;
        double? fastestBound = null;
        double? slowestBound = null;
        PredictionResult? bestResult = null;
        double bestOuterScale = 1.0;
        double minDiff = double.MaxValue;

        double EvaluateScale(double outerScale)
        {
            cancellationToken.ThrowIfCancellationRequested();
            evalCount++;

            double cScale = timeTargetDef.Distribution == TimeTargetDistribution.ClimbFocused && hasClimbs
                ? outerScale * bias / normalizer
                : outerScale;
            double oScale = timeTargetDef.Distribution == TimeTargetDistribution.ClimbFocused && hasClimbs
                ? outerScale / normalizer
                : outerScale;

            var policy = new TimeTargetPowerPolicy(cScale, oScale);
            PredictionResult result;
            try
            {
                result = routePredictor.Predict(
                    context.Route,
                    context.Profile,
                    context.Model,
                    policy,
                    cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return double.NaN;
            }

            double movingTime = result.MovingTime.TotalSeconds;
            if (double.IsNaN(movingTime) || double.IsInfinity(movingTime) || movingTime <= 0)
            {
                return double.NaN;
            }

            double diff = Math.Abs(movingTime - timeTargetDef.TargetMovingSeconds);
            if (diff < minDiff)
            {
                minDiff = diff;
                bestResult = result;
                bestOuterScale = outerScale;
            }

            if (movingTime < timeTargetDef.TargetMovingSeconds)
            {
                if (fastestBound is null || movingTime > fastestBound.Value) fastestBound = movingTime;
            }
            else
            {
                if (slowestBound is null || movingTime < slowestBound.Value) slowestBound = movingTime;
            }

            return movingTime;
        }

        double foundScale = BoundedPacingSearch.FindMultiplier(
            0.3,
            4.0,
            timeTargetDef.TargetMovingSeconds,
            EvaluateScale,
            30.0);

        if (bestResult is null)
        {
            throw new ArgumentException("Time-target search produced no valid simulation candidate.");
        }

        double achievedSeconds = bestResult.MovingTime.TotalSeconds;
        double absoluteMiss = Math.Abs(achievedSeconds - timeTargetDef.TargetMovingSeconds);
        double percentageMiss = absoluteMiss / timeTargetDef.TargetMovingSeconds * 100.0;
        bool converged = absoluteMiss <= 30.0;
        bool bracketed = fastestBound is not null && slowestBound is not null;

        if (!converged)
        {
            warnings.Add(AdjustmentWarningCodes.TimeTargetInfeasible);
        }

        double selClimbScale = timeTargetDef.Distribution == TimeTargetDistribution.ClimbFocused && hasClimbs
            ? bestOuterScale * bias / normalizer
            : bestOuterScale;
        double selOtherScale = timeTargetDef.Distribution == TimeTargetDistribution.ClimbFocused && hasClimbs
            ? bestOuterScale / normalizer
            : bestOuterScale;

        var gradientBandReports = new List<TimeTargetGradientBandReport>();
        TimeTargetFeasibilityVerdict verdict;

        if (timeTargetDef.IncludeFeasibilityReport)
        {
            var powerLookup = new PowerLookup(context.Model.PowerModel);
            var bandDict = new Dictionary<string, (double movingTime, double baseJoules, double reqJoules)>();

            foreach (var bandKey in PowerModelBands.Gradient.Select(b => b.Key))
            {
                bandDict[bandKey] = (0, 0, 0);
            }

            for (int i = 0; i < bestResult.Segments.Count; i++)
            {
                var segResult = bestResult.Segments[i];
                var baseSegResult = baselineResult.Segments[i];

                string bandKey = PowerModelBands.FindGradientBand(segResult.Gradient).Key;

                double segMovingTime = segResult.MovingTime.TotalSeconds;
                double reqJoules = segResult.PowerWatts * segMovingTime;
                double baseJoules = baseSegResult.PowerWatts * baseSegResult.MovingTime.TotalSeconds;

                var (curTime, curBase, curReq) = bandDict[bandKey];
                bandDict[bandKey] = (curTime + segMovingTime, curBase + baseJoules, curReq + reqJoules);
            }

            double maxRatio = 0;
            bool impossible = !converged && !bracketed;

            foreach (var kvp in bandDict)
            {
                var (mTime, baseJ, reqJ) = kvp.Value;
                if (mTime <= 0) continue;

                double ratio;
                if (baseJ <= 0)
                {
                    ratio = reqJ > 0 ? double.PositiveInfinity : 1.0;
                }
                else
                {
                    ratio = reqJ / baseJ;
                }

                if (double.IsInfinity(ratio) || ratio > maxRatio)
                {
                    maxRatio = ratio;
                }

                gradientBandReports.Add(new TimeTargetGradientBandReport(
                    kvp.Key,
                    mTime,
                    baseJ,
                    reqJ,
                    ratio));
            }

            if (impossible || maxRatio > 2.0) verdict = TimeTargetFeasibilityVerdict.Impossible;
            else if (maxRatio > 1.5) verdict = TimeTargetFeasibilityVerdict.Extreme;
            else if (maxRatio > 1.2) verdict = TimeTargetFeasibilityVerdict.Challenging;
            else verdict = TimeTargetFeasibilityVerdict.Achievable;
        }
        else
        {
            verdict = converged ? TimeTargetFeasibilityVerdict.Achievable : TimeTargetFeasibilityVerdict.Impossible;
        }

        double totalBaseWattSeconds = baselineResult.Segments.Sum(s => s.PowerWatts * s.MovingTime.TotalSeconds);
        double totalAdjWattSeconds = bestResult.Segments.Sum(s => s.PowerWatts * s.MovingTime.TotalSeconds);
        double baseAvgPower = totalBaseWattSeconds / baselineMovingSeconds;
        double adjAvgPower = totalAdjWattSeconds / achievedSeconds;

        double movingTimeDelta = achievedSeconds - baselineMovingSeconds;

        var (baselineSpeed, _) = RouteAverages(context.Route, baselineResult);
        var (adjustedSpeed, _) = RouteAverages(context.Route, bestResult);
        double avgSpeedDelta = adjustedSpeed - baselineSpeed;
        double avgPowerDelta = adjAvgPower - baseAvgPower;

        var report = new TimeTargetReport(
            timeTargetDef.TargetMovingSeconds,
            achievedSeconds,
            absoluteMiss,
            percentageMiss,
            timeTargetDef.Distribution,
            bestOuterScale,
            selClimbScale,
            selOtherScale,
            converged,
            bracketed,
            evalCount,
            fastestBound,
            slowestBound,
            gradientBandReports,
            verdict,
            movingTimeDelta,
            avgSpeedDelta,
            avgPowerDelta);

        var annotations = new Dictionary<int, PredictionAdjustmentAnnotation>();

        return new PacingStrategyComputation(
            bestResult,
            report,
            annotations,
            warnings,
            AlgorithmVersion);
    }

    private static (double AverageSpeedMetresPerSecond, double AveragePowerWatts) RouteAverages(PredictionRoute route, PredictionResult result)
    {
        if (result.MovingTime <= TimeSpan.Zero) return (0, 0);

        var speed = route.DistanceMetres / result.MovingTime.TotalSeconds;
        var power = result.Segments.Sum(segment => segment.PowerWatts * segment.MovingTime.TotalSeconds) / result.MovingTime.TotalSeconds;
        return (speed, power);
    }
}
