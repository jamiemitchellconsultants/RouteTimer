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
        routes.MapPost("/api/training/uploads", UploadTrainingFilesAsync);
        return routes;
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
            return TypedResults.Ok(new TrainingUploadBatchResponse(
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
}
