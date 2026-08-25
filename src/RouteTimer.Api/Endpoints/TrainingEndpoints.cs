using RouteTimer.Api.Errors;
using RouteTimer.Api.Uploads;
using RouteTimer.Contracts.Errors;
using RouteTimer.Contracts.Training;
using RouteTimer.Contracts.Uploads;
using RouteTimer.Services.Training;

namespace RouteTimer.Api.Endpoints;

public static class TrainingEndpoints
{
    public static IEndpointRouteBuilder MapTrainingEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/api/training-activities", GetTrainingActivitiesAsync);
        routes.MapPost("/api/training-activities", UploadTrainingFilesAsync);
        routes.MapGet("/api/training-activities/{id:guid}", GetTrainingActivityAsync);
        routes.MapDelete("/api/training-activities/{id:guid}", DeleteTrainingActivityAsync);
        return routes;
    }

    private static async Task<IResult> GetTrainingActivitiesAsync(
        TrainingActivityQueryService activities,
        CancellationToken cancellationToken)
    {
        var summaries = await activities.GetSummariesAsync(cancellationToken);
        return TypedResults.Ok<IReadOnlyList<TrainingActivitySummaryResponse>>(summaries.Select(ToSummaryResponse).ToList());
    }

    private static async Task<IResult> GetTrainingActivityAsync(
        Guid id,
        TrainingActivityQueryService activities,
        CancellationToken cancellationToken) =>
        (await activities.GetAsync(id, cancellationToken)) is { } activity
            ? TypedResults.Ok(new TrainingActivityDetailResponse(ToSummaryResponse(activity.Summary), activity.ExclusionCounts))
            : ApiProblems.NotFound(ErrorCodes.ActivityNotFound, "The training activity was not found.");

    private static async Task<IResult> DeleteTrainingActivityAsync(
        Guid id,
        TrainingActivityDeletionService activities,
        CancellationToken cancellationToken)
    {
        var deleted = await activities.DeleteAsync(id, cancellationToken);
        return deleted.Deleted
            ? TypedResults.NoContent()
            : ApiProblems.NotFound(ErrorCodes.ActivityNotFound, "The training activity was not found.");
    }

    private static async Task<IResult> UploadTrainingFilesAsync(
        HttpRequest request,
        TrainingUploadService uploads,
        CancellationToken cancellationToken)
    {
        IFormFileCollection files;
        try
        {
            files = await MultipartUploadReader.ReadAsync(
                request,
                minimumFileCount: 1,
                maximumFileCount: UploadLimits.MaximumTrainingFiles,
                cancellationToken);
        }
        catch (MultipartUploadException exception)
        {
            return ApiProblems.BadRequest(exception.Code, exception.Message);
        }
        catch (MultipartUploadFileCountException exception)
        {
            return exception.ActualFileCount > exception.MaximumFileCount
                ? ApiProblems.BadRequest(ErrorCodes.TooManyFiles, $"A maximum of {UploadLimits.MaximumTrainingFiles} training uploads is allowed.")
                : ApiProblems.BadRequest(ErrorCodes.FitUploadRequired, "At least one .fit training upload is required.");
        }

        var batch = OpenBatch(files);
        try
        {
            var results = await uploads.AcceptAsync(batch, cancellationToken);
            return TypedResults.Accepted("/api/training-activities", new TrainingUploadBatchResponse(
                results.Select(result => new TrainingUploadFileResponse(
                    result.FileName,
                    result.Outcome.ToString().ToLowerInvariant(),
                    result.UploadId,
                    result.JobId,
                    result.ErrorCode))
                .ToList()));
        }
        finally
        {
            foreach (var upload in batch)
            {
                await upload.Content.DisposeAsync();
            }
        }
    }

    private static List<TrainingUpload> OpenBatch(IFormFileCollection files)
    {
        var batch = new List<TrainingUpload>(files.Count);
        try
        {
            foreach (var file in files)
            {
                batch.Add(new TrainingUpload(file.FileName, file.OpenReadStream()));
            }

            return batch;
        }
        catch
        {
            foreach (var upload in batch)
            {
                try
                {
                    upload.Content.Dispose();
                }
                catch
                {
                    // Preserve the stream-opening failure while still attempting later clean-up.
                }
            }

            throw;
        }
    }

    private static TrainingActivitySummaryResponse ToSummaryResponse(RouteTimer.Services.Persistence.TrainingActivitySummary summary) => new(
        summary.Id,
        summary.UploadId,
        summary.Metadata.SourceFileName,
        summary.Metadata.StartedAt,
        summary.Metadata.EndedAt,
        summary.Metadata.DeviceManufacturer,
        summary.Metadata.DeviceProduct,
        summary.Metadata.DistanceMetres,
        summary.Metadata.AscentMetres,
        summary.MovingDuration.TotalSeconds,
        summary.Eligibility.ToString(),
        summary.PositionCoverage,
        summary.ElevationCoverage,
        summary.SpeedCoverage,
        summary.PowerCoverage,
        summary.ReasonCodes,
        summary.CreatedAt);
}
