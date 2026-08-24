using RouteTimer.Domain.Jobs;
using RouteTimer.Domain.Models;
using RouteTimer.Domain.Predictions;
using RouteTimer.Services.Jobs;
using RouteTimer.Services.Persistence;
using RouteTimer.Services.Routes;
using RouteTimer.Services.Validation;

namespace RouteTimer.Services.Predictions;

public sealed class PredictionJobHandler(
    IPredictionRepository predictions,
    IRiderModelRepository models,
    IGpxRouteParser parser,
    IRouteProcessor processor,
    IRoutePredictor predictor) : IJobHandler
{
    public JobType Handles => JobType.PredictRoute;

    public async Task HandleAsync(AnalysisJob job, CancellationToken cancellationToken)
    {
        var prediction = await predictions.GetForProcessingAsync(job.SubjectId, cancellationToken)
            ?? throw new PredictionJobException("prediction-missing", "The prediction no longer exists.");
        var model = await models.GetAsync(prediction.ModelId, cancellationToken)
            ?? throw new PredictionJobException("model-missing", "The prediction's captured model no longer exists.");

        try
        {
            await using var content = new MemoryStream(prediction.Upload.Content, writable: false);
            var parsed = await parser.ParseAsync(content, cancellationToken);
            var route = processor.Process(parsed.Points);
            var result = predictor.Predict(route, prediction.Profile, model.Model);
            PredictionPublication publication;
            try
            {
                publication = BuildPublication(route, result, model);
            }
            catch (OverflowException exception)
            {
                throw new PredictionJobException("invalid-prediction-result", "The prediction contains overflowing time values.", exception);
            }

            await predictions.PublishAsync(prediction.Id, publication, cancellationToken);
        }
        catch (PredictionJobException)
        {
            throw;
        }
        catch (Exception exception) when (exception is RouteInputException or PredictionCalculationException or ArgumentException)
        {
            const string code = "invalid-prediction-result";
            throw new PredictionJobException(code, "The route could not produce a valid prediction.", exception);
        }
    }

    private static PredictionPublication BuildPublication(RouteTimer.Domain.Routes.ProcessedRoute route, PredictionResult result, RiderModelSnapshot model)
    {
        if (result.Segments is null || result.Warnings is null)
        {
            throw new PredictionJobException("invalid-prediction-result", "The prediction result structure is invalid.");
        }

        var routeSegments = route.Samples.Skip(1).ToArray();
        if (routeSegments.Length != result.Segments.Count || !routeSegments.Select(sample => sample.Sequence).SequenceEqual(result.Segments.Select(segment => segment.Sequence)))
        {
            throw new PredictionJobException("prediction-sequence-mismatch", "The prediction segments do not match the processed route.");
        }

        var cumulative = TimeSpan.Zero;
        var persisted = new List<PersistedPredictionSegment>(result.Segments.Count);
        foreach (var pair in routeSegments.Zip(result.Segments))
        {
            var sample = pair.First;
            var segment = pair.Second;
            cumulative += segment.MovingTime;
            ValidateFinite(sample.Point.Latitude, sample.Point.Longitude, sample.Point.ElevationMetres, sample.Gradient);
            ValidateNonNegative(sample.CumulativeDistanceMetres, sample.SegmentDistanceMetres, sample.CurvaturePerMetre,
                segment.PowerWatts, segment.SpeedMetresPerSecond, segment.MovingTime.TotalSeconds, cumulative.TotalSeconds);
            persisted.Add(new PersistedPredictionSegment(segment.Sequence, sample.Point.Latitude, sample.Point.Longitude, sample.Point.ElevationMetres,
                sample.CumulativeDistanceMetres, sample.SegmentDistanceMetres, sample.Gradient, sample.CurvaturePerMetre, segment.PowerWatts,
                segment.SpeedMetresPerSecond, segment.MovingTime, cumulative, segment.Confidence));
        }

        ValidateNonNegative(route.DistanceMetres, route.AscentMetres, result.MovingTime.TotalSeconds);
        if (Math.Abs((cumulative - result.MovingTime).TotalMilliseconds) > 1)
        {
            throw new PredictionJobException("prediction-time-mismatch", "The prediction moving-time total is inconsistent with its segments.");
        }

        var averageSpeed = result.MovingTime > TimeSpan.Zero ? route.DistanceMetres / result.MovingTime.TotalSeconds : 0;
        var averagePower = result.MovingTime > TimeSpan.Zero
            ? result.Segments.Sum(segment => segment.PowerWatts * segment.MovingTime.TotalSeconds) / result.MovingTime.TotalSeconds
            : 0;
        ValidateNonNegative(averageSpeed, averagePower);
        var (confidence, warnings) = ApplyModelWarnings(result.Confidence, result.Warnings, model);
        return new PredictionPublication(route.DistanceMetres, route.AscentMetres, result.MovingTime, averageSpeed, averagePower, confidence, warnings, persisted);
    }

    private static (ConfidenceLevel Confidence, IReadOnlyList<string> Warnings) ApplyModelWarnings(
        ConfidenceLevel confidence,
        IReadOnlyList<string> predictorWarnings,
        RiderModelSnapshot model)
    {
        var warnings = new List<string>();
        var warningSet = new HashSet<string>(StringComparer.Ordinal);
        foreach (var warning in predictorWarnings) AddWarning(warning, warnings, warningSet);
        if (!model.WasCalibrated)
        {
            AddWarning("uncalibrated-coefficients", warnings, warningSet);
            confidence = ConfidenceLevel.Low;
        }

        switch (model.Validation.Status)
        {
            case ModelValidationStatus.Failed:
                AddWarning("model-validation-failed", warnings, warningSet);
                confidence = ConfidenceLevel.Low;
                break;
            case ModelValidationStatus.InsufficientData:
                AddWarning("model-validation-insufficient-data", warnings, warningSet);
                confidence = Min(confidence, ConfidenceLevel.Medium);
                break;
            case ModelValidationStatus.NotValidated:
                AddWarning("model-validation-not-validated", warnings, warningSet);
                confidence = Min(confidence, ConfidenceLevel.Medium);
                break;
        }

        return (confidence, warnings);
    }

    private static void AddWarning(string warning, ICollection<string> warnings, ISet<string> warningSet)
    {
        if (warningSet.Add(warning)) warnings.Add(warning);
    }

    private static ConfidenceLevel Min(ConfidenceLevel left, ConfidenceLevel right) => (ConfidenceLevel)Math.Min((int)left, (int)right);

    private static void ValidateFinite(params double[] values)
    {
        if (values.Any(value => !double.IsFinite(value)))
        {
            throw new PredictionJobException("invalid-prediction-result", "The prediction contains non-finite values.");
        }
    }

    private static void ValidateNonNegative(params double[] values)
    {
        if (values.Any(value => !double.IsFinite(value) || value < 0))
        {
            throw new PredictionJobException("invalid-prediction-result", "The prediction contains non-finite or negative values.");
        }
    }
}
