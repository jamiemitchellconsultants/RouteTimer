namespace RouteTimer.Persistence.Entities;

public sealed class RiderModelDescentLimitEntity
{
    public Guid ModelId { get; set; }
    public string GradeKey { get; set; } = "";
    public string CurvatureKey { get; set; } = "";
    public double SpeedCapMetresPerSecond { get; set; }
    public double EvidenceSeconds { get; set; }
    public int ActivityCount { get; set; }
    public string Confidence { get; set; } = "Low";
    public bool IsFallback { get; set; }
    public RiderModelEntity Model { get; set; } = null!;
}
