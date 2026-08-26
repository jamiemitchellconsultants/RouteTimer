namespace RouteTimer.Persistence.Entities;

public sealed class LocalCredentialEntity
{
    public int Id { get; set; }
    public string PasswordHash { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
