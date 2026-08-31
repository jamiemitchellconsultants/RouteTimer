using RouteTimer.Domain.Adjustments;
using RouteTimer.Domain.Jobs;
using RouteTimer.Domain.Models;
using RouteTimer.Domain.Predictions;
using RouteTimer.Services.Jobs;
using RouteTimer.Services.Persistence;
using RouteTimer.Services.Predictions;

namespace RouteTimer.Services.Adjustments;

/// <summary>
/// Reconstructs the immutable context a strategy needs purely from what the baseline already
/// captured and persisted - the exact rider model snapshot, profile, and ordered segments - never
/// the GPX parser, the current profile, or the latest model. This is what lets an adjustment on an
/// old baseline still reproduce that baseline's exact physics.
/// </summary>
public sealed class PredictionAdjustmentJobHandler(
    IPredictionAdjustmentRepository adjustments,
    IPredictionRepository predictions,
    IRiderModelRepository models,
    PacingStrategyDispatcher dispatcher,
    IJobProgressReporter progress) : IJobHandler
{
    public JobType Handles => JobType.AdjustPrediction;

    public async Task HandleAsync(AnalysisJob job, CancellationToken cancellationToken)
    {
        await progress.ReportAsync(job, 5, JobProgressStages.LoadingBaseline, cancellationToken);
        var adjustment = await adjustments.GetForProcessingAsync(job.SubjectId, cancellationToken)
            ?? throw new PredictionAdjustmentJobException("adjustment-missing", "The adjustment no longer exists.");

        var baseline = await predictions.GetAsync(adjustment.PredictionId, cancellationToken)
            ?? throw new PredictionAdjustmentJobException("baseline-missing", "The baseline prediction no longer exists.");
        if (baseline.State != PredictionState.Succeeded)
        {
            throw new PredictionAdjustmentJobException("baseline-not-ready", "The baseline prediction has not succeeded.");
        }

        RiderModelSnapshot? model;
        try
        {
            model = await models.GetAsync(baseline.ModelId, cancellationToken);
        }
        catch (InvalidPersistedRiderModelException exception)
        {
            throw new PredictionAdjustmentJobException("invalid-rider-model", "The baseline's captured rider model is invalid.", exception);
        }

        if (model is null)
        {
            throw new PredictionAdjustmentJobException("model-missing", "The baseline's captured rider model no longer exists.");
        }

        PredictionRoute route;
        PredictionResult baselineResult;
        try
        {
            (route, baselineResult) = MapBaseline(baseline);
        }
        catch (ArgumentException exception)
        {
            throw new PredictionAdjustmentJobException("invalid-prediction-adjustment-result", "The persisted baseline segments are invalid.", exception);
        }

        await progress.ReportAsync(job, 25, JobProgressStages.PreparingStrategy, cancellationToken);
        var handler = dispatcher.GetHandlerForProcessing(adjustment.StrategyType);
        PacingStrategyDefinition strategy;
        try
        {
            strategy = handler.Deserialize(adjustment.StrategyJson);
        }
        catch (PacingStrategyValidationException exception)
        {
            throw new PredictionAdjustmentJobException("invalid-prediction-adjustment-strategy", "The stored strategy definition is malformed.", exception);
        }

        var context = new PacingStrategyContext(adjustment.PredictionId, route, baselineResult, baseline.Profile, model.Model);
        await progress.ReportAsync(job, 45, JobProgressStages.Simulating, cancellationToken);
        PacingStrategyComputation computation;
        try
        {
            computation = handler.Run(context, strategy, cancellationToken);
        }
        catch (Exception exception) when (exception is PredictionCalculationException or ArgumentException)
        {
            throw new PredictionAdjustmentJobException("invalid-prediction-adjustment-result", "The strategy could not produce a valid result.", exception);
        }

        var publication = BuildPublication(route, computation, handler);
        var workerId = job.WorkerId ?? throw new InvalidOperationException("A claimed adjustment job is required.");
        await progress.ReportAsync(job, 90, JobProgressStages.Publishing, cancellationToken);
        // False means this worker no longer owns the adjustment - its lease expired, the baseline was
        // deleted underneath it, or the child already reached a terminal state - so the result is
        // discarded rather than written over whoever does own it. Mirrors PredictionJobHandler.
        if (!await adjustments.TryPublishAsync(adjustment.Id, job.Id, workerId, publication, cancellationToken))
        {
            return;
        }
    }

    private static (PredictionRoute Route, PredictionResult Baseline) MapBaseline(PredictionDetail baseline)
    {
        if (baseline.MovingTime is not { } movingTime || baseline.Confidence is not { } confidence ||
            baseline.DistanceMetres is not { } distance || baseline.AscentMetres is not { } ascent ||
            baseline.Segments.Count == 0)
        {
            throw new ArgumentException("A succeeded baseline requires its full result and at least one segment.");
        }

        var routeSegments = baseline.Segments.Select(segment => new PredictionRouteSegment(
            segment.Sequence, segment.Latitude, segment.Longitude, segment.ElevationMetres,
            segment.CumulativeDistanceMetres, segment.SegmentDistanceMetres, segment.Gradient, segment.CurvaturePerMetre)).ToArray();
        var route = new PredictionRoute(routeSegments, distance, ascent);

        var predictionSegments = baseline.Segments.Select(segment => new PredictionSegment(
            segment.Sequence, segment.SegmentDistanceMetres, segment.Gradient, segment.PredictedPowerWatts,
            segment.PredictedSpeedMetresPerSecond, segment.SegmentMovingTime, segment.Confidence)).ToList();
        var result = new PredictionResult(predictionSegments, movingTime, confidence, baseline.Warnings);
        return (route, result);
    }

    private static AdjustmentPublication BuildPublication(PredictionRoute route, PacingStrategyComputation computation, IPacingStrategyHandler handler)
    {
        if (computation is null || computation.Adjusted is null || computation.Report is null ||
            computation.Annotations is null || computation.Warnings is null ||
            string.IsNullOrWhiteSpace(computation.AlgorithmVersion) ||
            !Enum.IsDefined(computation.Adjusted.Confidence) ||
            computation.Adjusted.Segments.Any(segment => segment is null || !Enum.IsDefined(segment.Confidence)) ||
            computation.Warnings.Any(warning => !AdjustmentWarningCodes.IsKnown(warning)))
        {
            throw new PredictionAdjustmentJobException("invalid-prediction-adjustment-result", "The strategy result structure is invalid.");
        }

        var routeSequences = route.Segments.Select(segment => segment.Sequence);
        var adjustedSequences = computation.Adjusted.Segments.Select(segment => segment.Sequence);
        if (!routeSequences.SequenceEqual(adjustedSequences))
        {
            throw new PredictionAdjustmentJobException("prediction-adjustment-sequence-mismatch", "The adjusted segments do not match the baseline route.");
        }

        var cumulative = TimeSpan.Zero;
        var persisted = new List<PersistedAdjustmentSegment>(computation.Adjusted.Segments.Count);
        foreach (var segment in computation.Adjusted.Segments)
        {
            cumulative += segment.MovingTime;
            if (!double.IsFinite(segment.PowerWatts) || segment.PowerWatts < 0 ||
                !double.IsFinite(segment.SpeedMetresPerSecond) || segment.SpeedMetresPerSecond < 0 ||
                segment.MovingTime <= TimeSpan.Zero)
            {
                throw new PredictionAdjustmentJobException("invalid-prediction-adjustment-result", "The strategy produced non-finite or negative values.");
            }

            computation.Annotations.TryGetValue(segment.Sequence, out var annotation);
            if (annotation is not null)
            {
                if (annotation.WPrimeBalanceJoules is not null && (!double.IsFinite(annotation.WPrimeBalanceJoules.Value) || annotation.WPrimeBalanceJoules.Value < 0))
                {
                    throw new PredictionAdjustmentJobException("invalid-prediction-adjustment-result", "Annotation W-prime balance must be non-negative finite.");
                }
                if (annotation.ZoneNumber is not null && annotation.ZoneNumber.Value < 1)
                {
                    throw new PredictionAdjustmentJobException("invalid-prediction-adjustment-result", "Annotation zone number must be positive.");
                }
                if (annotation.StrategyPhase is not null && annotation.StrategyPhase is not ("baseline" or "conservation" or "recovery" or "burn"))
                {
                    throw new PredictionAdjustmentJobException("invalid-prediction-adjustment-result", "Annotation strategy phase is invalid.");
                }
            }

            persisted.Add(new PersistedAdjustmentSegment(
                segment.Sequence, segment.PowerWatts, segment.SpeedMetresPerSecond, segment.MovingTime, cumulative, segment.Confidence,
                annotation?.ZoneNumber, annotation?.StrategyPhase, annotation?.WPrimeBalanceJoules));
        }

        if (Math.Abs((cumulative - computation.Adjusted.MovingTime).TotalMilliseconds) > 1)
        {
            throw new PredictionAdjustmentJobException("invalid-prediction-adjustment-result", "The adjusted moving-time total is inconsistent with its segments.");
        }

        var averageSpeed = computation.Adjusted.MovingTime > TimeSpan.Zero ? route.DistanceMetres / computation.Adjusted.MovingTime.TotalSeconds : 0;
        var averagePower = computation.Adjusted.MovingTime > TimeSpan.Zero
            ? computation.Adjusted.Segments.Sum(segment => segment.PowerWatts * segment.MovingTime.TotalSeconds) / computation.Adjusted.MovingTime.TotalSeconds
            : 0;
        if (!double.IsFinite(averageSpeed) || averageSpeed < 0 || !double.IsFinite(averagePower) || averagePower < 0)
        {
            throw new PredictionAdjustmentJobException("invalid-prediction-adjustment-result", "The adjusted route averages are non-finite or negative.");
        }

        string reportJson;
        try
        {
            reportJson = handler.CanonicalizeReport(computation.Report);
        }
        catch (PacingStrategyValidationException exception)
        {
            throw new PredictionAdjustmentJobException("invalid-prediction-adjustment-result", "The strategy report could not be canonicalized.", exception);
        }

        return new AdjustmentPublication(
            computation.Adjusted.MovingTime, averageSpeed, averagePower, computation.Adjusted.Confidence,
            computation.Warnings, reportJson, computation.AlgorithmVersion, persisted);
    }
}
