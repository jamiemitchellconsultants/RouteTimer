namespace RouteTimer.Persistence.Entities;

public sealed class PredictionAdjustmentEntity
{
    public Guid Id { get; set; }
    public Guid PredictionId { get; set; }
    public required string StrategyType { get; set; }
    public required string StrategyJson { get; set; }
    public required string StrategyAlgorithmVersion { get; set; }
    public string State { get; set; } = string.Empty;
    public double? MovingSeconds { get; set; }
    public double? AverageSpeedMetresPerSecond { get; set; }
    public double? AveragePowerWatts { get; set; }
    public string? Confidence { get; set; }
    public List<string> Warnings { get; set; } = [];
    public string? ResultJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    public List<PredictionAdjustmentSegmentEntity> Segments { get; set; } = [];
}
