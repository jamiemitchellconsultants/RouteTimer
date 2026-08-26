using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RouteTimer.Contracts.Errors;
using RouteTimer.Contracts.Routes;
using RouteTimer.Services.Routes;

namespace RouteTimer.Api.Tests.Endpoints;

public sealed class RouteEndpointsTests
{
    [Theory]
    [InlineData("abc")]
    [InlineData("has space")]
    public async Task Rejects_a_code_that_does_not_match_the_permitted_shape(string code)
    {
        await using var app = CreateApp(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Found)));
        using var client = app.CreateClient();

        using var response = await client.GetAsync($"/api/routes/short-links/{Uri.EscapeDataString(code)}");
        using var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonDocument>();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ErrorCodes.ShortLinkCodeInvalid, body!.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Returns_the_resolved_url_for_a_stubbed_redirect()
    {
        var handler = new StubHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Found);
            response.Headers.Location = new Uri("https://www.google.com/maps/dir/A/B");
            return response;
        });
        await using var app = CreateApp(handler);
        using var client = app.CreateClient();

        using var response = await client.GetAsync("/api/routes/short-links/abcd1234");
        var body = await response.Content.ReadFromJsonAsync<ShortLinkResponse>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("https://www.google.com/maps/dir/A/B", body!.ResolvedUrl);
    }

    [Fact]
    public async Task Returns_a_bad_gateway_problem_when_the_upstream_does_not_redirect()
    {
        await using var app = CreateApp(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));
        using var client = app.CreateClient();

        using var response = await client.GetAsync("/api/routes/short-links/abcd1234");
        using var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonDocument>();

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal(ErrorCodes.ShortLinkUnresolved, body!.RootElement.GetProperty("code").GetString());
    }

    private static RouteTimerApiFactory CreateApp(StubHandler handler) =>
        new RouteTimerApiFactory().WithRiderAuthentication(services =>
        {
            services.RemoveAll<ShortLinkResolutionService>();
            services.AddSingleton(new ShortLinkResolutionService(
                new HttpClient(handler) { BaseAddress = new Uri("https://maps.app.goo.gl") }));
        });

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
