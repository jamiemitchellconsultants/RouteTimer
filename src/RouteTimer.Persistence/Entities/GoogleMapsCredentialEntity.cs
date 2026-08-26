namespace RouteTimer.Persistence.Entities;

public sealed class GoogleMapsCredentialEntity
{
    public int Id { get; set; }
    public int EncryptionVersion { get; set; }
    public byte[] Nonce { get; set; } = [];
    public byte[] Ciphertext { get; set; } = [];
    public byte[] Tag { get; set; } = [];
    public string KeyHint { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; set; }
}
