using RouteTimer.Domain.Jobs;
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
            var publication = BuildPublication(route, result);
            await predictions.PublishAsync(prediction.Id, publication, cancellationToken);
        }
        catch (PredictionJobException exception)
        {
            await predictions.FailAsync(prediction.Id, exception.Code, exception.Message, cancellationToken);
            throw;
        }
        catch (Exception exception) when (exception is RouteInputException or PredictionCalculationException or ArgumentException)
        {
            const string code = "invalid-prediction-result";
            await predictions.FailAsync(prediction.Id, code, "The route could not produce a valid prediction.", cancellationToken);
            throw new PredictionJobException(code, "The route could not produce a valid prediction.", exception);
        }
    }

    private static PredictionPublication BuildPublication(RouteTimer.Domain.Routes.ProcessedRoute route, PredictionResult result)
    {
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
        var averagePower = result.Segments.Count == 0 ? 0 : result.Segments.Average(segment => segment.PowerWatts);
        ValidateNonNegative(averageSpeed, averagePower);
        return new PredictionPublication(route.DistanceMetres, route.AscentMetres, result.MovingTime, averageSpeed, averagePower, result.Confidence, [], persisted);
    }

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
