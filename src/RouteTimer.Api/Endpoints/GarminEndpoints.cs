using RouteTimer.Api.Errors;
using RouteTimer.Contracts.Errors;
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
        catch (Exception exception) when (IsPublicGarminFailure(exception))
        {
            return ToProblem(exception);
        }
    }

    private static async Task<IResult> ExecuteAsync(Func<Task<GarminConnectionResult>> operation)
    {
        try
        {
            return TypedResults.Ok(ToResponse(await operation()));
        }
        catch (Exception exception) when (IsPublicGarminFailure(exception))
        {
            return ToProblem(exception);
        }
    }

    private static bool IsPublicGarminFailure(Exception exception) =>
        exception is GarminAdapterException or
            GarminCredentialsRejectedException or
            GarminMfaInvalidException or
            GarminChallengeExpiredException or
            GarminConnectionRequiredException or
            GarminReconnectRequiredException or
            GarminCursorInvalidException or
            GarminResponseInvalidException;

    private static IResult ToProblem(Exception exception) =>
        exception switch
        {
            GarminCredentialsRejectedException => CredentialsRejected(),
            GarminMfaInvalidException => MfaInvalid(),
            GarminChallengeExpiredException => ChallengeExpired(),
            GarminConnectionRequiredException => ConnectionRequired(),
            GarminReconnectRequiredException => ReconnectRequired(),
            GarminCursorInvalidException => CursorInvalid(),
            GarminResponseInvalidException => ResponseInvalid(),
            GarminAdapterException adapterException => adapterException.Error switch
            {
                GarminAdapterError.CredentialsRejected => CredentialsRejected(),
                GarminAdapterError.MfaInvalid => MfaInvalid(),
                GarminAdapterError.ChallengeExpired => ChallengeExpired(),
                GarminAdapterError.Authentication => ReconnectRequired(),
                GarminAdapterError.RateLimited => ApiProblems.TooManyRequests(
                    ErrorCodes.GarminRateLimited,
                    "Garmin rate limited the request. Try again later."),
                GarminAdapterError.Unavailable => ApiProblems.ServiceUnavailable(
                    ErrorCodes.GarminUnavailable,
                    "Garmin is temporarily unavailable."),
                GarminAdapterError.AdapterUnavailable => ApiProblems.ServiceUnavailable(
                    ErrorCodes.GarminAdapterUnavailable,
                    "The Garmin connection service is temporarily unavailable."),
                GarminAdapterError.ResponseInvalid or
                GarminAdapterError.RequestInvalid or
                GarminAdapterError.ActivityNotAllowed or
                GarminAdapterError.FitTooLarge => ResponseInvalid(),
                _ => ResponseInvalid()
            },
            _ => ResponseInvalid()
        };

    private static IResult CredentialsRejected() =>
        ApiProblems.BadRequest(
            ErrorCodes.GarminCredentialsRejected,
            "Garmin credentials were rejected.");

    private static IResult MfaInvalid() =>
        ApiProblems.BadRequest(
            ErrorCodes.GarminMfaInvalid,
            "The Garmin MFA code was rejected.");

    private static IResult ChallengeExpired() =>
        ApiProblems.Conflict(
            ErrorCodes.GarminChallengeExpired,
            "The Garmin MFA challenge is absent or expired. Start login again.");

    private static IResult ReconnectRequired() =>
        ApiProblems.Conflict(
            ErrorCodes.GarminReconnectRequired,
            "The Garmin connection must be established again.");

    private static IResult ConnectionRequired() =>
        ApiProblems.Conflict(
            ErrorCodes.GarminConnectionRequired,
            "Connect a Garmin account before listing activities.");

    private static IResult CursorInvalid() =>
        ApiProblems.BadRequest(
            ErrorCodes.GarminCursorInvalid,
            "The Garmin activity cursor is invalid.");

    private static IResult ResponseInvalid() =>
        ApiProblems.BadGateway(
            ErrorCodes.GarminResponseInvalid,
            "Garmin returned an unusable response.");

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
