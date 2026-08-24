namespace RouteTimer.Persistence.Entities;

public sealed class RiderModelEntity
{
    public Guid Id { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public double ProfileRiderWeightKg { get; set; }
    public double ProfileBikeWeightKg { get; set; }
    public required string AlgorithmVersion { get; set; }
    public double DrivetrainEfficiency { get; set; }
    public double AirDensity { get; set; }
    public double Crr { get; set; }
    public double CdA { get; set; }
    public bool WasCalibrated { get; set; }
    public double GlobalTypicalWatts { get; set; }
    public required string ValidationStatus { get; set; }
    public double? ValidationMedianApe { get; set; }
    public double? ValidationP90Ape { get; set; }

    public List<PowerBandEntity> Bands { get; set; } = [];
}
