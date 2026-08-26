using RouteTimer.Api.Garmin;
using RouteTimer.Contracts.Garmin;
using RouteTimer.Services.Garmin;

namespace RouteTimer.Api.Endpoints;

public static class GarminEndpoints
{
    public static IEndpointRouteBuilder MapGarminEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/api/garmin/connection", GetConnectionAsync);
        routes.MapPost("/api/garmin/connection/login", LoginAsync);
        routes.MapPost("/api/garmin/connection/mfa", CompleteMfaAsync);
        routes.MapDelete("/api/garmin/connection", DisconnectAsync);
        routes.MapGet("/api/garmin/activities", GetActivitiesAsync);
        routes.MapPost("/api/garmin/activities/import", ImportActivitiesAsync);
        return routes;
    }

    private static Task<IResult> GetConnectionAsync(
        GarminConnectionService connections,
        CancellationToken cancellationToken) =>
        ExecuteAsync(() => connections.ValidateAsync(cancellationToken));

    private static Task<IResult> LoginAsync(
        GarminLoginRequest request,
        GarminConnectionService connections,
        CancellationToken cancellationToken) =>
        ExecuteAsync(() => connections.LoginAsync(request.Email, request.Password, cancellationToken));

    private static Task<IResult> CompleteMfaAsync(
        GarminMfaRequest request,
        GarminConnectionService connections,
        CancellationToken cancellationToken) =>
        ExecuteAsync(() => connections.CompleteMfaAsync(request.ChallengeId, request.Code, cancellationToken));

    private static async Task<IResult> DisconnectAsync(
        GarminConnectionService connections,
        CancellationToken cancellationToken)
    {
        await connections.DisconnectAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<IResult> GetActivitiesAsync(
        string? cursor,
        GarminActivityService activities,
        CancellationToken cancellationToken)
    {
        try
        {
            return TypedResults.Ok(ToResponse(await activities.GetActivitiesAsync(cursor, cancellationToken)));
        }
        catch (Exception exception) when (GarminProblemMapping.IsPublicGarminFailure(exception))
        {
            return GarminProblemMapping.ToProblem(exception);
        }
    }

    private static async Task<IResult> ImportActivitiesAsync(
        GarminImportRequest request,
        GarminActivityService activities,
        CancellationToken cancellationToken)
    {
        try
        {
            var results = await activities.ImportAsync(request.ActivityIds, cancellationToken);
            return TypedResults.Accepted(
                "/api/garmin/activities/import",
                new GarminImportBatchResponse(results.Select(static result => new GarminImportResultResponse(
                    result.ActivityId,
                    result.Name,
                    result.Outcome,
                    result.UploadId,
                    result.JobId,
                    result.ErrorCode)).ToArray()));
        }
        catch (Exception exception) when (GarminProblemMapping.IsPublicGarminFailure(exception))
        {
            return GarminProblemMapping.ToProblem(exception);
        }
    }

    private static async Task<IResult> ExecuteAsync(Func<Task<GarminConnectionResult>> operation)
    {
        try
        {
            return TypedResults.Ok(ToResponse(await operation()));
        }
        catch (Exception exception) when (GarminProblemMapping.IsPublicGarminFailure(exception))
        {
            return GarminProblemMapping.ToProblem(exception);
        }
    }

    private static GarminConnectionResponse ToResponse(GarminConnectionResult result) =>
        new(result.State, result.GarminUserId, result.DisplayName, result.ChallengeId);

    private static GarminActivityPageResponse ToResponse(GarminActivityPage page) =>
        new(
            page.Activities.Select(static activity => new GarminActivitySummaryResponse(
                activity.ActivityId,
                activity.Name,
                activity.StartedAt,
                activity.ActivityType,
                activity.DistanceMetres,
                activity.DurationSeconds,
                activity.AscentMetres,
                activity.AveragePowerWatts,
                activity.AlreadyImported)).ToArray(),
            page.NextCursor);
}
