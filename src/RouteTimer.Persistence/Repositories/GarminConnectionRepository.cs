using Microsoft.EntityFrameworkCore;
using RouteTimer.Persistence.Entities;
using RouteTimer.Services.Garmin;
using RouteTimer.Services.Persistence;

namespace RouteTimer.Persistence.Repositories;

public sealed class GarminConnectionRepository(RouteTimerDbContext context) : IGarminConnectionRepository
{
    private const int ConnectionId = 1;

    public async Task<GarminConnectionRecord?> GetAsync(CancellationToken cancellationToken)
    {
        var entity = await context.GarminConnections
            .AsNoTracking()
            .SingleOrDefaultAsync(connection => connection.Id == ConnectionId, cancellationToken);
        return entity is null ? null : ToRecord(entity);
    }

    public async Task SaveAsync(GarminConnectionRecord connection, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(connection.Token);

        var entity = await context.GarminConnections
            .SingleOrDefaultAsync(saved => saved.Id == ConnectionId, cancellationToken);
        if (entity is null)
        {
            entity = new GarminConnectionEntity { Id = ConnectionId };
            context.GarminConnections.Add(entity);
        }

        entity.State = connection.State;
        entity.GarminUserId = connection.GarminUserId;
        entity.DisplayName = connection.DisplayName;
        entity.EncryptionVersion = connection.Token.Version;
        entity.Nonce = connection.Token.Nonce.ToArray();
        entity.Ciphertext = connection.Token.Ciphertext.ToArray();
        entity.Tag = connection.Token.Tag.ToArray();
        entity.LastValidatedAt = connection.LastValidatedAt;
        entity.UpdatedAt = connection.UpdatedAt;
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAsync(CancellationToken cancellationToken)
    {
        var entity = await context.GarminConnections
            .SingleOrDefaultAsync(connection => connection.Id == ConnectionId, cancellationToken);
        if (entity is null)
        {
            return;
        }

        context.GarminConnections.Remove(entity);
        await context.SaveChangesAsync(cancellationToken);
    }

    private static GarminConnectionRecord ToRecord(GarminConnectionEntity entity) =>
        new(
            entity.State,
            entity.GarminUserId,
            entity.DisplayName,
            new ProtectedGarminToken(
                entity.EncryptionVersion,
                entity.Nonce.ToArray(),
                entity.Ciphertext.ToArray(),
                entity.Tag.ToArray()),
            entity.LastValidatedAt,
            entity.UpdatedAt);
}
