using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using RouteTimer.Api.Auth;
using RouteTimer.Api.Errors;
using RouteTimer.Contracts.Auth;
using RouteTimer.Contracts.Errors;

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

        if (mode == AuthMode.Local)
        {
            routes.MapPost("/api/auth/setup", SetupAsync).AllowAnonymous();
            routes.MapPost("/api/auth/login", LoginAsync).AllowAnonymous();
            // LogoutAsync's single-HttpContext-parameter shape matches RequestDelegate closely
            // enough that ASP0016 assumes it might be bound as one (which would discard the
            // IResult and never write the response body). The explicit Delegate cast disambiguates:
            // this is a route handler, not a RequestDelegate.
            routes.MapPost("/api/auth/logout", (Delegate)LogoutAsync).AllowAnonymous();
        }

        return routes;
    }

    private static async Task<IResult> SetupAsync(
        SetLocalCredentialRequest request,
        LocalCredentialService credentials,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        context.Response.Headers.CacheControl = "no-store";

        LocalCredentialSetupResult result;
        try
        {
            result = await credentials.SetupAsync(request.Passphrase, cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Two concurrent first-run setup requests both see "no credential" and both attempt to
            // store one. The database's singleton check constraint resolves the race correctly --
            // the loser gets a unique-violation and no second row appears -- but that surfaces here
            // as a DbUpdateException, which must map to the same clean response as AlreadyConfigured
            // rather than an unhandled 500.
            return AlreadyConfiguredResponse();
        }

        switch (result)
        {
            case LocalCredentialSetupResult.AlreadyConfigured:
                return AlreadyConfiguredResponse();
            case LocalCredentialSetupResult.TooShort:
                return ApiProblems.BadRequest(
                    ErrorCodes.LocalCredentialTooShort,
                    $"The passphrase must be at least {LocalCredentialService.MinimumPassphraseLength} characters.");
            case LocalCredentialSetupResult.Padded:
                return ApiProblems.BadRequest(
                    ErrorCodes.LocalCredentialPadded,
                    "The passphrase cannot start or end with a space. Leading and trailing spaces are not allowed because they would have to be retyped exactly on every sign-in.");
            default:
                await SignInAsync(context);
                return TypedResults.Ok(new AuthSessionResponse(true));
        }
    }

    private static async Task<IResult> LoginAsync(
        LocalLoginRequest request,
        LocalCredentialService credentials,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        context.Response.Headers.CacheControl = "no-store";

        if (!await credentials.VerifyAsync(request.Passphrase, cancellationToken))
        {
            return ApiProblems.Create(
                StatusCodes.Status401Unauthorized,
                ErrorCodes.LocalCredentialRejected,
                "That passphrase was not recognised.");
        }

        await SignInAsync(context);
        return TypedResults.Ok(new AuthSessionResponse(true));
    }

    private static async Task<IResult> LogoutAsync(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        await context.SignOutAsync(LocalAuthenticationDefaults.AuthenticationScheme);
        return TypedResults.Ok(new AuthSessionResponse(false));
    }

    private static Task SignInAsync(HttpContext context)
    {
        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.Name, "rider"),
                new Claim(ClaimTypes.Role, LocalAuthenticationDefaults.RiderRole)
            ],
            LocalAuthenticationDefaults.AuthenticationScheme,
            ClaimTypes.Name,
            ClaimTypes.Role);

        return context.SignInAsync(
            LocalAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity),
            new AuthenticationProperties { IsPersistent = true });
    }

    private static IResult AlreadyConfiguredResponse() =>
        ApiProblems.Conflict(
            ErrorCodes.LocalCredentialAlreadyConfigured,
            "A passphrase has already been set for this installation. Sign in with it, or clear the stored credential to run first-use setup again.");

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
