using System.Net;
using System.Net.Http.Json;
using RouteTimer.Client.Api;
using RouteTimer.Contracts.Settings;

namespace RouteTimer.Client.Tests.Api;

public sealed class GoogleMapsKeyClientTests
{
    [Fact]
    public async Task Reveals_the_google_maps_key_with_a_post()
    {
        var handler = new StubHandler(request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/api/settings/google-maps-key/use", request.RequestUri!.AbsolutePath);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new GoogleMapsKeyResponse("AIzaSyExampleKeyValue0123456789"))
            };
        });
        var client = new RouteTimerApiClient(new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") });

        var response = await client.UseGoogleMapsKeyAsync(CancellationToken.None);

        Assert.Equal("AIzaSyExampleKeyValue0123456789", response.ApiKey);
    }

    [Fact]
    public async Task Saves_the_key_with_a_put()
    {
        var handler = new StubHandler(request =>
        {
            Assert.Equal(HttpMethod.Put, request.Method);
            Assert.Equal("/api/settings/google-maps-key", request.RequestUri!.AbsolutePath);
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });
        var client = new RouteTimerApiClient(new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") });

        await client.SaveGoogleMapsKeyAsync(new SaveGoogleMapsKeyRequest("AIzaSyExampleKeyValue0123456789"), CancellationToken.None);
    }

    [Fact]
    public async Task Deletes_the_key()
    {
        var handler = new StubHandler(request =>
        {
            Assert.Equal(HttpMethod.Delete, request.Method);
            Assert.Equal("/api/settings/google-maps-key", request.RequestUri!.AbsolutePath);
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });
        var client = new RouteTimerApiClient(new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") });

        await client.DeleteGoogleMapsKeyAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Gets_the_status()
    {
        var handler = new StubHandler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("/api/settings/google-maps-key", request.RequestUri!.AbsolutePath);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new GoogleMapsKeyStatusResponse(true, "AIza…6789", true))
            };
        });
        var client = new RouteTimerApiClient(new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") });

        var status = await client.GetGoogleMapsKeyStatusAsync(CancellationToken.None);

        Assert.True(status.Configured);
        Assert.Equal("AIza…6789", status.Hint);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
