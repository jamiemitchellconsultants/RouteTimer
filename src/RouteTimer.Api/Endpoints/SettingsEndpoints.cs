using RouteTimer.Api.Errors;
using RouteTimer.Contracts.Errors;
using RouteTimer.Contracts.Settings;
using RouteTimer.Services.Settings;

namespace RouteTimer.Api.Endpoints;

public static class SettingsEndpoints
{
    public static IEndpointRouteBuilder MapSettingsEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/api/settings/google-maps-key", GetStatusAsync);
        routes.MapPut("/api/settings/google-maps-key", SaveAsync);
        routes.MapDelete("/api/settings/google-maps-key", DeleteAsync);

        // Deliberately a POST. UseSameOriginEnforcement exempts GET, HEAD, and OPTIONS from its
        // Sec-Fetch-Site check, so a GET that returns the key would be readable by a page served
        // from any other port on this host -- exactly the case that middleware exists to close.
        routes.MapPost("/api/settings/google-maps-key/use", RevealAsync);
        return routes;
    }

    private static async Task<IResult> GetStatusAsync(
        GoogleMapsKeyService keys,
        CancellationToken cancellationToken)
    {
        var status = await keys.GetStatusAsync(cancellationToken);
        return TypedResults.Ok(new GoogleMapsKeyStatusResponse(status.Configured, status.Hint, status.StorageAvailable));
    }

    private static async Task<IResult> SaveAsync(
        SaveGoogleMapsKeyRequest request,
        GoogleMapsKeyService keys,
        CancellationToken cancellationToken)
    {
        try
        {
            await keys.SaveAsync(request.ApiKey, cancellationToken);
            return TypedResults.NoContent();
        }
        catch (GoogleMapsKeyInvalidException)
        {
            return ApiProblems.BadRequest(
                ErrorCodes.GoogleMapsKeyInvalid,
                "Enter a Google Maps API key of at most 512 characters.");
        }
        catch (GoogleMapsKeyStorageUnavailableException)
        {
            return ApiProblems.Conflict(
                ErrorCodes.GoogleMapsKeyStorageUnavailable,
                "This deployment has no Google Maps key encryption key configured, so keys cannot be saved.");
        }
    }

    private static async Task<IResult> DeleteAsync(
        GoogleMapsKeyService keys,
        CancellationToken cancellationToken)
    {
        await keys.DeleteAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<IResult> RevealAsync(
        GoogleMapsKeyService keys,
        CancellationToken cancellationToken)
    {
        try
        {
            return TypedResults.Ok(new GoogleMapsKeyResponse(await keys.RevealAsync(cancellationToken)));
        }
        catch (GoogleMapsKeyNotStoredException)
        {
            return ApiProblems.NotFound(ErrorCodes.GoogleMapsKeyNotStored, "No Google Maps API key is stored.");
        }
        catch (GoogleMapsKeyStorageUnavailableException)
        {
            return ApiProblems.Conflict(
                ErrorCodes.GoogleMapsKeyStorageUnavailable,
                "This deployment has no Google Maps key encryption key configured.");
        }
    }
}
