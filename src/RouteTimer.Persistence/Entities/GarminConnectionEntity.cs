namespace RouteTimer.Persistence.Entities;

public sealed class GarminConnectionEntity
{
    public int Id { get; set; }
    public string State { get; set; } = "connected";
    public string? GarminUserId { get; set; }
    public string? DisplayName { get; set; }
    public int EncryptionVersion { get; set; }
    public byte[] Nonce { get; set; } = [];
    public byte[] Ciphertext { get; set; } = [];
    public byte[] Tag { get; set; } = [];
    public DateTimeOffset? LastValidatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
