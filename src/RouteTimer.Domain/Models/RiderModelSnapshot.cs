using RouteTimer.Domain.Profile;

namespace RouteTimer.Domain.Models;

public sealed record RiderModelSnapshot(
    Guid Id,
    DateTimeOffset CreatedAt,
    RiderProfile ProfileSnapshot,
    RiderModel Model,
    ModelValidationSummary Validation)
{
    public bool WasCalibrated => Model.WasCalibrated;
    public bool DescentWasLearned => Model.DescentLimits.WasLearned;
}
