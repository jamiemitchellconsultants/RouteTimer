namespace RouteTimer.Persistence.Entities;

/// <summary>
/// Adjusted per-segment values only. Geometry is not copied here - it's joined at query time from
/// the immutable baseline's <see cref="PredictionSegmentEntity"/> with the same sequence.
/// </summary>
public sealed class PredictionAdjustmentSegmentEntity
{
    public Guid AdjustmentId { get; set; }
    public int Sequence { get; set; }
    public double PowerWatts { get; set; }
    public double SpeedMetresPerSecond { get; set; }
    public double SegmentMovingSeconds { get; set; }
    public double CumulativeMovingSeconds { get; set; }
    public required string Confidence { get; set; }
    public int? ZoneNumber { get; set; }
    public string? StrategyPhase { get; set; }
    public double? WPrimeBalanceJoules { get; set; }
}
