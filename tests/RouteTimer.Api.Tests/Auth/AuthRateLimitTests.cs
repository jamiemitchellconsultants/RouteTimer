using System.Net;
using System.Text.Json;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RouteTimer.Contracts.Auth;
using RouteTimer.Contracts.Errors;
using RouteTimer.Services.Persistence;

namespace RouteTimer.Api.Tests.Auth;

public sealed class AuthRateLimitTests
{
    private const string Passphrase = "correct horse battery staple";

    [Fact]
    public async Task Repeated_failed_logins_are_locked_out_with_a_problem_response()
    {
        await using var app = LocalApp("an-existing-hash");
        using var client = app.CreateClient();

        var statuses = new List<HttpStatusCode>();
        HttpResponseMessage? lockedOut = null;
        for (var attempt = 0; attempt < 12; attempt++)
        {
            var response = await client.PostAsJsonAsync(
                "/api/auth/login",
                new LocalLoginRequest("wrong passphrase entirely"),
                CancellationToken.None);
            statuses.Add(response.StatusCode);
            if (response.StatusCode == HttpStatusCode.TooManyRequests && lockedOut is null)
            {
                lockedOut = response;
            }
            else
            {
                response.Dispose();
            }
        }

        Assert.Equal(HttpStatusCode.Unauthorized, statuses[0]);
        Assert.Contains(HttpStatusCode.TooManyRequests, statuses);

        // A bare 429 tells the client nothing; the rider needs to know they are locked out rather
        // than that the passphrase was wrong.
        Assert.NotNull(lockedOut);
        var problem = await lockedOut.Content.ReadFromJsonAsync<JsonElement>(CancellationToken.None);
        Assert.Equal(ErrorCodes.LocalCredentialLockedOut, problem.GetProperty("code").GetString());
        lockedOut.Dispose();
    }

    [Fact]
    public async Task The_generous_policy_does_not_lock_out_ordinary_config_polling()
    {
        await using var app = LocalApp(null);
        using var client = app.CreateClient();

        // Comfortably more than the strict login budget, and more than any real page load.
        for (var attempt = 0; attempt < 30; attempt++)
        {
            using var response = await client.GetAsync("/api/auth/config", CancellationToken.None);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    [Fact]
    public async Task Login_is_not_starved_by_the_generous_policy_on_its_siblings()
    {
        await using var app = LocalApp(null);
        using var client = app.CreateClient();
        await client.PostAsJsonAsync("/api/auth/setup", new SetLocalCredentialRequest(Passphrase), CancellationToken.None);
        await client.PostAsync("/api/auth/logout", content: null, CancellationToken.None);

        for (var attempt = 0; attempt < 20; attempt++)
        {
            using var poll = await client.GetAsync("/api/auth/session", CancellationToken.None);
            Assert.Equal(HttpStatusCode.OK, poll.StatusCode);
        }

        using var login = await client.PostAsJsonAsync(
            "/api/auth/login",
            new LocalLoginRequest(Passphrase),
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    [Fact]
    public async Task Lockout_also_blocks_the_correct_passphrase_for_the_rest_of_the_window()
    {
        await using var app = LocalApp(null);
        using var client = app.CreateClient();
        await client.PostAsJsonAsync("/api/auth/setup", new SetLocalCredentialRequest(Passphrase), CancellationToken.None);
        await client.PostAsync("/api/auth/logout", content: null, CancellationToken.None);

        for (var attempt = 0; attempt < 6; attempt++)
        {
            using var wrong = await client.PostAsJsonAsync(
                "/api/auth/login",
                new LocalLoginRequest("wrong passphrase entirely"),
                CancellationToken.None);
        }

        using var correct = await client.PostAsJsonAsync(
            "/api/auth/login",
            new LocalLoginRequest(Passphrase),
            CancellationToken.None);

        // Deliberate, not an oversight. A lockout that the attacker can step around by guessing
        // correctly is not a lockout, and on a loopback-bound single-rider install the legitimate
        // rider and the only possible attacker are the same local user. The cost is that a rider
        // who mistypes six times waits out the remainder of a one-minute window.
        Assert.Equal(HttpStatusCode.TooManyRequests, correct.StatusCode);
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
}
