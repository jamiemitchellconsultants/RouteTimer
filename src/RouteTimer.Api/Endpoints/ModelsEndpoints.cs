using RouteTimer.Api.Errors;
using RouteTimer.Contracts.Errors;
using RouteTimer.Contracts.Jobs;
using RouteTimer.Contracts.Models;
using RouteTimer.Domain.Jobs;
using RouteTimer.Services.Models;

namespace RouteTimer.Api.Endpoints;

public static class ModelsEndpoints
{
    public static IEndpointRouteBuilder MapModelsEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/api/models/current", GetCurrentModelAsync);
        routes.MapPost("/api/models/rebuild", RebuildModelAsync);
        return routes;
    }

    private static async Task<IResult> GetCurrentModelAsync(
        ModelStatusService models,
        CancellationToken cancellationToken)
    {
        var status = await models.GetAsync(cancellationToken);
        return TypedResults.Ok(ToResponse(status));
    }

    private static async Task<IResult> RebuildModelAsync(
        ModelRebuildService models,
        CancellationToken cancellationToken)
    {
        try
        {
            var jobId = await models.RequestAsync(cancellationToken);
            return TypedResults.Accepted("/api/models/current", new ModelRebuildResponse(jobId));
        }
        catch (ModelRebuildRequestException exception)
        {
            return exception.Code switch
            {
                ErrorCodes.ProfileRequired or ErrorCodes.NoEligibleActivities => ApiProblems.Conflict(exception.Code, exception.Message),
                _ => ApiProblems.BadRequest(exception.Code, exception.Message)
            };
        }
    }

    private static ModelStatusResponse ToResponse(ModelStatusResult status)
    {
        var current = status.CurrentModel;
        var powerBands = current?.Model.PowerModel.Bands
            .Select(band => new PowerBandCoverageResponse(
                band.GradeKey,
                band.DurationKey,
                band.TypicalWatts,
                band.Evidence.TotalSeconds,
                band.ActivityCount,
                band.ShrinkageWeight,
                band.Confidence.ToString()))
            .ToList() ?? [];
        var descentCells = current?.Model.DescentLimits.Cells ?? [];

        return new ModelStatusResponse(
            status.IsReady,
            status.BlockingReason,
            current?.Id,
            current?.Model.AlgorithmVersion,
            current?.CreatedAt,
            current?.WasCalibrated,
            current?.DescentWasLearned,
            current?.Validation.Status.ToString(),
            current?.Validation.MedianAbsolutePercentageError,
            current?.Validation.P90AbsolutePercentageError,
            current is null
                ? null
                : new PhysicalCoefficientsResponse(
                    current.Model.Coefficients.DrivetrainEfficiency,
                    current.Model.Coefficients.AirDensity,
                    current.Model.Coefficients.Crr,
                    current.Model.Coefficients.CdA),
            powerBands,
            descentCells.Count(cell => !cell.IsFallback),
            descentCells.Count(cell => cell.IsFallback),
            status.RebuildJob is null ? null : ToJobResponse(status.RebuildJob));
    }

    private static JobResponse ToJobResponse(AnalysisJob job) => new(
        job.Id,
        job.Type.ToString(),
        job.SubjectId,
        job.State.ToString(),
        job.ProgressPercent,
        job.ProgressStage,
        job.AttemptCount,
        job.CreatedAt,
        job.StartedAt,
        job.UpdatedAt,
        job.CompletedAt,
        job.State == JobState.Running ? job.LeaseExpiresAt : null,
        job.DiagnosticCode,
        job.DiagnosticMessage);
}
