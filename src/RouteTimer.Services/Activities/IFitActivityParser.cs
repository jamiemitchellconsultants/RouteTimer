namespace RouteTimer.Services.Activities;

public interface IFitActivityParser
{
    Task<ParsedFitActivity> ParseAsync(Stream input, CancellationToken cancellationToken);
}
