namespace RouteTimer.Persistence.Entities;

public sealed class PredictionSegmentEntity
{
    public Guid PredictionId { get; set; }
    public int Sequence { get; set; }
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public double ElevationMetres { get; set; }
    public double CumulativeDistanceMetres { get; set; }
    public double SegmentDistanceMetres { get; set; }
    public double Gradient { get; set; }
    public double CurvaturePerMetre { get; set; }
    public double PredictedPowerWatts { get; set; }
    public double PredictedSpeedMetresPerSecond { get; set; }
    public double SegmentMovingSeconds { get; set; }
    public double CumulativeMovingSeconds { get; set; }
    public required string Confidence { get; set; }
}
