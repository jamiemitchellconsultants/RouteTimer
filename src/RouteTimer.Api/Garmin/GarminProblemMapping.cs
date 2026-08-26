using RouteTimer.Api.Errors;
using RouteTimer.Contracts.Errors;
using RouteTimer.Services.Garmin;

namespace RouteTimer.Api.Garmin;

/// <summary>
/// Maps the Garmin-related exceptions shared by every endpoint that talks to a Garmin
/// connection -- activity browsing/import and, now, course push -- to problem responses. Kept in
/// one place so each endpoint module doesn't re-derive the same status codes and error codes.
/// </summary>
public static class GarminProblemMapping
{
    public static bool IsPublicGarminFailure(Exception exception) =>
        exception is GarminAdapterException or
            GarminCredentialsRejectedException or
            GarminMfaInvalidException or
            GarminChallengeExpiredException or
            GarminConnectionRequiredException or
            GarminReconnectRequiredException or
            GarminCursorInvalidException or
            GarminImportLimitException or
            GarminResponseInvalidException;

    public static IResult ToProblem(Exception exception) =>
        exception switch
        {
            GarminCredentialsRejectedException => CredentialsRejected(),
            GarminMfaInvalidException => MfaInvalid(),
            GarminChallengeExpiredException => ChallengeExpired(),
            GarminConnectionRequiredException => ConnectionRequired(),
            GarminReconnectRequiredException => ReconnectRequired(),
            GarminCursorInvalidException => CursorInvalid(),
            GarminImportLimitException => ImportLimit(),
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
                GarminAdapterError.CourseRejected => CourseRejected(),
                GarminAdapterError.ResponseInvalid or
                GarminAdapterError.RequestInvalid or
                GarminAdapterError.ActivityNotAllowed or
                GarminAdapterError.FitTooLarge => ResponseInvalid(),
                _ => ResponseInvalid()
            },
            _ => ResponseInvalid()
        };

    public static IResult CredentialsRejected() =>
        ApiProblems.BadRequest(
            ErrorCodes.GarminCredentialsRejected,
            "Garmin credentials were rejected.");

    public static IResult MfaInvalid() =>
        ApiProblems.BadRequest(
            ErrorCodes.GarminMfaInvalid,
            "The Garmin MFA code was rejected.");

    public static IResult ChallengeExpired() =>
        ApiProblems.Conflict(
            ErrorCodes.GarminChallengeExpired,
            "The Garmin MFA challenge is absent or expired. Start login again.");

    public static IResult ReconnectRequired() =>
        ApiProblems.Conflict(
            ErrorCodes.GarminReconnectRequired,
            "The Garmin connection must be established again.");

    public static IResult ConnectionRequired() =>
        ApiProblems.Conflict(
            ErrorCodes.GarminConnectionRequired,
            "Connect a Garmin account before listing or importing activities.");

    public static IResult CursorInvalid() =>
        ApiProblems.BadRequest(
            ErrorCodes.GarminCursorInvalid,
            "The Garmin activity cursor is invalid.");

    public static IResult ImportLimit() =>
        ApiProblems.BadRequest(
            ErrorCodes.GarminImportLimit,
            "Select between one and ten distinct Garmin activities.");

    public static IResult ResponseInvalid() =>
        ApiProblems.BadGateway(
            ErrorCodes.GarminResponseInvalid,
            "Garmin returned an unusable response.");

    public static IResult CourseRejected() =>
        ApiProblems.Create(
            422,
            ErrorCodes.GarminCourseRejected,
            "Garmin rejected the course.");
}
