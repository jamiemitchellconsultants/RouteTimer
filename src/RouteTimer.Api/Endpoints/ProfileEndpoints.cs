using RouteTimer.Api.Errors;
using RouteTimer.Contracts.Profile;
using RouteTimer.Services.Profile;

namespace RouteTimer.Api.Endpoints;

public static class ProfileEndpoints
{
    public static IEndpointRouteBuilder MapProfileEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/api/profile", GetProfileAsync);
        routes.MapPut("/api/profile", UpdateProfileAsync);
        return routes;
    }

    private static async Task<IResult> GetProfileAsync(ProfileService profiles, CancellationToken cancellationToken) =>
        (await profiles.GetAsync(cancellationToken)) is { } profile
            ? TypedResults.Ok(new ProfileResponse(profile.RiderWeightKg, profile.BikeAndEquipmentWeightKg))
            : TypedResults.NotFound();

    private static async Task<IResult> UpdateProfileAsync(
        UpdateProfileRequest request,
        ProfileService profiles,
        CancellationToken cancellationToken)
    {
        try
        {
            var profile = await profiles.UpdateAsync(request.RiderWeightKg, request.BikeAndEquipmentWeightKg, cancellationToken);
            return TypedResults.Ok(new ProfileResponse(profile.RiderWeightKg, profile.BikeAndEquipmentWeightKg));
        }
        catch (ProfileValidationException exception)
        {
            return ApiProblems.InvalidProfile(exception);
        }
    }
}
