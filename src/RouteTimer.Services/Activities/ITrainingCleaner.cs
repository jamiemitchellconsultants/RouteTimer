using RouteTimer.Domain.Activities;

namespace RouteTimer.Services.Activities;

public interface ITrainingCleaner
{
    CleanedActivity Clean(ParsedFitActivity activity);
}
