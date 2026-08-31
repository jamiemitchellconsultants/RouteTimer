using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using RouteTimer.Domain.Adjustments;
using RouteTimer.Domain.Adjustments.NpIf;
using RouteTimer.Domain.Models;
using RouteTimer.Domain.Predictions;
using RouteTimer.Services.Adjustments;
using RouteTimer.Services.Models;
using RouteTimer.Services.Predictions;

namespace RouteTimer.Services.Adjustments.NpIf;

public sealed class NpIfTargetHandler(IRoutePredictor routePredictor) : IPacingStrategyHandler
{
    public PacingStrategyType Type => PacingStrategyType.NpIfTarget;
    public const string AlgorithmVersion = "np-if-target-v1";

    public string Canonicalize(PacingStrategyDefinition strategy) =>
        PacingStrategyJson.Canonicalize((NpIfTargetDefinition)strategy);

    public PacingStrategyDefinition Deserialize(string canonicalJson) =>
        PacingStrategyJson.Deserialize<NpIfTargetDefinition>(canonicalJson);

    public string CanonicalizeReport(PacingStrategyReport report) =>
        PacingStrategyJson.CanonicalizeReport((NpIfTargetReport)report);

    public PacingStrategyComputation Run(
        PacingStrategyContext context,
        PacingStrategyDefinition strategy,
        CancellationToken cancellationToken)
    {
        if (context is null) throw new ArgumentNullException(nameof(context));
        if (strategy is not NpIfTargetDefinition npIfDef)
            throw new ArgumentException("Definition must be NpIfTargetDefinition.", nameof(strategy));

        double targetNp = npIfDef.FtpWatts * npIfDef.TargetIntensityFactor;
        int evalCount = 0;
        double? fastestBound = null;
        double? slowestBound = null;
        PredictionResult? bestResult = null;
        double bestParameter = npIfDef.Mode == NpIfScalingMode.Proportional ? 1.0 : 0.0;
        double minDiff = double.MaxValue;
        double bestAchievedNp = 0;

        double EvaluateParameter(double param)
        {
            cancellationToken.ThrowIfCancellationRequested();
            evalCount++;

            var policy = new NpIfPowerPolicy(npIfDef.Mode, param);
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

            if (result.MovingTime <= TimeSpan.Zero || result.Segments.Count == 0)
            {
                return double.NaN;
            }

            var powers = result.Segments.Select(s => s.PowerWatts).ToList();
            var durations = result.Segments.Select(s => s.MovingTime.TotalSeconds).ToList();
            double np = NormalizedPowerCalculator.CalculateNormalizedPower(powers, durations);

            double diff = Math.Abs(np - targetNp);
            if (diff < minDiff)
            {
                minDiff = diff;
                bestResult = result;
                bestParameter = param;
                bestAchievedNp = np;
            }

            if (np > targetNp)
            {
                if (fastestBound is null || np < fastestBound.Value) fastestBound = np;
            }
            else
            {
                if (slowestBound is null || np > slowestBound.Value) slowestBound = np;
            }

            return np;
        }

        double minParam = npIfDef.Mode == NpIfScalingMode.Proportional ? 0.1 : -2000.0;
        double maxParam = npIfDef.Mode == NpIfScalingMode.Proportional ? 5.0 : 2000.0;

        // The search's return value is the best parameter it evaluated; this handler needs the
        // PredictionResult that went with it, which the closure above already keeps, so the parameter
        // itself is redundant here.
        double toleranceWatts = ConvergenceToleranceWatts(targetNp);
        _ = BoundedPacingSearch.FindMultiplier(
            minParam,
            maxParam,
            targetNp,
            EvaluateParameter,
            toleranceWatts);

        if (bestResult is null)
        {
            throw new ArgumentException("NP/IF search produced no valid simulation candidate.");
        }

        double achievedSeconds = bestResult.MovingTime.TotalSeconds;
        double absoluteMiss = Math.Abs(bestAchievedNp - targetNp);
        bool converged = absoluteMiss <= toleranceWatts;
        bool bracketed = fastestBound is not null && slowestBound is not null;

        var warnings = new List<string>();
        bool usedShortRoute = achievedSeconds < 600;
        if (usedShortRoute)
        {
            warnings.Add(AdjustmentWarningCodes.NpIfShortRouteApproximation);
        }

        if (npIfDef.Mode == NpIfScalingMode.Proportional)
        {
            if (bestParameter < 0.5) warnings.Add(AdjustmentWarningCodes.NpIfLowIntensity);
            else if (bestParameter > 2.0) warnings.Add(AdjustmentWarningCodes.NpIfHighIntensity);
        }

        if (!converged)
        {
            warnings.Add(AdjustmentWarningCodes.NpIfClosestFeasible);
        }

        double achievedIf = bestAchievedNp / npIfDef.FtpWatts;
        double baselineMovingSeconds = context.Baseline.MovingTime.TotalSeconds;
        double movingTimeDelta = achievedSeconds - baselineMovingSeconds;

        var (baselineSpeed, baselinePower) = RouteAverages(context.Route, context.Baseline);
        var (adjustedSpeed, adjustedPower) = RouteAverages(context.Route, bestResult);
        double avgSpeedDelta = adjustedSpeed - baselineSpeed;
        double avgPowerDelta = adjustedPower - baselinePower;

        var report = new NpIfTargetReport(
            targetNp,
            bestAchievedNp,
            npIfDef.TargetIntensityFactor,
            achievedIf,
            npIfDef.FtpWatts,
            npIfDef.Mode,
            bestParameter,
            converged,
            bracketed,
            evalCount,
            usedShortRoute,
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

    /// <summary>
    /// A quarter of a percent of the target, never tighter than half a watt. A flat absolute figure
    /// would demand 0.017% precision of a 3000 W target and spend the whole evaluation budget failing
    /// to reach it. Mirrors <c>TimeTargetHandler.ConvergenceToleranceSeconds</c>.
    /// </summary>
    internal static double ConvergenceToleranceWatts(double targetNormalizedPowerWatts) =>
        Math.Max(0.5, targetNormalizedPowerWatts * 0.0025);

    private static (double AverageSpeedMetresPerSecond, double AveragePowerWatts) RouteAverages(PredictionRoute route, PredictionResult result)
    {
        if (result.MovingTime <= TimeSpan.Zero) return (0, 0);

        var speed = route.DistanceMetres / result.MovingTime.TotalSeconds;
        var power = result.Segments.Sum(segment => segment.PowerWatts * segment.MovingTime.TotalSeconds) / result.MovingTime.TotalSeconds;
        return (speed, power);
    }
}
