using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RouteTimer.Contracts.Auth;
using RouteTimer.Services.Persistence;

namespace RouteTimer.Api.Tests.Auth;

public sealed class AuthConfigEndpointTests
{
    private const string KeycloakAuthority = "https://keycloak.test.invalid/realms/routetimer";

    [Fact]
    public async Task Config_is_anonymous_and_reports_keycloak_settings_in_keycloak_mode()
    {
        await using var app = new RouteTimerApiFactory().WithAuthMode("Keycloak");
        using var client = app.CreateClient();

        using var response = await client.GetAsync("/api/auth/config", CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var config = await response.Content.ReadFromJsonAsync<AuthConfigResponse>(CancellationToken.None);
        Assert.NotNull(config);
        Assert.Equal("Keycloak", config.Mode);
        Assert.False(config.SetupRequired);
        Assert.Equal("routetimer-web", config.ClientId);
        Assert.Equal("authentication/login-callback", config.RedirectUri);
        Assert.Equal("authentication/logout-callback", config.PostLogoutRedirectUri);
        Assert.Equal(KeycloakAuthority, config.Authority);
    }

    [Fact]
    public async Task Config_is_anonymous_and_reports_setup_required_in_local_mode()
    {
        await using var app = new RouteTimerApiFactory()
            .WithAuthMode("Local")
            .WithServices(services =>
            {
                services.RemoveAll<ILocalCredentialRepository>();
                services.AddSingleton<ILocalCredentialRepository>(new FakeLocalCredentialRepository(null));
            });
        using var client = app.CreateClient();

        using var response = await client.GetAsync("/api/auth/config", CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var config = await response.Content.ReadFromJsonAsync<AuthConfigResponse>(CancellationToken.None);
        Assert.NotNull(config);
        Assert.Equal("Local", config.Mode);
        Assert.True(config.SetupRequired);
        Assert.Null(config.Authority);
        Assert.Null(config.ClientId);
        Assert.Null(config.RedirectUri);
        Assert.Null(config.PostLogoutRedirectUri);
    }

    [Fact]
    public async Task Config_reports_setup_complete_once_a_credential_exists()
    {
        await using var app = new RouteTimerApiFactory()
            .WithAuthMode("Local")
            .WithServices(services =>
            {
                services.RemoveAll<ILocalCredentialRepository>();
                services.AddSingleton<ILocalCredentialRepository>(new FakeLocalCredentialRepository("a-hash"));
            });
        using var client = app.CreateClient();

        var config = await client.GetFromJsonAsync<AuthConfigResponse>("/api/auth/config", CancellationToken.None);

        Assert.NotNull(config);
        Assert.False(config.SetupRequired);
    }

    [Fact]
    public async Task Session_reports_anonymous_for_an_unauthenticated_caller()
    {
        await using var app = new RouteTimerApiFactory().WithAuthMode("Keycloak");
        using var client = app.CreateClient();

        var session = await client.GetFromJsonAsync<AuthSessionResponse>("/api/auth/session", CancellationToken.None);

        Assert.NotNull(session);
        Assert.False(session.Authenticated);
    }

    [Fact]
    public async Task Session_reports_anonymous_in_local_mode_where_no_scheme_is_registered()
    {
        await using var app = new RouteTimerApiFactory().WithAuthMode("Local");
        using var client = app.CreateClient();

        using var response = await client.GetAsync("/api/auth/session", CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var session = await response.Content.ReadFromJsonAsync<AuthSessionResponse>(CancellationToken.None);
        Assert.NotNull(session);
        Assert.False(session.Authenticated);
    }

    [Fact]
    public async Task Session_reports_an_authenticated_caller()
    {
        await using var app = new RouteTimerApiFactory().WithRiderAuthentication();
        using var client = app.CreateClient();

        var session = await client.GetFromJsonAsync<AuthSessionResponse>("/api/auth/session", CancellationToken.None);

        Assert.NotNull(session);
        Assert.True(session.Authenticated);
    }

    [Fact]
    public async Task Config_and_session_are_not_cacheable()
    {
        await using var app = new RouteTimerApiFactory();
        using var client = app.CreateClient();

        using var config = await client.GetAsync("/api/auth/config", CancellationToken.None);
        using var session = await client.GetAsync("/api/auth/session", CancellationToken.None);

        Assert.Equal("no-store", Assert.Single(config.Headers.CacheControl!.ToString().Split(", ")));
        Assert.Equal("no-store", Assert.Single(session.Headers.CacheControl!.ToString().Split(", ")));
    }

    internal sealed class FakeLocalCredentialRepository(string? initialHash) : ILocalCredentialRepository
    {
        private string? hash = initialHash;

        public Task<string?> GetAsync(CancellationToken cancellationToken) => Task.FromResult(hash);

        public Task SetAsync(string passwordHash, CancellationToken cancellationToken)
        {
            hash = passwordHash;
            return Task.CompletedTask;
        }
    }
}
