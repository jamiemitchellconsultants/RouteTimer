using System.Net.Http.Json;
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
using var bootstrapClient = new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) };
var authConfig = await bootstrapClient.GetFromJsonAsync<AuthConfigResponse>("api/auth/config")
    ?? throw new InvalidOperationException("The API did not return an authentication configuration.");
builder.Services.AddSingleton(new ClientAuthConfig(authConfig));

if (string.Equals(authConfig.Mode, AuthConfigResponse.LocalMode, StringComparison.OrdinalIgnoreCase))
{
    // The browser attaches the session cookie to same-origin requests on its own, so there is no
    // bearer handler in this mode.
    builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });
    builder.Services.AddScoped<LocalAuthenticationStateProvider>();
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
