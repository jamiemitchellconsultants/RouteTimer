using Microsoft.JSInterop;

namespace RouteTimer.Client.RouteBuilder;

public sealed class BrowserInterop(IJSRuntime js) : IAsyncDisposable
{
    private IJSObjectReference? _module;

    private async Task<IJSObjectReference> ModuleAsync() =>
        _module ??= await js.InvokeAsync<IJSObjectReference>("import", "./js/browser.js");

    public async Task ReloadAsync() =>
        await (await ModuleAsync()).InvokeVoidAsync("reload");

    public async Task<string> OriginAsync() =>
        await (await ModuleAsync()).InvokeAsync<string>("origin");

    public async Task CopyToClipboardAsync(string text) =>
        await (await ModuleAsync()).InvokeVoidAsync("copyToClipboard", text);

    public async ValueTask DisposeAsync()
    {
        if (_module is not null) await _module.DisposeAsync();
    }
}
