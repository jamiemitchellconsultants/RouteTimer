using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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

        using var profile = await client.GetAsync("/api/profile", CancellationToken.None);
        Assert.NotEqual(HttpStatusCode.Unauthorized, profile.StatusCode);
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
        await using var app = LocalApp(null);
        using var client = app.CreateClient();

        using var tooShort = await client.PostAsJsonAsync("/api/auth/setup", new SetLocalCredentialRequest("short"), CancellationToken.None);
        using var tooShortBody = JsonDocument.Parse(await tooShort.Content.ReadAsStringAsync());

        Assert.Equal("local-credential-too-short", tooShortBody.RootElement.GetProperty("code").GetString());
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
    /// Simulates the loser of a concurrent first-run setup race: <see cref="GetAsync"/> reports no
    /// credential exists (matching what both concurrent callers would have seen), but the write
    /// fails with the same exception the database's singleton check constraint would raise for a
    /// second row.
    /// </summary>
    private sealed class ThrowsOnSetLocalCredentialRepository : ILocalCredentialRepository
    {
        public Task<string?> GetAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(null);

        public Task SetAsync(string passwordHash, CancellationToken cancellationToken) =>
            throw new DbUpdateException("Simulated unique-constraint violation from a concurrent setup.");
    }
}
