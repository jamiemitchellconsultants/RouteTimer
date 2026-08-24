namespace RouteTimer.Persistence.Entities;

public sealed class RiderProfileEntity
{
    public int Id { get; set; }
    public double RiderWeightKg { get; set; }
    public double BikeAndEquipmentWeightKg { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
