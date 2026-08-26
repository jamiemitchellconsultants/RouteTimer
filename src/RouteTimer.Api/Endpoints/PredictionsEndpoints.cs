using RouteTimer.Api.Errors;
using RouteTimer.Api.Uploads;
using RouteTimer.Contracts.Errors;
using RouteTimer.Contracts.Predictions;
using RouteTimer.Contracts.Uploads;
using RouteTimer.Services.Persistence;
using RouteTimer.Services.Predictions;
using RouteTimer.Services.Routes;

namespace RouteTimer.Api.Endpoints;

public static class PredictionEndpoints
{
    public static IEndpointRouteBuilder MapPredictionEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapPost("/api/predictions", SubmitPredictionAsync);
        routes.MapGet("/api/predictions", GetPredictionsAsync);
        routes.MapGet("/api/predictions/{id:guid}", GetPredictionAsync);
        routes.MapDelete("/api/predictions/{id:guid}", DeletePredictionAsync);
        routes.MapGet("/api/predictions/{id:guid}/gpx", GetPredictionGpxAsync);
        return routes;
    }

    private static async Task<IResult> SubmitPredictionAsync(
        HttpRequest request,
        PredictionSubmissionService submissions,
        CancellationToken cancellationToken)
    {
        IFormFileCollection files;
        try
        {
            files = await MultipartUploadReader.ReadAsync(request, 1, 1, cancellationToken);
        }
        catch (MultipartUploadException exception)
        {
            return ApiProblems.BadRequest(exception.Code, exception.Message);
        }
        catch (MultipartUploadFileCountException)
        {
            return ApiProblems.BadRequest(ErrorCodes.PredictionGpxRequired, "A single .gpx route upload is required.");
        }

        var file = files[0];
        if (!file.FileName.EndsWith(".gpx", StringComparison.OrdinalIgnoreCase))
        {
            return ApiProblems.BadRequest(ErrorCodes.PredictionGpxRequired, "A single .gpx route upload is required.");
        }

        if (file.Length > UploadLimits.MaximumFileBytes)
        {
            return ApiProblems.PayloadTooLarge(ErrorCodes.GpxTooLarge, "The GPX upload exceeds 50 MB.");
        }

        try
        {
            await using var input = file.OpenReadStream();
            var accepted = await submissions.SubmitAsync(new PredictionUpload(file.FileName, input), cancellationToken);
            return TypedResults.Accepted(
                $"/api/predictions/{accepted.PredictionId}",
                new PredictionSubmissionResponse(accepted.PredictionId, accepted.JobId, accepted.ModelId));
        }
        catch (PredictionSubmissionException exception)
        {
            return exception.Code switch
            {
                ErrorCodes.ProfileRequired or ErrorCodes.ModelNotReady => ApiProblems.Conflict(exception.Code, exception.Message),
                ErrorCodes.GpxTooLarge => ApiProblems.PayloadTooLarge(exception.Code, exception.Message),
                _ => ApiProblems.BadRequest(exception.Code, exception.Message)
            };
        }
    }

    private static async Task<IResult> GetPredictionsAsync(
        PredictionQueryService predictions,
        CancellationToken cancellationToken)
    {
        var summaries = await predictions.GetSummariesAsync(cancellationToken);
        return TypedResults.Ok<IReadOnlyList<PredictionSummaryResponse>>(summaries.Select(ToSummary).ToList());
    }

    private static async Task<IResult> GetPredictionAsync(
        Guid id,
        PredictionQueryService predictions,
        CancellationToken cancellationToken) =>
        (await predictions.GetAsync(id, cancellationToken)) is { } prediction
            ? TypedResults.Ok(new PredictionDetailResponse(ToDetailSummary(prediction), prediction.Segments.Select(ToSegment).ToList()))
            : ApiProblems.NotFound(ErrorCodes.PredictionNotFound, "The prediction was not found.");

    private static async Task<IResult> DeletePredictionAsync(
        Guid id,
        PredictionDeletionService deletions,
        CancellationToken cancellationToken) =>
        await deletions.DeleteAsync(id, cancellationToken)
            ? TypedResults.NoContent()
            : ApiProblems.NotFound(ErrorCodes.PredictionNotFound, "The prediction was not found.");

    private static async Task<IResult> GetPredictionGpxAsync(
        Guid id,
        bool? timed,
        PredictionQueryService predictions,
        CancellationToken cancellationToken)
    {
        var source = await predictions.GetGpxSourceAsync(id, cancellationToken);
        if (source is null)
        {
            return ApiProblems.NotFound(ErrorCodes.PredictionNotFound, "The prediction was not found.");
        }

        try
        {
            var gpx = PredictionGpxWriter.Write(source, timed ?? false);
            return TypedResults.File(
                System.Text.Encoding.UTF8.GetBytes(gpx),
                "application/gpx+xml",
                PredictionGpxWriter.SuggestFileName(source.RouteName));
        }
        catch (PredictionNotCompleteException)
        {
            return ApiProblems.Conflict(
                ErrorCodes.PredictionNotComplete,
                "This prediction has not produced a route yet, so it cannot be exported.");
        }
    }

    private static PredictionSummaryResponse ToSummary(PredictionSummary prediction) => new(
        prediction.Id,
        prediction.State.ToString(),
        prediction.DistanceMetres,
        prediction.AscentMetres,
        prediction.MovingTime?.TotalSeconds,
        prediction.AverageSpeedMetresPerSecond,
        prediction.AveragePowerWatts,
        prediction.Confidence?.ToString(),
        prediction.Warnings,
        prediction.ModelId,
        prediction.ModelVersion,
        prediction.ModelWasCalibrated,
        prediction.Validation.Status.ToString(),
        prediction.Validation.MedianAbsolutePercentageError,
        prediction.Validation.P90AbsolutePercentageError,
        prediction.Profile.RiderWeightKg,
        prediction.Profile.BikeAndEquipmentWeightKg,
        prediction.Assumptions.Surface,
        prediction.Assumptions.Wind,
        prediction.Assumptions.Weather,
        prediction.Assumptions.MovingOnly,
        prediction.CreatedAt,
        prediction.CompletedAt);

    private static PredictionSummaryResponse ToDetailSummary(PredictionDetail prediction) => new(
        prediction.Id,
        prediction.State.ToString(),
        prediction.DistanceMetres,
        prediction.AscentMetres,
        prediction.MovingTime?.TotalSeconds,
        prediction.AverageSpeedMetresPerSecond,
        prediction.AveragePowerWatts,
        prediction.Confidence?.ToString(),
        prediction.Warnings,
        prediction.ModelId,
        prediction.ModelVersion,
        prediction.ModelWasCalibrated,
        prediction.Validation.Status.ToString(),
        prediction.Validation.MedianAbsolutePercentageError,
        prediction.Validation.P90AbsolutePercentageError,
        prediction.Profile.RiderWeightKg,
        prediction.Profile.BikeAndEquipmentWeightKg,
        prediction.Assumptions.Surface,
        prediction.Assumptions.Wind,
        prediction.Assumptions.Weather,
        prediction.Assumptions.MovingOnly,
        prediction.CreatedAt,
        prediction.CompletedAt);

    private static PredictionSegmentResponse ToSegment(PersistedPredictionSegment segment) => new(
        segment.Sequence,
        segment.Latitude,
        segment.Longitude,
        segment.ElevationMetres,
        segment.CumulativeDistanceMetres,
        segment.SegmentDistanceMetres,
        segment.Gradient,
        segment.CurvaturePerMetre,
        segment.PredictedPowerWatts,
        segment.PredictedSpeedMetresPerSecond,
        segment.SegmentMovingTime.TotalSeconds,
        segment.CumulativeMovingTime.TotalSeconds,
        segment.Confidence.ToString());
}
