using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace RouteTimer.Client.RoutePacer;

public sealed class PaceTrackerQrInterop(IJSRuntime js) : IAsyncDisposable
{
    private IJSObjectReference? module;

    private async Task<IJSObjectReference> ModuleAsync() =>
        module ??= await js.InvokeAsync<IJSObjectReference>("import", "./js/pace-tracker-qr.mjs");

    /// <summary>
    /// Renders the handoff link as an SVG QR code inside <paramref name="element"/>. The expected
    /// origin, current time, and expiry are passed explicitly so the module can refuse a link that
    /// does not match, independently of whatever the API returned.
    /// </summary>
    public async Task RenderAsync(
        ElementReference element,
        string url,
        string expectedOrigin,
        DateTimeOffset now,
        DateTimeOffset expiresAt) =>
        await (await ModuleAsync()).InvokeVoidAsync(
            "render",
            element,
            url,
            expectedOrigin,
            now.ToUniversalTime().ToString("O"),
            expiresAt.ToUniversalTime().ToString("O"));

    public async Task ClearAsync(ElementReference element) =>
        await (await ModuleAsync()).InvokeVoidAsync("clear", element);

    public async ValueTask DisposeAsync()
    {
        if (module is not null)
        {
            await module.DisposeAsync();
        }
    }
}
