using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Components.Authorization;
using RouteTimer.Client.Auth;
using RouteTimer.Contracts.Auth;

namespace RouteTimer.Client.Tests.Auth;

public sealed class LocalAuthenticationStateProviderTests
{
    [Fact]
    public async Task Reports_an_anonymous_user_when_the_session_endpoint_says_so()
    {
        var provider = new LocalAuthenticationStateProvider(Client(new AuthSessionResponse(false)));

        var state = await provider.GetAuthenticationStateAsync();

        Assert.False(state.User.Identity?.IsAuthenticated);
    }

    [Fact]
    public async Task Reports_an_authenticated_rider_when_the_session_endpoint_says_so()
    {
        var provider = new LocalAuthenticationStateProvider(Client(new AuthSessionResponse(true)));

        var state = await provider.GetAuthenticationStateAsync();

        Assert.True(state.User.Identity?.IsAuthenticated);
        Assert.True(state.User.IsInRole("rider"));
    }

    [Fact]
    public async Task Reports_an_anonymous_user_when_the_session_endpoint_is_unreachable()
    {
        var provider = new LocalAuthenticationStateProvider(FailingClient());

        var state = await provider.GetAuthenticationStateAsync();

        Assert.False(state.User.Identity?.IsAuthenticated);
    }

    [Fact]
    public async Task Notifying_a_sign_in_refreshes_the_reported_state()
    {
        // Calling GetAuthenticationStateAsync a second time would pass even if NotifySessionChanged
        // did nothing at all -- SequenceHandler advances on every call regardless. What actually
        // matters is whether the framework's own change notification fires, since that is the signal
        // CascadingAuthenticationState and every AuthorizeView in the tree are subscribed to.
        var handler = new SequenceHandler([new AuthSessionResponse(false), new AuthSessionResponse(true)]);
        var provider = new LocalAuthenticationStateProvider(
            new HttpClient(handler) { BaseAddress = new Uri("https://localhost/") });
        await provider.GetAuthenticationStateAsync();

        Task<AuthenticationState>? raised = null;
        provider.AuthenticationStateChanged += task => raised = task;
        provider.NotifySessionChanged();

        Assert.NotNull(raised);
        var notified = await raised;
        Assert.True(notified.User.Identity?.IsAuthenticated);
    }

    [Fact]
    public async Task Reports_an_anonymous_user_when_the_session_endpoint_returns_malformed_json()
    {
        var provider = new LocalAuthenticationStateProvider(
            new HttpClient(new FixedResponseHandler("not json")) { BaseAddress = new Uri("https://localhost/") });

        var state = await provider.GetAuthenticationStateAsync();

        Assert.False(state.User.Identity?.IsAuthenticated);
    }

    [Fact]
    public async Task Reports_an_anonymous_user_when_the_session_check_times_out()
    {
        var provider = new LocalAuthenticationStateProvider(
            new HttpClient(new TimingOutHandler()) { BaseAddress = new Uri("https://localhost/"), Timeout = TimeSpan.FromMilliseconds(20) });

        var state = await provider.GetAuthenticationStateAsync();

        Assert.False(state.User.Identity?.IsAuthenticated);
    }

    private static HttpClient Client(AuthSessionResponse session) =>
        new(new SequenceHandler([session])) { BaseAddress = new Uri("https://localhost/") };

    private static HttpClient FailingClient() =>
        new(new FailingHandler()) { BaseAddress = new Uri("https://localhost/") };

    private sealed class SequenceHandler(IReadOnlyList<AuthSessionResponse> responses) : HttpMessageHandler
    {
        private int index;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var session = responses[Math.Min(index, responses.Count - 1)];
            index++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(session)
            });
        }
    }

    private sealed class FailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("unreachable");
    }

    private sealed class FixedResponseHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
    }

    private sealed class TimingOutHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("Unreachable.");
        }
    }
}
