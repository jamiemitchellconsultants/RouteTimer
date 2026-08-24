namespace RouteTimer.Persistence.Entities;

public sealed class PredictionEntity
{
    public Guid Id { get; set; }
    public required string ModelVersion { get; set; }
    public double RiderWeightKg { get; set; }
    public double BikeWeightKg { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
