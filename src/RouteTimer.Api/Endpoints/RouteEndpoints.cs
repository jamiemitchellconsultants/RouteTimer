using RouteTimer.Api.Errors;
using RouteTimer.Contracts.Errors;
using RouteTimer.Contracts.Routes;
using RouteTimer.Services.Routes;

namespace RouteTimer.Api.Endpoints;

public static class RouteEndpoints
{
    public static IEndpointRouteBuilder MapRouteEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/api/routes/short-links/{code}", ResolveShortLinkAsync);
        return routes;
    }

    private static async Task<IResult> ResolveShortLinkAsync(
        string code,
        ShortLinkResolutionService shortLinks,
        CancellationToken cancellationToken)
    {
        try
        {
            var resolved = await shortLinks.ResolveAsync(code, cancellationToken);
            return TypedResults.Ok(new ShortLinkResponse(resolved));
        }
        catch (ShortLinkCodeInvalidException)
        {
            return ApiProblems.BadRequest(
                ErrorCodes.ShortLinkCodeInvalid,
                "The short-link code is not in the permitted form.");
        }
        catch (ShortLinkUnresolvedException)
        {
            return ApiProblems.BadGateway(
                ErrorCodes.ShortLinkUnresolved,
                "The short link did not resolve. Open it in a browser tab and paste the expanded Google Maps URL instead.");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ApiProblems.Create(
                StatusCodes.Status504GatewayTimeout,
                ErrorCodes.ShortLinkUnresolved,
                "The short-link service did not respond in time.");
        }
    }
}
