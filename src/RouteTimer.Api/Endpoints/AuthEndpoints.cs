using RouteTimer.Api.Auth;
using RouteTimer.Contracts.Auth;

namespace RouteTimer.Api.Endpoints;

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder routes, AuthMode mode)
    {
        routes.MapGet("/api/auth/config", (IConfiguration configuration, LocalCredentialService credentials, CancellationToken cancellationToken) =>
            GetConfigAsync(mode, configuration, credentials, cancellationToken)).AllowAnonymous();

        routes.MapGet("/api/auth/session", (HttpContext context) =>
            TypedResults.Ok(new AuthSessionResponse(context.User.Identity?.IsAuthenticated == true))).AllowAnonymous();

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
                nameof(AuthMode.Local),
                setupRequired,
                Authority: null,
                ClientId: null,
                RedirectUri: null,
                PostLogoutRedirectUri: null));
        }

        return TypedResults.Ok(new AuthConfigResponse(
            nameof(AuthMode.Keycloak),
            SetupRequired: false,
            Authority: configuration["Keycloak:Authority"],
            ClientId: "routetimer-web",
            RedirectUri: "authentication/login-callback",
            PostLogoutRedirectUri: "/"));
    }
}
