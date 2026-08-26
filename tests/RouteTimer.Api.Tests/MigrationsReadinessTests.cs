using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RouteTimer.Api.Health;
using RouteTimer.Persistence;

namespace RouteTimer.Api.Tests;

public sealed class MigrationsReadinessTests
{
    [Fact]
    public async Task Ready_is_unhealthy_while_migrations_are_still_pending()
    {
        await using var app = new MigrationReadinessApplicationFactory(migrationsRequired: true, completed: false);
        using var client = app.CreateClient();

        using var response = await client.GetAsync("/health/ready", CancellationToken.None);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task Ready_is_healthy_once_migrations_have_completed()
    {
        await using var app = new MigrationReadinessApplicationFactory(migrationsRequired: true, completed: true);
        using var client = app.CreateClient();

        using var response = await client.GetAsync("/health/ready", CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Ready_is_healthy_when_this_deployment_does_not_apply_migrations()
    {
        await using var app = new MigrationReadinessApplicationFactory(migrationsRequired: false, completed: false);
        using var client = app.CreateClient();

        using var response = await client.GetAsync("/health/ready", CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private sealed class MigrationReadinessApplicationFactory(bool migrationsRequired, bool completed)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting(RouteTimer.Api.Auth.AuthModeResolver.ConfigurationKey, "Keycloak");
            // Keycloak mode refuses to start without an authority.
            builder.UseSetting("Keycloak:Authority", RouteTimerApiFactory.DefaultKeycloakAuthority);
            builder.UseSetting("Database:ApplyMigrations", migrationsRequired ? "true" : "false");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<RouteTimerDbContext>>();
                services.RemoveAll<IDbContextOptionsConfiguration<RouteTimerDbContext>>();
                services.AddDbContext<RouteTimerDbContext>(options => options.UseInMemoryDatabase(Guid.NewGuid().ToString()));
                services.RemoveAll<Microsoft.Extensions.Hosting.IHostedService>();

                services.RemoveAll<MigrationState>();
                var state = new MigrationState(migrationsRequired);
                if (completed)
                {
                    state.MarkCompleted();
                }

                services.AddSingleton(state);
            });
        }
    }
}
