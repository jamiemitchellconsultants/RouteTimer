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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RouteTimer.Persistence;

namespace RouteTimer.Api.Tests;

public sealed class RouteTimerApiFactory(
    bool authenticateAsRider = false,
    Action<IServiceCollection>? configureServices = null,
    string authMode = "Keycloak",
    IReadOnlyDictionary<string, string>? settings = null)
    : WebApplicationFactory<Program>
{
    internal const string DefaultKeycloakAuthority = "https://keycloak.test.invalid/realms/routetimer";
    private const string DefaultGarminTokenKey = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=";
    private const string DefaultGarminAdapterBaseUrl = "http://garmin-adapter.invalid/";
    private const string DefaultGoogleMapsKeyEncryptionKey = "Hx4dHBsaGRgXFhUUExIREA8ODQwLCgkIBwYFBAMCAQA=";

    private static readonly IReadOnlyDictionary<string, string> DefaultSettings =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // Keycloak mode refuses to start without an authority, so the default mode needs one.
            ["Keycloak:Authority"] = DefaultKeycloakAuthority,
            // Program refuses to build without a Garmin token key, and the adapter's typed
            // HttpClient refuses to be constructed without a base address, so every host that
            // boots Program needs both -- including the ones that never touch Garmin.
            ["Garmin:TokenEncryptionKey"] = DefaultGarminTokenKey,
            ["GarminAdapter:BaseUrl"] = DefaultGarminAdapterBaseUrl,
            // Unlike the Garmin key, Program tolerates this being absent -- key storage just
            // reports unavailable. Set by default so ordinary tests get working storage, and
            // unset via WithSetting(..., null) to exercise the unavailable path specifically.
            ["GoogleMaps:KeyEncryptionKey"] = DefaultGoogleMapsKeyEncryptionKey,
            // Disabled by default, exactly as tracked appsettings ship it: a test host that has
            // not deliberately opted in must never hold a signing key or an upload credential.
            ["RoutePacerHandoff:Enabled"] = "false"
        };

    /// <summary>
    /// Applies the settings without which <c>Program</c> refuses to start, for the test hosts
    /// that derive from <see cref="WebApplicationFactory{TEntryPoint}"/> directly rather than
    /// from this factory. Keeping that list in one place is why the values above are consts.
    /// </summary>
    internal static void ApplyRequiredSettings(IWebHostBuilder builder, string authMode = "Keycloak")
    {
        builder.UseSetting(RouteTimer.Api.Auth.AuthModeResolver.ConfigurationKey, authMode);
        foreach (var setting in DefaultSettings)
        {
            builder.UseSetting(setting.Key, setting.Value);
        }
    }

    private readonly string databaseName = Guid.NewGuid().ToString();

    public RouteTimerApiFactory WithRiderAuthentication(Action<IServiceCollection>? configure = null) =>
        new(true, Combine(configureServices, configure), authMode, settings);

    public RouteTimerApiFactory WithServices(Action<IServiceCollection> configure) =>
        new(authenticateAsRider, Combine(configureServices, configure), authMode, settings);

    public RouteTimerApiFactory WithAuthMode(string mode) =>
        new(authenticateAsRider, configureServices, mode, settings);

    /// <summary>Overrides one configuration value. Pass null to unset a default.</summary>
    public RouteTimerApiFactory WithSetting(string key, string? value)
    {
        var merged = new Dictionary<string, string>(settings ?? DefaultSettings, StringComparer.Ordinal);
        if (value is null)
        {
            merged.Remove(key);
        }
        else
        {
            merged[key] = value;
        }

        return new RouteTimerApiFactory(authenticateAsRider, configureServices, authMode, merged);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting(RouteTimer.Api.Auth.AuthModeResolver.ConfigurationKey, authMode);
        foreach (var setting in settings ?? DefaultSettings)
        {
            builder.UseSetting(setting.Key, setting.Value);
        }

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
