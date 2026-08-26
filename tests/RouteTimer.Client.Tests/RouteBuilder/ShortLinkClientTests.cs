using RouteTimer.Client.Api;
using RouteTimer.Client.Logging;
using RouteTimer.Client.RouteBuilder;
using RouteTimer.Client.Tests.Fakes;
using RouteTimer.Contracts.Routes;

namespace RouteTimer.Client.Tests.RouteBuilder;

public sealed class ShortLinkClientTests
{
    [Fact]
    public async Task Returns_the_resolved_url_and_logs_it()
    {
        var api = new FakeRouteTimerApiClient
        {
            OnResolveShortLinkAsync = (code, _) =>
                Task.FromResult(new ShortLinkResponse($"https://www.google.com/maps/dir/{code}"))
        };
        var log = new ActionLog();
        var client = new ShortLinkClient(api, log);

        var resolved = await client.ResolveAsync("abcd1234", CancellationToken.None);

        Assert.Equal("https://www.google.com/maps/dir/abcd1234", resolved);
        Assert.Contains(log.Entries, entry => entry.Level == ActionLevel.Success);
    }

    [Fact]
    public async Task Returns_null_and_explains_the_manual_work_around_on_failure()
    {
        var api = new FakeRouteTimerApiClient
        {
            OnResolveShortLinkAsync = (_, _) => throw new HttpRequestException("boom")
        };
        var log = new ActionLog();
        var client = new ShortLinkClient(api, log);

        var resolved = await client.ResolveAsync("abcd1234", CancellationToken.None);

        Assert.Null(resolved);
        Assert.Contains(log.Entries, entry =>
            entry.Level == ActionLevel.Warn &&
            entry.Detail is not null &&
            entry.Detail.Contains("paste", StringComparison.OrdinalIgnoreCase));
    }
}
