using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.AspNetCore.Components.WebAssembly.Authentication;
using Microsoft.Extensions.DependencyInjection;
using RouteTimer.Client.Api;
using RouteTimer.Client.Auth;
using RouteTimer.Client.Jobs;
using RouteTimer.Client;
using RouteTimer.Contracts.Auth;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// The deployment decides how it authenticates, so the client cannot know at build time. Fetch the
// configuration first; one published image then serves every deployment.
var authConfig = await FetchAuthConfigAsync(builder.HostEnvironment.BaseAddress);
builder.Services.AddSingleton(new ClientAuthConfig(authConfig));

if (string.Equals(authConfig.Mode, AuthConfigResponse.LocalMode, StringComparison.OrdinalIgnoreCase))
{
    // The browser attaches the session cookie to same-origin requests on its own, so there is no
    // bearer handler in this mode.
    builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });
    builder.Services.AddScoped<LocalAuthenticationStateProvider>(sp => new LocalAuthenticationStateProvider(
        // A dedicated client with a short timeout, not the one RouteTimerApiClient shares for
        // uploads up to ~500 MB -- a session check that hangs must fail fast, but a large legitimate
        // upload must not be cut off at the same threshold.
        new HttpClient
        {
            BaseAddress = new Uri(builder.HostEnvironment.BaseAddress),
            Timeout = TimeSpan.FromSeconds(10)
        }));
    builder.Services.AddScoped<AuthenticationStateProvider>(sp => sp.GetRequiredService<LocalAuthenticationStateProvider>());
    builder.Services.AddAuthorizationCore();
}
else
{
    builder.Services.AddOidcAuthentication(options =>
    {
        options.ProviderOptions.Authority = authConfig.Authority;
        options.ProviderOptions.ClientId = authConfig.ClientId;
        options.ProviderOptions.RedirectUri = authConfig.RedirectUri;
        options.ProviderOptions.PostLogoutRedirectUri = authConfig.PostLogoutRedirectUri;
        options.ProviderOptions.ResponseType = "code";
    });
    builder.Services.AddScoped(sp =>
    {
        var handler = sp.GetRequiredService<AuthorizationMessageHandler>()
            .ConfigureHandler(authorizedUrls: [builder.HostEnvironment.BaseAddress]);
        return new HttpClient(handler) { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) };
    });
}

builder.Services.AddScoped<IRouteTimerApiClient>(sp => new RouteTimerApiClient(sp.GetRequiredService<HttpClient>()));
builder.Services.AddScoped<JobPoller>();
builder.Services.AddSingleton(TimeProvider.System);

await builder.Build().RunAsync();

// The most likely failure here is routine, not exotic: a page load during the post-deploy migration
// window hits the API before it reports ready and gets a 500. Retry with backoff before giving up --
// a bounded, back-off retry rather than an aggressive one, since Keycloak-mode deployments get no
// rate limiter on this endpoint at all (the shared ingress owns that instead) and every open tab
// hitting this on a real outage should not hammer it. On final failure, throw with enough detail that
// the framework's own #blazor-error-ui and the browser console both say what actually happened,
// rather than the empty-body case this used to be the only thing distinguished.
static async Task<AuthConfigResponse> FetchAuthConfigAsync(string baseAddress)
{
    using var client = new HttpClient
    {
        BaseAddress = new Uri(baseAddress),
        Timeout = TimeSpan.FromSeconds(10)
    };

    Exception? lastFailure = null;
    int[] retryDelaysMs = [500, 1000, 2000];
    for (var attempt = 0; attempt <= retryDelaysMs.Length; attempt++)
    {
        try
        {
            return await client.GetFromJsonAsync<AuthConfigResponse>("api/auth/config")
                ?? throw new InvalidOperationException("The API returned an empty authentication configuration.");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            lastFailure = ex;
            if (attempt < retryDelaysMs.Length)
            {
                await Task.Delay(retryDelaysMs[attempt]);
            }
        }
    }

    throw new InvalidOperationException(
        "RouteTimer could not reach its API to read the authentication configuration. " +
        "The container may still be starting up; reloading the page usually resolves this.",
        lastFailure);
}
