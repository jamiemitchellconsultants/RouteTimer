using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RouteTimer.Persistence;

namespace RouteTimer.Api.Tests;

public sealed class RouteTimerApiFactory(bool authenticateAsRider = false, Action<IServiceCollection>? configureServices = null)
    : WebApplicationFactory<Program>
{
    private readonly string databaseName = Guid.NewGuid().ToString();

    public RouteTimerApiFactory WithRiderAuthentication(Action<IServiceCollection>? configure = null) =>
        new(true, Combine(configureServices, configure));

    public RouteTimerApiFactory WithServices(Action<IServiceCollection> configure) =>
        new(authenticateAsRider, Combine(configureServices, configure));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["GarminAdapter:BaseUrl"] = "http://garmin-adapter.invalid/"
            }));
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<DbContextOptions<RouteTimerDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<RouteTimerDbContext>>();
            services.AddDbContext<RouteTimerDbContext>(options => options.UseInMemoryDatabase(databaseName));
            services.RemoveAll<Microsoft.Extensions.Hosting.IHostedService>();

            if (authenticateAsRider)
            {
                services.AddAuthentication("test")
                    .AddScheme<AuthenticationSchemeOptions, RiderAuthenticationHandler>("test", _ => { });
            }

            configureServices?.Invoke(services);
        });
    }

    private static Action<IServiceCollection>? Combine(
        Action<IServiceCollection>? first,
        Action<IServiceCollection>? second)
    {
        if (first is null)
        {
            return second;
        }

        if (second is null)
        {
            return first;
        }

        return services =>
        {
            first(services);
            second(services);
        };
    }

    private sealed class RiderAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var claims = new List<Claim> { new(ClaimTypes.Name, "rider") };
            if (Request.Headers["X-Test-Role"] != "non-rider")
            {
                claims.Add(new Claim(ClaimTypes.Role, "rider"));
            }

            var identity = new ClaimsIdentity(claims, Scheme.Name);
            var principal = new ClaimsPrincipal(identity);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
        }
    }
}
