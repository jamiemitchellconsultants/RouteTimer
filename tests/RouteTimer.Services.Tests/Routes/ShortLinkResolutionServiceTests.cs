using System.Net;
using RouteTimer.Services.Routes;

namespace RouteTimer.Services.Tests.Routes;

public sealed class ShortLinkResolutionServiceTests
{
    [Theory]
    [InlineData(HttpStatusCode.Moved)]
    [InlineData(HttpStatusCode.Found)]
    [InlineData(HttpStatusCode.SeeOther)]
    [InlineData(HttpStatusCode.TemporaryRedirect)]
    [InlineData(HttpStatusCode.PermanentRedirect)]
    public async Task Returns_the_location_of_a_redirect(HttpStatusCode status)
    {
        var handler = new StubHandler(_ =>
        {
            var response = new HttpResponseMessage(status);
            response.Headers.Location = new Uri("https://www.google.com/maps/dir/A/B");
            return response;
        });
        var service = CreateService(handler);

        var resolved = await service.ResolveAsync("abcd1234", CancellationToken.None);

        Assert.Equal("https://www.google.com/maps/dir/A/B", resolved);
        Assert.Equal("/abcd1234", handler.LastRequest!.RequestUri!.AbsolutePath);
        Assert.Equal("RouteTimer/1.0", handler.LastRequest.Headers.UserAgent.ToString());
        Assert.False(handler.LastRequest.Headers.Contains("Cookie"));
        Assert.Null(handler.LastRequest.Headers.Referrer);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("has space")]
    [InlineData("has/slash")]
    [InlineData("")]
    public async Task Rejects_a_code_that_does_not_match_the_permitted_shape(string code)
    {
        var service = CreateService(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Found)));

        await Assert.ThrowsAsync<ShortLinkCodeInvalidException>(
            () => service.ResolveAsync(code, CancellationToken.None));
    }

    [Fact]
    public async Task Fails_when_the_upstream_does_not_redirect()
    {
        var service = CreateService(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));

        await Assert.ThrowsAsync<ShortLinkUnresolvedException>(
            () => service.ResolveAsync("abcd1234", CancellationToken.None));
    }

    [Fact]
    public async Task Fails_when_a_redirect_carries_no_location()
    {
        var service = CreateService(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Found)));

        await Assert.ThrowsAsync<ShortLinkUnresolvedException>(
            () => service.ResolveAsync("abcd1234", CancellationToken.None));
    }

    private static ShortLinkResolutionService CreateService(StubHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://maps.app.goo.gl") });

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(respond(request));
        }
    }
}
