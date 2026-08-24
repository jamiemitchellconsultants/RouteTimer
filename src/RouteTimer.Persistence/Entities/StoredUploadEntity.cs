namespace RouteTimer.Persistence.Entities;

public sealed class StoredUploadEntity
{
    public Guid Id { get; set; }
    public required string Kind { get; set; }
    public required string FileName { get; set; }
    public required byte[] Content { get; set; }
    public required byte[] Sha256 { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
