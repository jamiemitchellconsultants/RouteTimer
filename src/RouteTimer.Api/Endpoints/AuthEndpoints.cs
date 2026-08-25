using RouteTimer.Api.Auth;
using RouteTimer.Contracts.Auth;

namespace RouteTimer.Api.Endpoints;

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder routes, AuthMode mode)
    {
        routes.MapGet("/api/auth/config", (HttpContext context, IConfiguration configuration, LocalCredentialService credentials, CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return GetConfigAsync(mode, configuration, credentials, cancellationToken);
        }).AllowAnonymous();

        routes.MapGet("/api/auth/session", (HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return TypedResults.Ok(new AuthSessionResponse(context.User.Identity?.IsAuthenticated == true));
        }).AllowAnonymous();

        return routes;
    }

    private static async Task<IResult> GetConfigAsync(
        AuthMode mode,
        IConfiguration configuration,
        LocalCredentialService credentials,
        CancellationToken cancellationToken)
    {
        if (mode == AuthMode.Local)
        {
            var setupRequired = await credentials.IsSetupRequiredAsync(cancellationToken);
            return TypedResults.Ok(new AuthConfigResponse(
                AuthConfigResponse.LocalMode,
                setupRequired,
                Authority: null,
                ClientId: null,
                RedirectUri: null,
                PostLogoutRedirectUri: null));
        }

        return TypedResults.Ok(new AuthConfigResponse(
            AuthConfigResponse.KeycloakMode,
            SetupRequired: false,
            Authority: configuration["Keycloak:Authority"],
            ClientId: "routetimer-web",
            RedirectUri: "authentication/login-callback",
            PostLogoutRedirectUri: "authentication/logout-callback"));
    }
}
