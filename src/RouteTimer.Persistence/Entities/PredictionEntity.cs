namespace RouteTimer.Persistence.Entities;

public sealed class PredictionEntity
{
    public Guid Id { get; set; }
    public Guid UploadId { get; set; }
    public Guid RiderModelId { get; set; }
    public required string ModelVersion { get; set; }
    public double RiderWeightKg { get; set; }
    public double BikeWeightKg { get; set; }
    public bool ModelWasCalibrated { get; set; }
    public string ModelValidationStatus { get; set; } = string.Empty;
    public double? ModelValidationMedianApe { get; set; }
    public double? ModelValidationP90Ape { get; set; }
    public string AssumptionSurface { get; set; } = string.Empty;
    public string AssumptionWind { get; set; } = string.Empty;
    public string AssumptionWeather { get; set; } = string.Empty;
    public bool AssumptionMovingOnly { get; set; }
    public string State { get; set; } = string.Empty;
    public double? DistanceMetres { get; set; }
    public double? AscentMetres { get; set; }
    public double? MovingSeconds { get; set; }
    public double? AverageSpeedMetresPerSecond { get; set; }
    public double? AveragePowerWatts { get; set; }
    public string? Confidence { get; set; }
    public List<string> Warnings { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    public StoredUploadEntity? Upload { get; set; }
    public RiderModelEntity? RiderModel { get; set; }
    public List<PredictionSegmentEntity> Segments { get; set; } = [];
}
