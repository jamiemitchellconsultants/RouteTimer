using Microsoft.EntityFrameworkCore;
using RouteTimer.Persistence.Entities;
using RouteTimer.Services.Persistence;
using RouteTimer.Services.Security;

namespace RouteTimer.Persistence.Repositories;

public sealed class GoogleMapsCredentialRepository(RouteTimerDbContext context) : IGoogleMapsCredentialRepository
{
    private const int CredentialId = 1;

    public async Task<GoogleMapsCredentialRecord?> GetAsync(CancellationToken cancellationToken)
    {
        var entity = await context.GoogleMapsCredentials
            .AsNoTracking()
            .SingleOrDefaultAsync(credential => credential.Id == CredentialId, cancellationToken);
        return entity is null ? null : ToRecord(entity);
    }

    public async Task SaveAsync(GoogleMapsCredentialRecord credential, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentNullException.ThrowIfNull(credential.Secret);

        var entity = await context.GoogleMapsCredentials
            .SingleOrDefaultAsync(saved => saved.Id == CredentialId, cancellationToken);
        if (entity is null)
        {
            entity = new GoogleMapsCredentialEntity { Id = CredentialId };
            context.GoogleMapsCredentials.Add(entity);
        }

        entity.EncryptionVersion = credential.Secret.Version;
        entity.Nonce = credential.Secret.Nonce.ToArray();
        entity.Ciphertext = credential.Secret.Ciphertext.ToArray();
        entity.Tag = credential.Secret.Tag.ToArray();
        entity.KeyHint = credential.KeyHint;
        entity.UpdatedAt = credential.UpdatedAt;
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAsync(CancellationToken cancellationToken)
    {
        var entity = await context.GoogleMapsCredentials
            .SingleOrDefaultAsync(credential => credential.Id == CredentialId, cancellationToken);
        if (entity is null)
        {
            return;
        }

        context.GoogleMapsCredentials.Remove(entity);
        await context.SaveChangesAsync(cancellationToken);
    }

    private static GoogleMapsCredentialRecord ToRecord(GoogleMapsCredentialEntity entity) =>
        new(
            new ProtectedSecret(
                entity.EncryptionVersion,
                entity.Nonce.ToArray(),
                entity.Ciphertext.ToArray(),
                entity.Tag.ToArray()),
            entity.KeyHint,
            entity.UpdatedAt);
}
