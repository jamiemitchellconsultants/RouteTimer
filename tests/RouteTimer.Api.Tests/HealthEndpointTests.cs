using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RouteTimer.Persistence;

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

    [Fact]
    public async Task Ready_health_is_anonymous_when_the_database_is_available()
    {
        await using var app = new ReadyHealthApplicationFactory();
        using var client = app.CreateClient();

        using var response = await client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private sealed class ReadyHealthApplicationFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<RouteTimerDbContext>>();
                services.RemoveAll<IDbContextOptionsConfiguration<RouteTimerDbContext>>();
                services.AddDbContext<RouteTimerDbContext>(options => options.UseInMemoryDatabase(Guid.NewGuid().ToString()));
            });
        }
    }
}
