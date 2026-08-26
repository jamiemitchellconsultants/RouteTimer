using System.Net;
using System.Net.Http.Json;
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
        var handler = new SequenceHandler([new AuthSessionResponse(false), new AuthSessionResponse(true)]);
        var provider = new LocalAuthenticationStateProvider(
            new HttpClient(handler) { BaseAddress = new Uri("https://localhost/") });

        var before = await provider.GetAuthenticationStateAsync();
        provider.NotifySessionChanged();
        var after = await provider.GetAuthenticationStateAsync();

        Assert.False(before.User.Identity?.IsAuthenticated);
        Assert.True(after.User.Identity?.IsAuthenticated);
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
}
