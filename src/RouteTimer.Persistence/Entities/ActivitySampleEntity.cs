namespace RouteTimer.Persistence.Entities;

public sealed class ActivitySampleEntity
{
    public Guid ActivityId { get; set; }
    public int Sequence { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public double MovingElapsedSeconds { get; set; }
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public double ElevationMetres { get; set; }
    public double SpeedMetresPerSecond { get; set; }
    public ushort? PowerWatts { get; set; }
    public byte? HeartRate { get; set; }
    public byte? Cadence { get; set; }
    public bool CrossesDiscontinuity { get; set; }
    public double Gradient { get; set; }
    public double CurvaturePerMetre { get; set; }
}
