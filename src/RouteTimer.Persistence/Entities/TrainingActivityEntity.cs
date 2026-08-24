namespace RouteTimer.Persistence.Entities;

public sealed class TrainingActivityEntity
{
    public Guid Id { get; set; }
    public Guid UploadId { get; set; }
    public required string Name { get; set; }
    public double MovingDurationSeconds { get; set; }
    public required string Eligibility { get; set; }
    public double PositionCoverage { get; set; }
    public double ElevationCoverage { get; set; }
    public double SpeedCoverage { get; set; }
    public double PowerCoverage { get; set; }
    public required IReadOnlyDictionary<string, int> ExclusionCounts { get; set; }
    public required IReadOnlyList<string> ReasonCodes { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public List<ActivitySampleEntity> Samples { get; set; } = [];
}
