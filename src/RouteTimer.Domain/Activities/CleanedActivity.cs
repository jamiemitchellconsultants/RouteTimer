namespace RouteTimer.Domain.Activities;

public sealed record CleanedActivity(
    string Name,
    IReadOnlyList<CleanRideSample> Samples,
    TimeSpan MovingDuration,
    ActivityQuality Quality,
    TrainingActivityMetadata Metadata);
