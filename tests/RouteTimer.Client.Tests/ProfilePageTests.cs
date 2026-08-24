using Bunit;
using Microsoft.Extensions.DependencyInjection;
using RouteTimer.Client.Pages;

namespace RouteTimer.Client.Tests;

public sealed class ProfilePageTests : BunitContext
{
    public ProfilePageTests() => Services.AddSingleton(new HttpClient(new NotFoundHandler()) { BaseAddress = new Uri("https://example.test/") });

    [Fact]
    public void Profile_shows_rider_and_bike_weight_inputs()
    {
        var cut = Render<Profile>();

        Assert.Equal(2, cut.FindAll("input[type=number]").Count);
    }

    private sealed class NotFoundHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
    }
}
