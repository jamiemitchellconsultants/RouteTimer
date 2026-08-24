using RouteTimer.Domain.Profile;

namespace RouteTimer.Domain.Models;

public sealed record RiderModelSnapshot(Guid Id, DateTimeOffset CreatedAt, RiderProfile ProfileSnapshot, RiderModel Model, bool WasCalibrated, ModelValidationSummary Validation);
