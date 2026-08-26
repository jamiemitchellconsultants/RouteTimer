using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
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
            // These three endpoints are anonymous and JSON-bound, and the app's global Kestrel
            // MaxRequestBodySize is sized for training-file uploads (~501 MB). Without their own
            // limit, an unauthenticated caller could post a body that large here and make
            // System.Text.Json materialise roughly a gigabyte of UTF-16 before SetupAsync/LoginAsync
            // ever run. 4096 bytes is generous for a JSON object holding one passphrase string.
            // LocalCredentialService.MaximumPassphraseLength is a second, independent bound, not a
            // backstop for this one: it runs after deserialization, on the string System.Text.Json
            // already allocated, so it caps hasher work but does nothing to prevent that
            // allocation -- this RequestSizeLimitAttribute is the only layer that does. Confirmed
            // (via a standalone probe app, not this test suite -- WebApplicationFactory's TestServer
            // does not implement IHttpMaxRequestBodySizeFeature, so the 413 path cannot be exercised
            // in-process) that Microsoft.AspNetCore.Routing.EndpointRoutingMiddleware applies this
            // metadata to a real Kestrel server automatically. That is framework behaviour, not
            // something this codebase controls, and CI does not exercise it -- re-run the probe
            // after any .NET major-version upgrade rather than assuming it still holds.
            routes.MapPost("/api/auth/setup", SetupAsync).AllowAnonymous().WithMetadata(new RequestSizeLimitAttribute(4096));
            routes.MapPost("/api/auth/login", LoginAsync).AllowAnonymous().WithMetadata(new RequestSizeLimitAttribute(4096));
            // LogoutAsync's single-HttpContext-parameter shape matches RequestDelegate closely
            // enough that ASP0016 assumes it might be bound as one (which would discard the
            // IResult and never write the response body). The explicit Delegate cast disambiguates:
            // this is a route handler, not a RequestDelegate.
            routes.MapPost("/api/auth/logout", (Delegate)LogoutAsync).AllowAnonymous().WithMetadata(new RequestSizeLimitAttribute(4096));
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
            // Defensive backstop, not the primary mechanism: LocalCredentialRepository.TryAddAsync
            // already catches the database's insert-conflict exception itself and reports it as a
            // false return, which SetupAsync maps to AlreadyConfigured below without ever throwing.
            // This catch exists in case a different ILocalCredentialRepository implementation lets
            // that exception escape instead -- it must still fail closed as a clean Conflict rather
            // than surface as an unhandled 500.
            return AlreadyConfiguredResponse();
        }

        // Deliberately exhaustive rather than a catch-all default for the success case: a default
        // that signs the caller in is a fail-open trap for the very case that matters most here --
        // add a new LocalCredentialSetupResult value (a lockout state, say) and forget a case here,
        // and this would silently issue a valid 30-day rider session while no credential was
        // actually stored. Padded was added to that enum exactly this way once already.
        switch (result)
        {
            case LocalCredentialSetupResult.Configured:
                await SignInAsync(context);
                return TypedResults.Ok(new AuthSessionResponse(true));
            case LocalCredentialSetupResult.AlreadyConfigured:
                return AlreadyConfiguredResponse();
            case LocalCredentialSetupResult.TooShort:
                return ApiProblems.BadRequest(
                    ErrorCodes.LocalCredentialTooShort,
                    $"The passphrase must be at least {LocalCredentialService.MinimumPassphraseLength} characters.");
            case LocalCredentialSetupResult.TooLong:
                return ApiProblems.BadRequest(
                    ErrorCodes.LocalCredentialTooLong,
                    $"The passphrase must be no more than {LocalCredentialService.MaximumPassphraseLength} characters.");
            case LocalCredentialSetupResult.Padded:
                return ApiProblems.BadRequest(
                    ErrorCodes.LocalCredentialPadded,
                    "The passphrase cannot start or end with whitespace. Leading and trailing whitespace is not allowed because it would have to be retyped exactly on every sign-in.");
            default:
                throw new InvalidOperationException($"Unhandled {nameof(LocalCredentialSetupResult)} value: {result}.");
        }
    }

    private static async Task<IResult> LoginAsync(
        LocalLoginRequest request,
        LocalCredentialService credentials,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        context.Response.Headers.CacheControl = "no-store";

        var verification = await credentials.VerifyAsync(request.Passphrase, cancellationToken);

        // Rejected and NotConfigured deliberately produce an identical response. The distinction
        // exists only so lockout counts genuine guesses against a real credential, and must not be
        // observable to the caller.
        switch (verification)
        {
            case LocalCredentialVerificationResult.Succeeded:
                await SignInAsync(context);
                return TypedResults.Ok(new AuthSessionResponse(true));
            case LocalCredentialVerificationResult.Rejected:
            case LocalCredentialVerificationResult.NotConfigured:
                return ApiProblems.Create(
                    StatusCodes.Status401Unauthorized,
                    ErrorCodes.LocalCredentialRejected,
                    "That passphrase was not recognised.");
            default:
                throw new InvalidOperationException(
                    $"Unhandled {nameof(LocalCredentialVerificationResult)} value: {verification}.");
        }
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
                new Claim(ClaimTypes.Name, LocalAuthenticationDefaults.RiderRole),
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
