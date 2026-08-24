namespace RouteTimer.Persistence.Entities;

public sealed class PowerBandEntity
{
    public Guid ModelId { get; set; }
    public required string GradeKey { get; set; }
    public required string DurationKey { get; set; }
    public double TypicalWatts { get; set; }
    public double EvidenceSeconds { get; set; }
    public int ActivityCount { get; set; }
    public double ShrinkageWeight { get; set; }
    public required string Confidence { get; set; }
}
