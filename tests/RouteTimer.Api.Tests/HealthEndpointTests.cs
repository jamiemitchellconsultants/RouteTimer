using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RouteTimer.Persistence;
using RouteTimer.Domain.Jobs;
using RouteTimer.Services.Activities;
using RouteTimer.Services.Jobs;
using RouteTimer.Services.Models;
using RouteTimer.Services.Physics;
using RouteTimer.Services.Predictions;

namespace RouteTimer.Api.Tests;

public sealed class HealthEndpointTests
{
    [Fact]
    public async Task Live_health_is_anonymous_and_returns_healthy()
    {
        await using var app = new KeycloakModeApplicationFactory();
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

    [Fact]
    public void Application_services_resolve_the_complete_build_model_handler_graph()
    {
        using var app = new KeycloakModeApplicationFactory();
        using var scope = app.Services.CreateScope();

        Assert.IsType<TrainingGeometryEnricher>(scope.ServiceProvider.GetRequiredService<ITrainingGeometryEnricher>());
        Assert.IsType<PhysicsCalibrator>(scope.ServiceProvider.GetRequiredService<IPhysicsCalibrator>());
        Assert.IsType<DescentLimitBuilder>(scope.ServiceProvider.GetRequiredService<IDescentLimitBuilder>());
        Assert.IsType<DescentSpeedLimiter>(scope.ServiceProvider.GetRequiredService<IDescentSpeedLimiter>());
        var handler = scope.ServiceProvider.GetServices<IJobHandler>()
            .Single(candidate => candidate.Handles == JobType.BuildModel);

        Assert.IsType<BuildModelJobHandler>(handler);
    }

    private class KeycloakModeApplicationFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting(RouteTimer.Api.Auth.AuthModeResolver.ConfigurationKey, "Keycloak");
            // Keycloak mode refuses to start without an authority.
            builder.UseSetting("Keycloak:Authority", RouteTimerApiFactory.DefaultKeycloakAuthority);
        }
    }

    private sealed class ReadyHealthApplicationFactory : KeycloakModeApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<RouteTimerDbContext>>();
                services.RemoveAll<IDbContextOptionsConfiguration<RouteTimerDbContext>>();
                services.AddDbContext<RouteTimerDbContext>(options => options.UseInMemoryDatabase(Guid.NewGuid().ToString()));
            });
        }
    }
}
