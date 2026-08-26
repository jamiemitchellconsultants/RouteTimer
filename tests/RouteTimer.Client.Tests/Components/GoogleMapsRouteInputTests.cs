using Bunit;
using Microsoft.Extensions.DependencyInjection;
using RouteTimer.Client.Api;
using RouteTimer.Client.Components;
using RouteTimer.Client.Logging;
using RouteTimer.Client.RouteBuilder;
using RouteTimer.Client.Tests.Fakes;
using RouteTimer.Contracts.Settings;

namespace RouteTimer.Client.Tests.Components;

public sealed class GoogleMapsRouteInputTests : BunitContext
{
    private readonly FakeRouteTimerApiClient api = new();

    public GoogleMapsRouteInputTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton<IRouteTimerApiClient>(api);
        Services.AddSingleton(TimeProvider.System);

        var log = new ActionLog();
        Services.AddSingleton(log);
        // Registered as pre-built instances, not via AddScoped<T>: these types are
        // IAsyncDisposable only, and bUnit's synchronous test teardown cannot await disposal of a
        // container-owned IAsyncDisposable. An externally-supplied instance is never disposed by
        // the container, which sidesteps the crash -- these tests never exercise JS interop anyway.
        Services.AddSingleton(new DirectionsInterop(JSInterop.JSRuntime, log));
        Services.AddSingleton(new BrowserInterop(JSInterop.JSRuntime));
        Services.AddScoped<ShortLinkClient>();
    }

    [Fact]
    public void Shows_the_saved_key_hint_and_offers_to_replace_it()
    {
        api.OnGetGoogleMapsKeyStatusAsync = _ =>
            Task.FromResult(new GoogleMapsKeyStatusResponse(true, "AIza…6789", true));

        var cut = Render<GoogleMapsRouteInput>();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("AIza…6789", cut.Find("[data-testid=maps-key-status]").TextContent, StringComparison.Ordinal);
            cut.Find("[data-testid=maps-key-replace]");
            cut.Find("[data-testid=maps-key-delete]");
        });
    }

    [Fact]
    public void Offers_no_save_option_when_key_storage_is_unavailable()
    {
        api.OnGetGoogleMapsKeyStatusAsync = _ =>
            Task.FromResult(new GoogleMapsKeyStatusResponse(false, null, false));

        var cut = Render<GoogleMapsRouteInput>();

        cut.WaitForAssertion(() =>
        {
            Assert.Empty(cut.FindAll("[data-testid=maps-key-save]"));
            Assert.Contains(
                "cannot be saved",
                cut.Find("[data-testid=maps-key-status]").TextContent,
                StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void States_plainly_what_saving_the_key_means()
    {
        api.OnGetGoogleMapsKeyStatusAsync = _ =>
            Task.FromResult(new GoogleMapsKeyStatusResponse(false, null, true));

        var cut = Render<GoogleMapsRouteInput>();

        cut.WaitForAssertion(() =>
        {
            var disclosure = cut.Find("[data-testid=maps-key-disclosure]").TextContent;
            Assert.Contains("encrypted", disclosure, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("server can decrypt", disclosure, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("anyone who can sign in", disclosure, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void Blocks_conversion_when_the_url_is_empty()
    {
        api.OnGetGoogleMapsKeyStatusAsync = _ =>
            Task.FromResult(new GoogleMapsKeyStatusResponse(true, "AIza…6789", true));

        var cut = Render<GoogleMapsRouteInput>();

        cut.WaitForAssertion(() => Assert.True(cut.Find("[data-testid=maps-convert]").HasAttribute("disabled")));
    }
}
