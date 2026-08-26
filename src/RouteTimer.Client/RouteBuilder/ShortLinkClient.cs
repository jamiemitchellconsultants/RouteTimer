using RouteTimer.Client.Api;
using RouteTimer.Client.Logging;

namespace RouteTimer.Client.RouteBuilder;

public sealed class ShortLinkClient(IRouteTimerApiClient api, ActionLog log)
{
    private const string ManualWorkAround =
        "Open the short link in a browser tab, copy the full www.google.com/maps URL it lands on, " +
        "and paste that into the same URL box.";

    public async Task<string?> ResolveAsync(string code, CancellationToken cancellationToken)
    {
        log.Info(
            $"Expanding short link '{code}' through RouteTimer's own API.",
            "The browser cannot fetch maps.app.goo.gl directly. Only the code is sent, never the API key.");

        try
        {
            var response = await api.ResolveShortLinkAsync(code, cancellationToken);
            log.Success("Short link resolved.", response.ResolvedUrl);
            return response.ResolvedUrl;
        }
        catch (ApiProblemException problem)
        {
            log.Warn($"RouteTimer could not expand the short link: {problem.Message}", ManualWorkAround);
            return null;
        }
        catch (HttpRequestException exception)
        {
            log.Warn($"Could not reach RouteTimer to expand the short link: {exception.Message}", ManualWorkAround);
            return null;
        }
    }
}
