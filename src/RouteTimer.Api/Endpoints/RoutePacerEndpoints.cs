using System.Globalization;
using Microsoft.Extensions.Options;
using RouteTimer.Api.Errors;
using RouteTimer.Api.RoutePacer;
using RouteTimer.Contracts.Errors;
using RouteTimer.Contracts.Predictions;
using RouteTimer.Services.RoutePacer;
using RouteTimer.Services.Routes;

namespace RouteTimer.Api.Endpoints;

public static class RoutePacerEndpoints
{
    // No AllowAnonymous anywhere in this file, and no payload route: RouteTimer never serves the
    // GPX to the phone. The relay does, from its own public origin.
    public static IEndpointRouteBuilder MapRoutePacerEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/api/routepacer/status", GetStatus);
        routes.MapPost("/api/predictions/{id:guid}/routepacer-handoff", CreateHandoffAsync);
        return routes;
    }

    private static IResult GetStatus(IOptions<RoutePacerHandoffOptions> options)
    {
        var configured = new Uri(options.Value.RoutePacerBaseUrl, UriKind.Absolute);
        return TypedResults.Ok(new RoutePacerStatusResponse(
            options.Value.Enabled,
            configured.GetLeftPart(UriPartial.Authority)));
    }

    private static async Task<IResult> CreateHandoffAsync(
        Guid id,
        RoutePacerHandoffService handoffs,
        CancellationToken cancellationToken)
    {
        try
        {
            var handoff = await handoffs.CreateAsync(id, cancellationToken);
            return TypedResults.Ok(new RoutePacerHandoffResponse(handoff.Url.AbsoluteUri, handoff.ExpiresAt));
        }
        catch (RoutePacerHandoffDisabledException)
        {
            return ApiProblems.ServiceUnavailable(
                ErrorCodes.RoutePacerHandoffDisabled,
                "Sending routes to PaceTracker is not enabled on this RouteTimer.");
        }
        catch (RoutePacerPredictionMissingException)
        {
            return ApiProblems.NotFound(ErrorCodes.PredictionNotFound, "The prediction was not found.");
        }
        catch (PredictionNotCompleteException)
        {
            return ApiProblems.Conflict(
                ErrorCodes.PredictionNotComplete,
                "This prediction has not produced a route yet, so it cannot be sent to PaceTracker.");
        }
        catch (RoutePacerRelayException exception)
        {
            return ToProblem(exception);
        }
    }

    // Every message here is written for the rider and carries nothing from the relay: the
    // exception's own message, any response body, the payload URL, and the credential all stay
    // server-side. Only the rate-limit delay crosses, and only after being re-derived as an integer.
    private static IResult ToProblem(RoutePacerRelayException exception) => exception.Failure switch
    {
        RoutePacerRelayFailure.Authentication => ApiProblems.BadGateway(
            ErrorCodes.RoutePacerRelayAuthenticationFailed,
            "RouteTimer could not authenticate with the PaceTracker relay. Check the server configuration."),
        RoutePacerRelayFailure.PayloadTooLarge => ApiProblems.PayloadTooLarge(
            ErrorCodes.RoutePacerPayloadTooLarge,
            "This route is too large to send to PaceTracker. Download the timed GPX instead."),
        RoutePacerRelayFailure.RejectedPayload => ApiProblems.BadGateway(
            ErrorCodes.RoutePacerRelayRejectedPayload,
            "The PaceTracker relay rejected this route. Download the timed GPX instead."),
        RoutePacerRelayFailure.RateLimited => RateLimited(exception.RetryAfter),
        _ => ApiProblems.BadGateway(
            ErrorCodes.RoutePacerRelayUnavailable,
            "The PaceTracker relay is unavailable. Try again shortly, or download the timed GPX instead.")
    };

    private static IResult RateLimited(TimeSpan? retryAfter)
    {
        var problem = ApiProblems.ServiceUnavailable(
            ErrorCodes.RoutePacerRelayRateLimited,
            "The PaceTracker relay is busy. Try again shortly.");

        // Rebuilt as whole seconds rather than forwarded verbatim: the relay's header is an
        // untrusted string, and an HTTP-date form would leak its clock into our response.
        if (retryAfter is not { } delay || delay <= TimeSpan.Zero)
        {
            return problem;
        }

        var seconds = (int)Math.Ceiling(delay.TotalSeconds);
        return new RetryAfterResult(problem, seconds);
    }

    private sealed class RetryAfterResult(IResult inner, int seconds) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            ArgumentNullException.ThrowIfNull(httpContext);
            httpContext.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
            return inner.ExecuteAsync(httpContext);
        }
    }
}
