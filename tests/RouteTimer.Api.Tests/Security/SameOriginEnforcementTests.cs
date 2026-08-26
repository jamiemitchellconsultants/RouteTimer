using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace RouteTimer.Api.Tests.Security;

/// <summary>
/// Covers the CSRF gap SameSite=Strict alone does not close: SameSite is site-scoped and ports are
/// not part of a site, so a page on a different localhost port is same-site (SameSite=Strict lets
/// its cookie-bearing requests through) but is not same-origin. Exercised against
/// <c>PUT /api/profile</c> -- an ordinary non-GET, non-auth endpoint -- to prove the enforcement is
/// global middleware, not something wired only into the local-mode auth endpoints.
/// </summary>
public sealed class SameOriginEnforcementTests
{
    private const string SecFetchSiteHeader = "Sec-Fetch-Site";

    [Fact]
    public async Task A_same_site_different_port_request_is_rejected()
    {
        // The exact case SameSite=Strict lets through and this middleware exists to close:
        // http://localhost:3000 posting to http://localhost:8080 reports "same-site", not
        // "same-origin", because SameSite does not consider the port part of the site.
        await using var app = new RouteTimerApiFactory().WithRiderAuthentication();
        using var client = app.CreateClient();

        using var response = await SendWithSecFetchSiteAsync(client, "same-site");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("cross-site-request-rejected", body.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task A_true_cross_site_request_is_rejected()
    {
        await using var app = new RouteTimerApiFactory().WithRiderAuthentication();
        using var client = app.CreateClient();

        using var response = await SendWithSecFetchSiteAsync(client, "cross-site");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_same_origin_request_passes_through_unchanged()
    {
        // This is what the same-origin WASM client sends, and it needs zero client-side changes.
        await using var app = new RouteTimerApiFactory().WithRiderAuthentication();
        using var client = app.CreateClient();

        using var response = await SendWithSecFetchSiteAsync(client, "same-origin");

        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_request_with_no_sec_fetch_site_header_passes_through_unchanged()
    {
        // Non-browser clients (curl, server-to-server calls, this very test suite's HttpClient
        // under every other test) never send this header. They must not be broken by this check.
        await using var app = new RouteTimerApiFactory().WithRiderAuthentication();
        using var client = app.CreateClient();

        using var response = await client.PutAsJsonAsync(
            "/api/profile",
            new { riderWeightKg = 75, bikeAndEquipmentWeightKg = 10 },
            CancellationToken.None);

        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_get_request_is_exempt_even_when_reported_cross_site()
    {
        await using var app = new RouteTimerApiFactory().WithRiderAuthentication();
        using var client = app.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/profile");
        request.Headers.Add(SecFetchSiteHeader, "cross-site");

        using var response = await client.SendAsync(request, CancellationToken.None);

        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_head_request_is_exempt_even_when_reported_cross_site()
    {
        // HEAD is a safe method, same reasoning as GET.
        await using var app = new RouteTimerApiFactory().WithRiderAuthentication();
        using var client = app.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Head, "/api/profile");
        request.Headers.Add(SecFetchSiteHeader, "cross-site");

        using var response = await client.SendAsync(request, CancellationToken.None);

        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task An_options_request_is_exempt_even_when_reported_cross_site()
    {
        // The load-bearing reason for this one: a CORS preflight is an OPTIONS request and carries
        // Sec-Fetch-Site too. If this middleware rejected it, CORS would silently stop working the
        // moment anyone configured it -- and the failure would show up as an opaque preflight error
        // with nothing pointing back here. OPTIONS itself never carries out a mutation; the browser
        // only sends the real PUT/POST/DELETE after a successful preflight.
        await using var app = new RouteTimerApiFactory().WithRiderAuthentication();
        using var client = app.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Options, "/api/profile");
        request.Headers.Add(SecFetchSiteHeader, "cross-site");

        using var response = await client.SendAsync(request, CancellationToken.None);

        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private static async Task<HttpResponseMessage> SendWithSecFetchSiteAsync(HttpClient client, string secFetchSite)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, "/api/profile")
        {
            Content = JsonContent.Create(new { riderWeightKg = 75, bikeAndEquipmentWeightKg = 10 })
        };
        request.Headers.Add(SecFetchSiteHeader, secFetchSite);
        return await client.SendAsync(request, CancellationToken.None);
    }
}
