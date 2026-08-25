using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RouteTimer.Contracts.Auth;
using RouteTimer.Services.Persistence;

namespace RouteTimer.Api.Tests.Auth;

public sealed class LocalAuthEndpointTests
{
    private const string Passphrase = "correct horse battery staple";

    [Fact]
    public async Task Setup_configures_the_credential_and_signs_the_rider_in()
    {
        await using var app = LocalApp(null);
        using var client = app.CreateClient();

        using var response = await client.PostAsJsonAsync(
            "/api/auth/setup",
            new SetLocalCredentialRequest(Passphrase),
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(response.Headers, header => header.Key == "Set-Cookie");

        // Authenticated but no profile row exists yet: 404, not 200 or (crucially) 401. A bare
        // "not Unauthorized" would also pass for a 500, which proves nothing about authentication.
        using var profile = await client.GetAsync("/api/profile", CancellationToken.None);
        Assert.Equal(HttpStatusCode.NotFound, profile.StatusCode);
    }

    [Fact]
    public async Task Setup_is_refused_once_a_credential_exists()
    {
        await using var app = LocalApp("an-existing-hash");
        using var client = app.CreateClient();

        using var response = await client.PostAsJsonAsync(
            "/api/auth/setup",
            new SetLocalCredentialRequest(Passphrase),
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Setup_rejects_a_passphrase_below_the_minimum_length()
    {
        await using var app = LocalApp(null);
        using var client = app.CreateClient();

        using var response = await client.PostAsJsonAsync(
            "/api/auth/setup",
            new SetLocalCredentialRequest("short"),
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Login_with_the_configured_passphrase_grants_access_to_a_protected_endpoint()
    {
        await using var app = LocalApp(null);
        using var client = app.CreateClient();
        await client.PostAsJsonAsync("/api/auth/setup", new SetLocalCredentialRequest(Passphrase), CancellationToken.None);
        await client.PostAsync("/api/auth/logout", content: null, CancellationToken.None);

        using var login = await client.PostAsJsonAsync(
            "/api/auth/login",
            new LocalLoginRequest(Passphrase),
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var session = await client.GetFromJsonAsync<AuthSessionResponse>("/api/auth/session", CancellationToken.None);
        Assert.NotNull(session);
        Assert.True(session.Authenticated);
    }

    [Fact]
    public async Task Login_with_the_wrong_passphrase_is_rejected()
    {
        await using var app = LocalApp(null);
        using var client = app.CreateClient();
        await client.PostAsJsonAsync("/api/auth/setup", new SetLocalCredentialRequest(Passphrase), CancellationToken.None);
        await client.PostAsync("/api/auth/logout", content: null, CancellationToken.None);

        using var login = await client.PostAsJsonAsync(
            "/api/auth/login",
            new LocalLoginRequest("not the passphrase at all"),
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
    }

    [Fact]
    public async Task Logout_ends_the_session()
    {
        await using var app = LocalApp(null);
        using var client = app.CreateClient();
        await client.PostAsJsonAsync("/api/auth/setup", new SetLocalCredentialRequest(Passphrase), CancellationToken.None);

        await client.PostAsync("/api/auth/logout", content: null, CancellationToken.None);

        var session = await client.GetFromJsonAsync<AuthSessionResponse>("/api/auth/session", CancellationToken.None);
        Assert.NotNull(session);
        Assert.False(session.Authenticated);
    }

    [Fact]
    public async Task Protected_endpoints_reject_an_anonymous_caller_in_local_mode()
    {
        await using var app = LocalApp("an-existing-hash");
        using var client = app.CreateClient();

        using var response = await client.GetAsync("/api/profile", CancellationToken.None);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Logout_is_reachable_by_an_anonymous_caller_with_no_session()
    {
        await using var app = LocalApp("an-existing-hash");
        using var client = app.CreateClient();

        using var response = await client.PostAsync("/api/auth/logout", content: null, CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var session = await response.Content.ReadFromJsonAsync<AuthSessionResponse>(CancellationToken.None);
        Assert.NotNull(session);
        Assert.False(session.Authenticated);
    }

    [Fact]
    public async Task Setup_and_login_do_not_exist_in_keycloak_mode()
    {
        // Authenticated as rider: the fallback authorization policy intercepts unmatched routes
        // for an anonymous caller before routing can report there is no endpoint, which would
        // otherwise surface as 401 rather than the 404 this test wants to prove.
        await using var app = new RouteTimerApiFactory().WithRiderAuthentication();
        using var client = app.CreateClient();

        using var setup = await client.PostAsJsonAsync("/api/auth/setup", new SetLocalCredentialRequest(Passphrase), CancellationToken.None);
        using var login = await client.PostAsJsonAsync("/api/auth/login", new LocalLoginRequest(Passphrase), CancellationToken.None);
        using var logout = await client.PostAsync("/api/auth/logout", content: null, CancellationToken.None);

        Assert.Equal(HttpStatusCode.NotFound, setup.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, login.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, logout.StatusCode);
    }

    [Fact]
    public async Task Setup_issues_an_httponly_strict_samesite_cookie_that_is_not_unconditionally_secure()
    {
        await using var app = LocalApp(null);
        using var client = app.CreateClient();

        using var response = await client.PostAsJsonAsync(
            "/api/auth/setup",
            new SetLocalCredentialRequest(Passphrase),
            CancellationToken.None);

        var setCookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));
        Assert.Contains("HttpOnly", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SameSite=Strict", setCookie, StringComparison.OrdinalIgnoreCase);
        // The request arrived over plain HTTP, so SecurePolicy=SameAsRequest must not mark the
        // cookie Secure -- an unconditionally Secure cookie would never be sent back on loopback HTTP.
        Assert.DoesNotContain("secure", setCookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Setup_endpoints_set_no_store_cache_control()
    {
        await using var app = LocalApp(null);
        using var client = app.CreateClient();

        using var setup = await client.PostAsJsonAsync("/api/auth/setup", new SetLocalCredentialRequest(Passphrase), CancellationToken.None);
        using var logout = await client.PostAsync("/api/auth/logout", content: null, CancellationToken.None);
        using var login = await client.PostAsJsonAsync("/api/auth/login", new LocalLoginRequest(Passphrase), CancellationToken.None);

        // The cookie handler's own SignInAsync/SignOutAsync append "no-cache" alongside the
        // "no-store" this endpoint sets explicitly, so the header carries both rather than
        // "no-store" alone -- still strictly no less cache-preventing than intended.
        Assert.Contains("no-store", setup.Headers.CacheControl?.ToString());
        Assert.Contains("no-store", logout.Headers.CacheControl?.ToString());
        Assert.Contains("no-store", login.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task Setup_rejects_a_passphrase_padded_with_leading_or_trailing_whitespace()
    {
        await using var app = LocalApp(null);
        using var client = app.CreateClient();

        using var response = await client.PostAsJsonAsync(
            "/api/auth/setup",
            new SetLocalCredentialRequest("a" + new string(' ', 11)),
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("local-credential-padded", body.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Setup_uses_distinct_error_codes_for_too_short_versus_padded_passphrases()
    {
        // Two separate apps: each has its own never-configured repository, since the first setup
        // call on either would otherwise make the second return AlreadyConfigured instead of the
        // validation code under test.
        await using var tooShortApp = LocalApp(null);
        using var tooShortClient = tooShortApp.CreateClient();
        await using var paddedApp = LocalApp(null);
        using var paddedClient = paddedApp.CreateClient();

        using var tooShort = await tooShortClient.PostAsJsonAsync("/api/auth/setup", new SetLocalCredentialRequest("short"), CancellationToken.None);
        using var tooShortBody = JsonDocument.Parse(await tooShort.Content.ReadAsStringAsync());
        using var padded = await paddedClient.PostAsJsonAsync("/api/auth/setup", new SetLocalCredentialRequest("a" + new string(' ', 11)), CancellationToken.None);
        using var paddedBody = JsonDocument.Parse(await padded.Content.ReadAsStringAsync());

        var tooShortCode = tooShortBody.RootElement.GetProperty("code").GetString();
        var paddedCode = paddedBody.RootElement.GetProperty("code").GetString();
        Assert.Equal("local-credential-too-short", tooShortCode);
        Assert.Equal("local-credential-padded", paddedCode);
        Assert.NotEqual(tooShortCode, paddedCode);
    }

    [Fact]
    public async Task Setup_rejects_a_passphrase_over_the_maximum_length_with_its_own_error_code()
    {
        await using var app = LocalApp(null);
        using var client = app.CreateClient();

        using var response = await client.PostAsJsonAsync(
            "/api/auth/setup",
            new SetLocalCredentialRequest(new string('a', 300)),
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("local-credential-too-long", body.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public void Setup_login_and_logout_endpoints_declare_a_4096_byte_request_size_limit()
    {
        // This cannot be a behavioural "POST an oversized body, expect 413" test: verified against
        // this exact endpoint (via a standalone probe app, not this suite) that
        // Microsoft.AspNetCore.Routing.EndpointRoutingMiddleware *does* apply IRequestSizeLimitMetadata
        // onto IHttpMaxRequestBodySizeFeature automatically for minimal API endpoints -- but
        // Microsoft.AspNetCore.TestHost.TestServer, which WebApplicationFactory uses for every test
        // in this suite, does not implement IHttpMaxRequestBodySizeFeature at all. The framework logs
        // "This server does not support the IHttpMaxRequestBodySizeFeature" and the limit is silently
        // not enforced in-process; a real Kestrel-hosted request over the limit gets 413. So the
        // closest in-process proof is that the metadata itself is attached correctly -- the necessary
        // condition for Kestrel's built-in enforcement to apply in production.
        using var app = LocalApp(null);

        var endpoints = app.Services.GetServices<EndpointDataSource>()
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .ToList();

        foreach (var pattern in new[] { "/api/auth/setup", "/api/auth/login", "/api/auth/logout" })
        {
            var endpoint = Assert.Single(endpoints, e => e.RoutePattern.RawText == pattern);
            var metadata = endpoint.Metadata.GetMetadata<IRequestSizeLimitMetadata>();
            Assert.NotNull(metadata);
            Assert.Equal(4096, metadata.MaxRequestBodySize);
        }
    }

    [Fact]
    public async Task Setup_maps_a_concurrent_setup_race_to_the_same_conflict_as_already_configured()
    {
        await using var app = new RouteTimerApiFactory()
            .WithAuthMode("Local")
            .WithServices(services =>
            {
                services.RemoveAll<ILocalCredentialRepository>();
                services.AddSingleton<ILocalCredentialRepository>(new ThrowsOnSetLocalCredentialRepository());
            });
        using var client = app.CreateClient();

        using var response = await client.PostAsJsonAsync(
            "/api/auth/setup",
            new SetLocalCredentialRequest(Passphrase),
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("local-credential-already-configured", body.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Login_with_the_wrong_passphrase_while_already_signed_in_leaves_the_existing_session_intact()
    {
        await using var app = LocalApp(null);
        using var client = app.CreateClient();
        await client.PostAsJsonAsync("/api/auth/setup", new SetLocalCredentialRequest(Passphrase), CancellationToken.None);

        using var wrongLogin = await client.PostAsJsonAsync(
            "/api/auth/login",
            new LocalLoginRequest("not the passphrase at all"),
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.Unauthorized, wrongLogin.StatusCode);
        Assert.DoesNotContain(wrongLogin.Headers, header => header.Key == "Set-Cookie");

        // The session established by the earlier setup call must still work -- a failed login
        // attempt must not have signed the caller out or otherwise disturbed it.
        var session = await client.GetFromJsonAsync<AuthSessionResponse>("/api/auth/session", CancellationToken.None);
        Assert.NotNull(session);
        Assert.True(session.Authenticated);
    }

    [Fact]
    public async Task Failure_responses_never_carry_a_set_cookie_header()
    {
        await using var configuredApp = LocalApp("an-existing-hash");
        using var configuredClient = configuredApp.CreateClient();
        await using var freshApp = LocalApp(null);
        using var freshClient = freshApp.CreateClient();

        using var alreadyConfigured = await configuredClient.PostAsJsonAsync("/api/auth/setup", new SetLocalCredentialRequest(Passphrase), CancellationToken.None);
        using var wrongLogin = await configuredClient.PostAsJsonAsync("/api/auth/login", new LocalLoginRequest("wrong passphrase entirely"), CancellationToken.None);
        using var tooShort = await freshClient.PostAsJsonAsync("/api/auth/setup", new SetLocalCredentialRequest("short"), CancellationToken.None);
        using var padded = await freshClient.PostAsJsonAsync("/api/auth/setup", new SetLocalCredentialRequest("a" + new string(' ', 11)), CancellationToken.None);

        Assert.Equal(HttpStatusCode.Conflict, alreadyConfigured.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, wrongLogin.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, tooShort.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, padded.StatusCode);
        Assert.DoesNotContain(alreadyConfigured.Headers, header => header.Key == "Set-Cookie");
        Assert.DoesNotContain(wrongLogin.Headers, header => header.Key == "Set-Cookie");
        Assert.DoesNotContain(tooShort.Headers, header => header.Key == "Set-Cookie");
        Assert.DoesNotContain(padded.Headers, header => header.Key == "Set-Cookie");
    }

    [Fact]
    public async Task A_session_is_revoked_once_the_stored_credential_is_removed()
    {
        // The recovery procedure the 409-Conflict message itself recommends is "clear the stored
        // credential". The cookie is a self-contained, data-protected ticket with no server-side
        // session store, so nothing re-checks the credential unless OnValidatePrincipal does --
        // this proves it does, and that a session survives right up until the row is actually gone.
        var repository = new AuthConfigEndpointTests.FakeLocalCredentialRepository(null);
        await using var app = new RouteTimerApiFactory()
            .WithAuthMode("Local")
            .WithServices(services =>
            {
                services.RemoveAll<ILocalCredentialRepository>();
                services.AddSingleton<ILocalCredentialRepository>(repository);
            });
        using var client = app.CreateClient();
        await client.PostAsJsonAsync("/api/auth/setup", new SetLocalCredentialRequest(Passphrase), CancellationToken.None);

        using var beforeDeletion = await client.GetAsync("/api/profile", CancellationToken.None);
        Assert.NotEqual(HttpStatusCode.Unauthorized, beforeDeletion.StatusCode);

        repository.Clear();

        using var afterDeletion = await client.GetAsync("/api/profile", CancellationToken.None);
        Assert.Equal(HttpStatusCode.Unauthorized, afterDeletion.StatusCode);
    }

    private static RouteTimerApiFactory LocalApp(string? initialHash) =>
        new RouteTimerApiFactory()
            .WithAuthMode("Local")
            .WithServices(services =>
            {
                services.RemoveAll<ILocalCredentialRepository>();
                services.AddSingleton<ILocalCredentialRepository>(
                    new AuthConfigEndpointTests.FakeLocalCredentialRepository(initialHash));
            });

    /// <summary>
    /// Defensive-backstop fixture, not a simulation of the real race window: the production
    /// repository (<c>LocalCredentialRepository.TryAddAsync</c>) already catches the database's
    /// insert-conflict <see cref="DbUpdateException"/> itself and reports it as a plain <c>false</c>
    /// return -- that is what actually resolves a concurrent setup race now (see
    /// <c>LocalCredentialServiceTests</c> for that invariant). This fixture instead proves that
    /// <c>AuthEndpoints.SetupAsync</c> still maps the exception to a clean Conflict if some other
    /// <see cref="ILocalCredentialRepository"/> implementation ever lets it escape uncaught.
    /// </summary>
    private sealed class ThrowsOnSetLocalCredentialRepository : ILocalCredentialRepository
    {
        public Task<string?> GetAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(null);

        public Task<bool> TryAddAsync(string passwordHash, CancellationToken cancellationToken) =>
            throw new DbUpdateException("Simulated unique-constraint violation from a concurrent setup.");

        public Task SetAsync(string passwordHash, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not used by this test.");
    }
}
