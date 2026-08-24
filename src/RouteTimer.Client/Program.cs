using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.AspNetCore.Components.WebAssembly.Authentication;
using RouteTimer.Client;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddOidcAuthentication(options =>
{
    builder.Configuration.Bind("Keycloak", options.ProviderOptions);
    options.ProviderOptions.ResponseType = "code";
});
builder.Services.AddScoped(sp =>
{
    var handler = sp.GetRequiredService<AuthorizationMessageHandler>()
        .ConfigureHandler(authorizedUrls: [builder.HostEnvironment.BaseAddress]);
    return new HttpClient(handler) { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) };
});

await builder.Build().RunAsync();
