namespace RouteTimer.Services.Routes;

public interface IGpxRouteParser
{
    Task<ParsedGpxRoute> ParseAsync(Stream input, CancellationToken cancellationToken);
}
