namespace RouteTimer.Persistence.Entities;

public sealed class GarminActivityImportEntity
{
    public string GarminActivityId { get; set; } = "";
    public Guid UploadId { get; set; }
    public string ActivityName { get; set; } = "";
    public DateTimeOffset LinkedAt { get; set; }
}
