using Microsoft.EntityFrameworkCore;
using RouteTimer.Persistence.Entities;
using RouteTimer.Services.Persistence;

namespace RouteTimer.Persistence.Repositories;

public sealed class LocalCredentialRepository(RouteTimerDbContext context) : ILocalCredentialRepository
{
    private const int SingletonId = 1;

    public async Task<string?> GetAsync(CancellationToken cancellationToken)
    {
        var credential = await context.LocalCredentials.SingleOrDefaultAsync(cancellationToken);
        return credential?.PasswordHash;
    }

    public async Task SetAsync(string passwordHash, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(passwordHash);

        var now = DateTimeOffset.UtcNow;
        var credential = await context.LocalCredentials.SingleOrDefaultAsync(cancellationToken);
        if (credential is null)
        {
            context.LocalCredentials.Add(new LocalCredentialEntity
            {
                Id = SingletonId,
                PasswordHash = passwordHash,
                CreatedAt = now,
                UpdatedAt = now
            });
        }
        else
        {
            credential.PasswordHash = passwordHash;
            credential.UpdatedAt = now;
        }

        await context.SaveChangesAsync(cancellationToken);
    }
}
