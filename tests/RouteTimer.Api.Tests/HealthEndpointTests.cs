using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace RouteTimer.Api.Tests;

public sealed class HealthEndpointTests
{
    [Fact]
    public async Task Live_health_is_anonymous_and_returns_healthy()
    {
        await using var app = new WebApplicationFactory<Program>();
        using var client = app.CreateClient();

        using var response = await client.GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
    }
}
