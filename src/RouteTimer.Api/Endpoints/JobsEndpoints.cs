using RouteTimer.Api.Errors;
using RouteTimer.Contracts.Errors;
using RouteTimer.Contracts.Jobs;
using RouteTimer.Services.Jobs;

namespace RouteTimer.Api.Endpoints;

public static class JobEndpoints
{
    public static IEndpointRouteBuilder MapJobEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/api/jobs/{id:guid}", GetJobAsync);
        return routes;
    }

    private static async Task<IResult> GetJobAsync(Guid id, IJobRepository jobs, CancellationToken cancellationToken) =>
        (await jobs.GetAsync(id, cancellationToken)) is { } job
            ? TypedResults.Ok(new JobResponse(
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
                job.State == RouteTimer.Domain.Jobs.JobState.Running ? job.LeaseExpiresAt : null,
                job.DiagnosticCode,
                job.DiagnosticMessage))
            : ApiProblems.NotFound(ErrorCodes.JobNotFound, "The job was not found.");
}
