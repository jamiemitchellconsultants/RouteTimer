using System.Net;

namespace RouteTimer.Api.Tests;

public sealed class SpaFallbackTests
{
    // This project has no reference to RouteTimer.Client and no wwwroot content of its own -- the
    // compiled client is only ever copied in by the Docker build (Dockerfile:
    // COPY --from=build /out/client/wwwroot ./wwwroot). So in this test host the fallback endpoint
    // legitimately 404s trying to serve a file that does not exist here. What these tests can and do
    // prove, without needing that file: the request reaches the fallback file-serving attempt at all,
    // rather than being rejected by the fallback authorization policy before it gets that far.

    [Fact]
    public async Task An_unmatched_client_side_route_is_not_blocked_by_the_fallback_authorization_policy()
    {
        await using var app = new RouteTimerApiFactory().WithAuthMode("Keycloak");
        using var client = app.CreateClient();

        using var response = await client.GetAsync("/predictions", CancellationToken.None);

        // Before MapFallbackToFile existed, the fallback authorization policy applied to this
        // unmatched path and returned 401 before any attempt to serve a file -- confirmed by
        // temporarily removing the mapping and observing that exact status, and separately by
        // creating a real wwwroot/index.html on disk and observing a genuine 200. A rider hitting
        // reload on any client-side route, in either auth mode, got a bare 401 instead of the app.
        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task The_oidc_login_callback_path_is_not_blocked_by_the_fallback_authorization_policy()
    {
        await using var app = new RouteTimerApiFactory().WithAuthMode("Keycloak");
        using var client = app.CreateClient();

        using var response = await client.GetAsync("/authentication/login-callback", CancellationToken.None);

        // This is the exact path Keycloak redirects the browser back to after sign-in. Without the
        // fallback route, that redirect landed on 401 and the sign-in flow could never complete.
        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Health_routes_still_answer_and_are_not_swallowed_by_the_fallback()
    {
        await using var app = new RouteTimerApiFactory().WithAuthMode("Keycloak");
        using var client = app.CreateClient();

        using var response = await client.GetAsync("/health/live", CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync(CancellationToken.None));
    }
}
